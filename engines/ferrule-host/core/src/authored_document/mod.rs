//! In-memory execution of an unsaved native proposal. No revision or host transport.
//! Deadlines are cooperative; this library is not a process resource sandbox.
mod admission;
mod codecs;
mod metadata;
pub mod transport;
pub use metadata::{CsvDialect, Dialect};
#[cfg(test)]
mod tests;

use ferrule_engine::{
    DebugDecision, DebugHook, ExecutionBudgets, ExecutionContext, ExecutionPolicy,
    PendingNodeValue, PendingTargetWrite, RestrictedPolicy,
};
use ferrule_ir::{Instance, Value};
use serde::Serialize;
use sha2::{Digest, Sha256};
use std::{
    cell::Cell,
    path::Path,
    time::{Duration, Instant},
};

pub const MAX_DOCUMENT_BYTES: usize = 8 * 1024 * 1024;
pub const MAX_NODES: usize = 100_000;
pub const MAX_DEPTH: usize = 64;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum Failure {
    InvalidLimits,
    NativeLimit,
    InvalidNative,
    UnsupportedBoundary,
    UnsupportedSchema,
    UnsupportedOperator,
    UnsupportedScope,
    NamedEndpoints,
    ExternalDependency,
    InvalidMapping,
    InputLimit,
    InvalidInput,
    ExecutionLimit,
    ExecutionFailed,
    OutputLimit,
    InvalidOutput,
    DeadlineExceeded,
}

#[derive(Clone, Copy, Debug)]
pub struct Limits {
    pub input_bytes: usize,
    pub output_bytes: usize,
    pub work: u64,
    pub elapsed: Duration,
}
impl Default for Limits {
    fn default() -> Self {
        Self {
            input_bytes: MAX_DOCUMENT_BYTES,
            output_bytes: MAX_DOCUMENT_BYTES,
            work: 100_000,
            elapsed: Duration::from_secs(5),
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Format {
    Json,
    Xml,
    Csv,
    Tsv,
    X12,
    Edifact,
}

impl Format {
    pub const fn id(self) -> &'static str {
        match self {
            Self::Json => "json",
            Self::Xml => "xml",
            Self::Csv => "csv",
            Self::Tsv => "tsv",
            Self::X12 => "x12",
            Self::Edifact => "edifact",
        }
    }

    pub const fn media_type(self) -> &'static str {
        match self {
            Self::Json => "application/json",
            Self::Xml => "application/xml",
            Self::Csv => "text/csv",
            Self::Tsv => "text/tab-separated-values",
            Self::X12 => "application/edi-x12",
            Self::Edifact => "application/edifact",
        }
    }
}

/// Digests bind the exact captured native/sample bytes and validated result.
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Output {
    pub engine_revision: &'static str,
    pub native_sha256: String,
    pub sample_sha256: String,
    pub output_sha256: String,
    pub input_format: Format,
    pub output_format: Format,
    pub dialect: Dialect,
    pub bytes: Vec<u8>,
}

struct Guard {
    deadline: Instant,
    writes: Cell<usize>,
    exhausted: Cell<bool>,
    retained_bytes: Cell<usize>,
    largest_scalar: Cell<usize>,
}
impl Guard {
    fn check(&self) -> Result<(), Failure> {
        if Instant::now() >= self.deadline {
            Err(Failure::DeadlineExceeded)
        } else {
            Ok(())
        }
    }
    fn decision(&self) -> DebugDecision {
        if self.check().is_err() || self.exhausted.get() {
            DebugDecision::Cancel
        } else {
            DebugDecision::Resume
        }
    }
}
impl DebugHook for Guard {
    fn before_target_write(&self, write: &PendingTargetWrite) -> DebugDecision {
        self.writes.set(self.writes.get() + 1);
        if self.writes.get() > MAX_NODES {
            self.exhausted.set(true);
        }
        let scalar = write.pending.value.as_ref().map_or(0, |v| {
            if v.truncated {
                self.largest_scalar.get()
            } else {
                v.preview.len()
            }
        });
        self.retained_bytes
            .set(self.retained_bytes.get().saturating_add(scalar + 256));
        if self.retained_bytes.get() > MAX_DOCUMENT_BYTES {
            self.exhausted.set(true);
        }
        self.decision()
    }
    fn wants_node_inputs(&self) -> bool {
        false
    }
    fn after_node_value(&self, _: &PendingNodeValue) -> DebugDecision {
        self.decision()
    }
}

/// Atomic success: bytes are returned only after codec reparse and envelope checks.
/// Error categories never contain source values, native text, paths or parser errors.
pub fn execute(native: &[u8], sample: &[u8], limits: Limits) -> Result<Output, Failure> {
    if limits.input_bytes == 0
        || limits.input_bytes > MAX_DOCUMENT_BYTES
        || limits.output_bytes == 0
        || limits.output_bytes > MAX_DOCUMENT_BYTES
        || limits.work == 0
        || limits.work > 100_000
        || limits.elapsed.is_zero()
        || limits.elapsed > Duration::from_secs(5)
    {
        return Err(Failure::InvalidLimits);
    }
    let guard = Guard {
        deadline: Instant::now() + limits.elapsed,
        writes: Cell::new(0),
        exhausted: Cell::new(false),
        retained_bytes: Cell::new(0),
        largest_scalar: Cell::new(32),
    };
    if native.len() > crate::protocol::MAX_NATIVE_BYTES {
        return Err(Failure::NativeLimit);
    }
    if sample.len() > limits.input_bytes {
        return Err(Failure::InputLimit);
    }
    let project = crate::decode::decode_native(native)
        .map_err(|_| Failure::InvalidNative)?
        .project;
    let (input_format, output_format) = admission::admit(native, &project)?;
    guard.check()?;
    let source = codecs::read(
        input_format,
        sample,
        &project.source,
        &project.source_options,
    )?;
    instance_bound(&source).map_err(|_| Failure::InputLimit)?;
    let mut pending = vec![&source];
    let mut source_nodes = 0usize;
    while let Some(value) = pending.pop() {
        source_nodes += 1;
        match value {
            Instance::Scalar(Value::String(s)) => guard
                .largest_scalar
                .set(guard.largest_scalar.get().max(s.len())),
            Instance::Group(fields) => pending.extend(fields.iter().map(|(_, v)| v)),
            Instance::Repeated(items) => pending.extend(items),
            _ => (),
        }
    }
    for node in project.graph.nodes.values() {
        if let ferrule_mapping::Node::Const {
            value: Value::String(s),
        } = node
        {
            guard
                .largest_scalar
                .set(guard.largest_scalar.get().max(s.len()));
        }
    }
    let mut scopes = vec![&project.root];
    let mut scope_count = 0usize;
    while let Some(scope) = scopes.pop() {
        scope_count += 1;
        scopes.extend(&scope.children);
    }
    // Admission permits only one source iteration on any root-to-leaf path.
    // This conservative bound also covers empty-binding scopes (no write hook).
    if source_nodes.saturating_mul(scope_count) > MAX_NODES {
        return Err(Failure::ExecutionLimit);
    }
    guard.check()?;
    let policy = ExecutionPolicy::Restricted(RestrictedPolicy::new(ExecutionBudgets::new(
        limits.work,
        MAX_DEPTH as u64,
        0,
    )));
    // The engine requires a lexical path context. Empty is never resolved or
    // observable: runtime/path nodes and every acquisition operation are refused.
    let context = ExecutionContext::new(Path::new(""))
        .with_policy(&policy)
        .with_debug_hook(&guard);
    let result = ferrule_engine::run_with_context(&project, &source, &context);
    guard.check()?;
    if guard.exhausted.get() {
        return Err(Failure::ExecutionLimit);
    }
    let result = result.map_err(|error| match error {
        ferrule_engine::EngineError::ExecutionBudgetExceeded { .. } => Failure::ExecutionLimit,
        _ => Failure::ExecutionFailed,
    })?;
    instance_bound(&result).map_err(|_| Failure::OutputLimit)?;
    let bytes = codecs::write(
        output_format,
        &result,
        &project.target,
        &project.target_options,
        limits.output_bytes,
    )?;
    guard.check()?;
    if bytes.len() > limits.output_bytes {
        return Err(Failure::OutputLimit);
    }
    let validated = codecs::read(
        output_format,
        &bytes,
        &project.target,
        &project.target_options,
    )
    .map_err(|e| {
        if e == Failure::InputLimit {
            Failure::OutputLimit
        } else {
            Failure::InvalidOutput
        }
    })?;
    instance_bound(&validated).map_err(|_| Failure::OutputLimit)?;
    guard.check()?;
    let dialect = metadata::describe(
        output_format,
        &project.target,
        &project.target_options,
        &bytes,
    )?;
    guard.check()?;
    let output = Output {
        engine_revision: crate::protocol::ENGINE_REVISION,
        native_sha256: digest(native),
        sample_sha256: digest(sample),
        output_sha256: digest(&bytes),
        input_format,
        output_format,
        dialect,
        bytes,
    };
    guard.check()?;
    Ok(output)
}

fn digest(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}

// Bounds the codec's owned intermediate tree and rendering inputs, independently
// of the final byte cap. No recursive/dynamic schemas or copy constructors enter.
fn instance_bound(instance: &Instance) -> Result<(), ()> {
    let mut pending = vec![(instance, 0)];
    let mut nodes = 0usize;
    let mut bytes = 0usize;
    while let Some((value, depth)) = pending.pop() {
        nodes += 1;
        if nodes > MAX_NODES || depth > MAX_DEPTH {
            return Err(());
        }
        match value {
            Instance::Scalar(Value::String(text)) => bytes += text.len(),
            Instance::Scalar(_) => bytes += 32,
            Instance::Group(fields) => {
                for (name, child) in fields {
                    bytes += name.len();
                    pending.push((child, depth + 1));
                }
            }
            Instance::Repeated(items) => pending.extend(items.iter().map(|v| (v, depth + 1))),
            _ => return Err(()),
        }
        if bytes > MAX_DOCUMENT_BYTES {
            return Err(());
        }
    }
    Ok(())
}
