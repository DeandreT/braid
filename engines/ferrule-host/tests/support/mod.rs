#![allow(dead_code)]

use std::io::Cursor;

use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use braid_ferrule_host::protocol::{Limits, PROTOCOL_VERSION, RequestHeader, Revision};

pub const BASIC: &[u8] = include_bytes!("../fixtures/valid-basic.json");
pub const INVALID: &[u8] = include_bytes!("../fixtures/invalid-graph.json");
pub const INVALID_FUNCTION: &[u8] = include_bytes!("../fixtures/invalid-function.json");
pub const EXACT: &[u8] = include_bytes!("../fixtures/versioned-exact.json");
pub const RESOURCES: &[u8] = include_bytes!("../fixtures/resource-metadata.json");

/// Self-authored identity graph for complete EDI envelopes. Fixed leaves are
/// useful when a static compiler requires an explicit envelope version.
pub fn edi_identity_mapping(kind: &str, document: &str, fixed: bool) -> Vec<u8> {
    let delimiter = if kind == "x12" { '*' } else { '+' };
    let terminator = if kind == "x12" { '~' } else { '\'' };
    let mut nodes = serde_json::Map::new();
    let mut segments = Vec::new();
    let mut scopes = Vec::new();
    let mut next = 1;
    let scalar = |name: &str, value: &str| {
        let mut field = json!({"name":name,"kind":{"kind":"scalar","ty":"string"}});
        if fixed {
            field["fixed"] = json!(value);
        }
        field
    };
    for segment in document.split(terminator).filter(|value| !value.is_empty()) {
        let mut values = segment.split(delimiter);
        let tag = values.next().unwrap();
        let mut fields = Vec::new();
        let mut bindings = Vec::new();
        let mut children = Vec::new();
        for (index, value) in values.enumerate() {
            let name = format!("e{}", index + 1);
            if kind == "edifact" && value.contains(':') {
                let mut components = Vec::new();
                let mut component_bindings = Vec::new();
                for (component, value) in value.split(':').enumerate() {
                    let part = format!("c{}", component + 1);
                    components.push(scalar(&part, value));
                    nodes.insert(
                        next.to_string(),
                        json!({"kind":"source_field","path":[tag,name,part]}),
                    );
                    component_bindings.push(json!({"target_field":part,"node":next}));
                    next += 1;
                }
                fields.push(json!({"name":name,"kind":{"kind":"group","children":components}}));
                children
                    .push(json!({"target_field":name,"bindings":component_bindings,"children":[]}));
            } else {
                fields.push(scalar(&name, value));
                nodes.insert(
                    next.to_string(),
                    json!({"kind":"source_field","path":[tag,name]}),
                );
                bindings.push(json!({"target_field":name,"node":next}));
                next += 1;
            }
        }
        segments.push(json!({"name":tag,"kind":{"kind":"group","children":fields}}));
        scopes.push(json!({"target_field":tag,"bindings":bindings,"children":children}));
    }
    let schema = json!({"name":"Interchange","kind":{"kind":"group","children":segments}});
    serde_json::to_vec(&json!({"source":schema,"target":schema,
        "source_options":{"edi_kind":kind},"target_options":{"edi_kind":kind},
        "graph":{"nodes":nodes},"root":{"target_field":"","bindings":[],"children":scopes}}))
    .unwrap()
}

pub fn x12_interchange_mapping(document: &str) -> Vec<u8> {
    let mut mapping: Value =
        serde_json::from_slice(&edi_identity_mapping("x12", document, false)).unwrap();
    let segments = mapping["source"]["kind"]["children"].as_array().unwrap();
    let segment = |name: &str| {
        segments
            .iter()
            .find(|value| value["name"] == name)
            .unwrap()
            .clone()
    };
    let mut isa = segment("ISA");
    isa["repeating"] = json!(true);
    isa["kind"]["children"][11]["fixed"] = json!("00501");
    let mut gs = segment("GS");
    gs["repeating"] = json!(true);
    gs["kind"]["children"][7]["fixed"] = json!("005010");
    let mut ge = segment("GE");
    ge["repeating"] = json!(true);
    let mut iea = segment("IEA");
    iea["repeating"] = json!(true);
    let mut business = segment("BGM");
    business["kind"]["children"].as_array_mut().unwrap().push(json!({"name":"e3","repeating":true,"kind":{"kind":"group","children":[{"name":"c1","kind":{"kind":"scalar","ty":"string"}},{"name":"c2","kind":{"kind":"scalar","ty":"string"}}]}}));
    let schema = json!({"name":"Document","kind":{"kind":"group","children":[{"name":"Interchanges","repeating":true,"kind":{"kind":"group","children":[isa,{"name":"Groups","repeating":true,"kind":{"kind":"group","children":[gs,{"name":"Messages","repeating":true,"kind":{"kind":"group","children":[segment("ST"),business,segment("SE")]}},ge]}},iea]}}]}});
    mapping["source"] = schema.clone();
    mapping["target"] = schema;
    mapping["source_options"] = json!({"edi_kind":"x12","x12_interchange_version":"00501"});
    mapping["target_options"] = json!({"json_document":true});
    mapping["graph"] = json!({"nodes":{}});
    mapping["root"] =
        json!({"target_field":"","construction":"copy_current_source","bindings":[],"children":[]});
    serde_json::to_vec(&mapping).unwrap()
}

pub fn request(operation: &str, native: &[u8]) -> RequestHeader {
    RequestHeader {
        protocol_version: PROTOCOL_VERSION.into(),
        request_id: "11111111-1111-4111-8111-111111111111".into(),
        operation: operation.into(),
        revision: Revision {
            organization_id: "22222222-2222-4222-8222-222222222222".into(),
            project_id: "33333333-3333-4333-8333-333333333333".into(),
            mapping_id: "44444444-4444-4444-8444-444444444444".into(),
            revision_id: "55555555-5555-4555-8555-555555555555".into(),
        },
        native_digest: digest(native),
        document_id: "entry.native.json".into(),
        limits: Limits {
            maximum_diagnostics: 128,
            maximum_items: 8192,
            maximum_text_bytes: 2048,
        },
    }
}

pub fn digest(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}

pub fn process(header: &RequestHeader, native: &[u8]) -> Value {
    serde_json::to_value(braid_ferrule_host::process_request(header, native)).unwrap()
}

/// Gives sandboxed children only the three standard streams, as the service does. A harness such as a CI
/// runner can leave descriptors open in the test process without close-on-exec; the sandbox correctly
/// refuses to start when a child inherits one.
pub fn close_inherited_descriptors_on_exec() {
    use rustix::io::{FdFlags, fcntl_getfd, fcntl_setfd};
    use std::os::fd::BorrowedFd;
    let descriptors: Vec<i32> = std::fs::read_dir("/proc/self/fd")
        .unwrap()
        .filter_map(|entry| entry.ok()?.file_name().to_str()?.parse().ok())
        .filter(|&descriptor| descriptor > 2)
        .collect();
    for descriptor in descriptors {
        // SAFETY: the descriptor is only borrowed for these flag calls, which neither close it nor
        // transfer ownership. One closed meanwhile (such as the listing's own) fails with EBADF.
        let borrowed = unsafe { BorrowedFd::borrow_raw(descriptor) };
        if let Ok(flags) = fcntl_getfd(borrowed) {
            let _ = fcntl_setfd(borrowed, flags | FdFlags::CLOEXEC);
        }
    }
}

pub fn framed_bytes(header: &[u8], native: &[u8]) -> Vec<u8> {
    let mut bytes = Vec::new();
    bytes.extend_from_slice(&(header.len() as u32).to_be_bytes());
    bytes.extend_from_slice(header);
    bytes.extend_from_slice(&(native.len() as u32).to_be_bytes());
    bytes.extend_from_slice(native);
    bytes
}

pub fn stream(bytes: &[u8]) -> Value {
    let mut output = Vec::new();
    braid_ferrule_host::handle_stream(&mut Cursor::new(bytes), &mut output).unwrap();
    response_frame(&output)
}

pub fn response_frame(output: &[u8]) -> Value {
    assert!(output.len() >= 4, "response must contain its length prefix");
    let length = u32::from_be_bytes(output[..4].try_into().unwrap()) as usize;
    assert!(length <= 8 * 1024 * 1024, "response exceeds hard cap");
    assert_eq!(output.len(), 4 + length, "exactly one response frame");
    serde_json::from_slice(&output[4..]).unwrap()
}

pub fn success(response: &Value, header: &RequestHeader, kind: &str) {
    assert!(response["failure"].is_null(), "{response:#}");
    assert_eq!(response["protocolVersion"], PROTOCOL_VERSION);
    assert_eq!(response["requestId"], header.request_id);
    assert_eq!(response["operation"], header.operation);
    assert_eq!(response["nativeDigest"], header.native_digest);
    assert_eq!(response["documentId"], header.document_id);
    assert_eq!(
        response["revision"],
        serde_json::to_value(&header.revision).unwrap()
    );
    assert_eq!(response["result"]["kind"], kind);
}

pub fn failure(response: &Value) -> &str {
    assert!(response["result"].is_null(), "{response:#}");
    let failure = &response["failure"];
    assert!(failure["message"].is_string(), "{response:#}");
    failure["code"].as_str().expect("structured failure code")
}
