mod support;

use std::io::{Cursor, Write};
use std::process::{Command, Stdio};
use braid_ferrule_host::interchange::{self, Request, Response};
use braid_ferrule_host::protocol::ENGINE_REVISION;

fn request(operation: &str, payload: &[u8]) -> Vec<u8> {
    support::framed_bytes(
        &serde_json::to_vec(&Request {
            protocol_version: interchange::PROTOCOL.into(),
            request_id: "11111111-1111-4111-8111-111111111111".into(),
            operation: operation.into(),
            input_digest: support::digest(payload),
            design_path: "maps/design.mfd".into(),
            engine_revision: ENGINE_REVISION.into(),
        })
        .unwrap(),
        payload,
    )
}
fn run(input: &[u8]) -> (Response, Vec<u8>) {
    let mut output = Vec::new();
    braid_ferrule_host::handle_stream(&mut Cursor::new(input), &mut output).unwrap();
    parse(&output)
}
fn parse(output: &[u8]) -> (Response, Vec<u8>) {
    let mut reader = Cursor::new(output);
    let header =
        braid_ferrule_host::framing::read_frame(&mut reader, interchange::MAX_HEADER).unwrap();
    let response: Response = serde_json::from_slice(&header).unwrap();
    let payload = if response.payload_bytes == 0 {
        Vec::new()
    } else {
        braid_ferrule_host::framing::read_frame(&mut reader, interchange::MAX_PACKAGE).unwrap()
    };
    braid_ferrule_host::framing::require_eof(&mut reader).unwrap();
    if !payload.is_empty() {
        assert_eq!(response.payload_digest, Some(support::digest(&payload)));
        assert_eq!(response.payload_bytes, payload.len());
    }
    (response, payload)
}

#[test]
fn self_authored_native_exports_assets_and_imports_exact_package_without_io() {
    let (export, package) = run(&request("export", support::BASIC));
    assert_eq!(export.status, "succeeded");
    assert_eq!(export.engine_revision, ENGINE_REVISION);
    let decoded = interchange::decode_package(&package).unwrap();
    assert!(decoded.entries().len() > 1);
    let (import, native) = run(&request("import", &package));
    assert_eq!(import.status, "succeeded");
    assert_eq!(import.engine_revision, ENGINE_REVISION);
    assert!(import.summary.contains("graph nodes"));
    assert!(import.summary.contains("Source"));
    assert!(import.summary.contains("Target"));
    let original = braid_ferrule_host::decode::decode_native(support::BASIC).unwrap();
    let roundtrip = braid_ferrule_host::decode::decode_native(&native).unwrap();
    assert_eq!(original.project.source, roundtrip.project.source);
    assert_eq!(original.project.target, roundtrip.project.target);
    use ferrule_ir::{Instance, Value};
    let input = Instance::Group(
        vec![
            (
                "code".into(),
                Instance::Scalar(Value::String("self-authored λ".into())),
            ),
            (
                "a.b".into(),
                Instance::Scalar(Value::String("dotted".into())),
            ),
            (
                "nested".into(),
                Instance::Group(
                    vec![("b".into(), Instance::Scalar(Value::String("nested".into())))].into(),
                ),
            ),
        ]
        .into(),
    );
    assert_eq!(
        ferrule_engine::run(&original.project, &input).unwrap(),
        ferrule_engine::run(&roundtrip.project, &input).unwrap()
    );
}

#[test]
fn missing_asset_trailing_bytes_and_malformed_package_refuse_without_fallback() {
    let (_, package) = run(&request("export", support::BASIC));
    let decoded = interchange::decode_package(&package).unwrap();
    let design = decoded.get(decoded.design_path()).unwrap();
    let incomplete =
        ferrule_mfd::MfdPackage::new(decoded.design_path(), [(decoded.design_path(), design)])
            .unwrap();
    assert_ne!(
        run(&request(
            "import",
            &interchange::encode_package(&incomplete)
        ))
        .0
        .status,
        "succeeded"
    );
    let mut trailing = request("import", &package);
    trailing.push(0);
    assert_eq!(run(&trailing).0.status, "invalidFrame");
    assert_ne!(run(&request("import", b"invalid")).0.status, "succeeded");
}

#[test]
fn maximum_aggregate_package_is_admitted_and_overflow_is_refused() {
    let (_, bytes) = run(&request("export", support::BASIC));
    let package = interchange::decode_package(&bytes).unwrap();
    let mut entries = package
        .entries()
        .map(|(p, b)| (p.to_owned(), b.to_vec()))
        .collect::<Vec<_>>();
    let remaining =
        ferrule_mfd::MAX_PACKAGE_BYTES - entries.iter().map(|(_, b)| b.len()).sum::<usize>();
    for (index, size) in (0..4).map(|index| {
        (
            index,
            (remaining - index * 4 * 1024 * 1024).min(4 * 1024 * 1024),
        )
    }) {
        entries.push((format!("unused-{index}.bin"), vec![0; size]));
    }
    let maximum = ferrule_mfd::MfdPackage::new(package.design_path(), entries.clone()).unwrap();
    assert_eq!(
        run(&request("import", &interchange::encode_package(&maximum)))
            .0
            .status,
        "succeeded"
    );
    entries.push(("overflow.bin".into(), vec![1]));
    assert!(ferrule_mfd::MfdPackage::new(package.design_path(), entries).is_err());
}

#[test]
fn real_service_sandbox_converts_package_without_filesystem_or_network() {
    let (_, package) = run(&request("export", support::BASIC));
    support::close_inherited_descriptors_on_exec();
    let mut child = Command::new(env!("CARGO_BIN_EXE_braid-ferrule-host"))
        .args([
            "--service-sandbox",
            "--cpu-seconds=30",
            "--memory-bytes=536870912",
            "--open-files=64",
        ])
        .env_clear()
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    child
        .stdin
        .take()
        .unwrap()
        .write_all(&request("import", &package))
        .unwrap();
    let output = child.wait_with_output().unwrap();
    assert!(output.status.success(), "{:?}", output.stderr);
    assert_eq!(&output.stdout[..4], b"BFS1");
    assert_eq!(parse(&output.stdout[4..]).0.status, "succeeded");
}
