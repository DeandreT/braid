mod support;
use serde_json::{Value, json};
use std::io::Cursor;
use support::{BASIC, INVALID, digest};
use weft_ferrule_host::build::{PROFILE, PROTOCOL, Request};

#[test]
fn document_compiler_uses_explicit_formats_and_retains_the_json_byte_api_for_json_only() {
    let mut native: Value = serde_json::from_slice(BASIC).unwrap();
    native["source_options"] = json!({"json_document":true});
    native["target_options"] = json!({"json_document":true});
    let native = serde_json::to_vec(&native).unwrap();
    let mut r = request(&native);
    r.profile = weft_ferrule_host::build::DOCUMENT_PROFILE.into();
    let (boundaries, files) = weft_ferrule_host::build::generate(&r, &native).unwrap();
    assert!(boundaries.iter().all(|value| value.format == "json"));
    assert!(files.iter().any(|file| {
        String::from_utf8_lossy(&file.contents).contains("ExecuteJsonBytesOutputsWithSources")
    }));
    let mut guessed = request(BASIC);
    guessed.profile = r.profile.clone();
    assert_eq!(
        weft_ferrule_host::build::generate(&guessed, BASIC).err(),
        Some("unsupported")
    );
}

#[test]
fn document_compiler_refuses_a_non_edi_schema_relabelled_as_edifact() {
    let mut native: Value = serde_json::from_slice(BASIC).unwrap();
    native["source_options"] = json!({"edi_kind":"edifact"});
    native["target_options"] = json!({"json_document":true});
    let native = serde_json::to_vec(&native).unwrap();
    let mut r = request(&native);
    r.profile = weft_ferrule_host::build::DOCUMENT_PROFILE.into();
    assert_eq!(
        weft_ferrule_host::build::generate(&r, &native).err(),
        Some("unsupported")
    );
}

#[test]
fn mapforce_interchanges_require_the_explicit_compiler_profile() {
    let document = "ISA*00*          *00*          *ZZ*WEFTSENDER     *ZZ*WEFTRECEIVER   *261024*1200*^*00501*000000330*0*T*:~GS*PO*WEFTSENDER*WEFTRECEIVER*20261024*1200*330*X*005010~ST*850*0330~BGM*220*WEFT-330~SE*3*0330~GE*1*330~IEA*1*000000330~";
    let native = support::x12_interchange_mapping(document);
    let mut request = request(&native);
    request.profile = weft_ferrule_host::build::DOCUMENT_PROFILE.into();
    assert_eq!(
        weft_ferrule_host::build::generate(&request, &native).err(),
        Some("unsupported")
    );
    request.profile = weft_ferrule_host::build::X12_INTERCHANGES_PROFILE.into();
    let (_, files) = weft_ferrule_host::build::generate(&request, &native).unwrap();
    assert!(
        files
            .iter()
            .any(|file| String::from_utf8_lossy(&file.contents).contains("mapforce_interchanges"))
    );
}

#[test]
fn document_compiler_retains_exact_output_syntax_without_weakening_source_admission() {
    let mut native: Value = serde_json::from_slice(BASIC).unwrap();
    native["source_options"] = json!({"json_document":true});
    native["target_options"] = json!({"json_document":true});
    native["extra_targets"] = json!([{"name":"quote\"line\u{2028}","schema":native["target"],"root":native["root"],"options":{"json_document":true}}]);
    let native = serde_json::to_vec(&native).unwrap();
    let mut r = request(&native);
    r.profile = weft_ferrule_host::build::DOCUMENT_PROFILE.into();
    let (_, files) = weft_ferrule_host::build::generate(&r, &native).unwrap();
    let metadata = files
        .iter()
        .find(|file| file.path.as_str() == "Runtime/WeftDocumentSyntax.cs")
        .unwrap();
    let text = std::str::from_utf8(&metadata.contents).unwrap();
    assert!(text.contains("if (name == null) return 126;"));
    assert!(text.contains("\\u0022"));
    assert!(text.contains("\\u2028"));
    assert!(!text.contains('\u{2028}'));
}

#[test]
fn explicit_edifact_compilation_uses_strict_shared_document_companions() {
    let source = "UNB+UNOC:3+WEFTSENDER+WEFTRECEIVER+261024:1200+330'UNH+330+ORDERS:D:96A:UN'BGM+220+WEFT-330'UNT+3+330'UNZ+1+330'";
    let native = support::edi_identity_mapping("edifact", source, true);
    let mut r = request(&native);
    r.profile = weft_ferrule_host::build::DOCUMENT_PROFILE.into();
    let (boundaries, files) = weft_ferrule_host::build::generate(&r, &native).unwrap();
    assert!(
        boundaries
            .iter()
            .all(|boundary| boundary.format == "edifact")
    );
    let companions = files
        .iter()
        .map(|file| String::from_utf8_lossy(&file.contents))
        .collect::<Vec<_>>();
    assert!(
        companions
            .iter()
            .any(|text| text.contains("ExecuteDocumentBytesOutputsWithHost"))
    );
    assert!(
        companions
            .iter()
            .any(|text| text.contains("strictEnvelope: true"))
    );
    assert_eq!(
        weft_ferrule_host::build::generate(&request(&native), &native).err(),
        Some("unsupported")
    );
}

#[test]
fn explicit_x12_compilation_emits_format_bearing_document_companions() {
    let document = "ISA*00*          *00*          *ZZ*WEFTSENDER     *ZZ*WEFTRECEIVER   *261024*1200*^*00501*000000330*0*T*:~GS*PO*WEFTSENDER*WEFTRECEIVER*20261024*1200*330*X*005010~ST*850*0330~BEG*00*SA*WEFT-330~SE*3*0330~GE*1*330~IEA*1*000000330~";
    let mut segments = Vec::new();
    let mut scopes = Vec::new();
    let mut nodes = serde_json::Map::new();
    let mut next = 1;
    for segment in document.split('~').filter(|value| !value.is_empty()) {
        let mut elements = segment.split('*');
        let tag = elements.next().unwrap();
        let mut fields = Vec::new();
        let mut bindings = Vec::new();
        for (index, value) in elements.enumerate() {
            let name = format!("e{}", index + 1);
            fields.push(json!({"name":name,"fixed":value,"kind":{"kind":"scalar","ty":"string"}}));
            nodes.insert(
                next.to_string(),
                json!({"kind":"source_field","path":[tag,name]}),
            );
            bindings.push(json!({"target_field":name,"node":next}));
            next += 1;
        }
        segments.push(json!({"name":tag,"kind":{"kind":"group","children":fields}}));
        scopes.push(json!({"target_field":tag,"bindings":bindings,"children":[]}));
    }
    let schema = json!({"name":"Document","kind":{"kind":"group","children":segments}});
    let native = serde_json::to_vec(&json!({
        "source":schema,"target":schema,"source_options":{"edi_kind":"x12"},"target_options":{"json_document":true},
        "graph":{"nodes":nodes},
        "root":{"target_field":"","bindings":[],"children":scopes}
    })).unwrap();
    let mut r = request(&native);
    r.profile = weft_ferrule_host::build::DOCUMENT_PROFILE.into();
    let project: ferrule_mapping::Project = serde_json::from_slice(&native).unwrap();
    let program = ferrule_codegen::lower(&project).unwrap();
    let emitted = ferrule_codegen_csharp::emit_with_static_document_adapters(
        &program,
        &ferrule_codegen::StaticDocumentBoundaryPolicy {
            source: ferrule_codegen::DocumentBoundaryOptions::X12(
                ferrule_codegen::X12BoundaryOptions::from_format_options(&project.source_options)
                    .unwrap(),
            ),
            target: ferrule_codegen::DocumentBoundaryOptions::Json,
            extra_sources: vec![],
            extra_targets: vec![],
        },
    );
    assert!(emitted.is_ok(), "{:?}", emitted.err());
    let (boundaries, files) = weft_ferrule_host::build::generate(&r, &native).unwrap();
    assert_eq!(boundaries[0].format, "x12");
    assert_eq!(boundaries[1].format, "json");
    assert!(files.iter().any(|file| {
        String::from_utf8_lossy(&file.contents).contains("ExecuteDocumentBytesOutputsWithHost")
    }));
    assert_eq!(
        weft_ferrule_host::build::generate(&request(&native), &native).err(),
        Some("unsupported")
    );
}

fn request(native: &[u8]) -> Request {
    let reference = support::request("inspect", native);
    Request {
        protocol_version: PROTOCOL.into(),
        request_id: reference.request_id,
        revision: reference.revision,
        native_digest: digest(native),
        intent_digest: "a".repeat(64),
        operation: "compile".into(),
        profile: PROFILE.into(),
    }
}
fn header(native: &[u8]) -> Value {
    let r = request(native);
    json!({"protocolVersion":r.protocol_version,"requestId":r.request_id,"revision":r.revision,
        "nativeDigest":r.native_digest,"intentDigest":r.intent_digest,"operation":r.operation,"profile":r.profile})
}

#[test]
fn native_codegen_emits_complete_bounded_deterministic_sources() {
    let a = weft_ferrule_host::build::generate(&request(BASIC), BASIC).unwrap();
    let b = weft_ferrule_host::build::generate(&request(BASIC), BASIC).unwrap();
    assert_eq!(a.1, b.1);
    assert!(a.1.iter().any(|f| f.path.as_str() == "GeneratedMapping.cs"));
    assert!(
        a.1.iter()
            .any(|f| f.path.as_str() == "Ferrule.Generated.csproj")
    );
    assert!(a.1.len() > 3 && a.1.len() <= 128);
    assert!(
        a.1.iter().map(|f| f.contents.len()).sum::<usize>()
            <= weft_ferrule_host::build::MAX_SOURCE_BYTES
    );
    assert_eq!(a.0.len(), 2);
    assert_eq!(a.0[0].kind, "primaryInput");
    assert_eq!(a.0[1].kind, "primaryOutput");
}
#[test]
fn unsupported_physical_input_does_not_fall_back_to_json() {
    let native = include_bytes!("fixtures/preview-csv-source.json");
    assert_eq!(
        weft_ferrule_host::build::generate(&request(native), native).err(),
        Some("unsupported")
    );
}
#[test]
fn malformed_mapping_is_not_build_support() {
    assert!(weft_ferrule_host::build::generate(&request(INVALID), INVALID).is_err());
}
#[test]
fn digest_and_profile_substitution_refuse_before_generation() {
    let mut r = request(BASIC);
    r.native_digest = "b".repeat(64);
    assert_eq!(
        weft_ferrule_host::build::generate(&r, BASIC).err(),
        Some("invalidRequest")
    );
    r = request(BASIC);
    r.profile = "unknown".into();
    assert_eq!(
        weft_ferrule_host::build::generate(&r, BASIC).err(),
        Some("invalidRequest")
    );
}
#[test]
fn framed_reply_hashes_every_source_and_ends_at_declared_bytes() {
    let mut input = support::framed_bytes(&serde_json::to_vec(&header(BASIC)).unwrap(), BASIC);
    let mut output = Vec::new();
    weft_ferrule_host::handle_stream(&mut Cursor::new(&input), &mut output).unwrap();
    let mut cursor = Cursor::new(&output);
    let raw = weft_ferrule_host::framing::read_frame(&mut cursor, 128 * 1024).unwrap();
    let reply: Value = serde_json::from_slice(&raw).unwrap();
    assert_eq!(reply["status"], "supported");
    assert_eq!(reply["intentDigest"], "a".repeat(64));
    for f in reply["files"].as_array().unwrap() {
        let content =
            weft_ferrule_host::framing::read_frame(&mut cursor, 32 * 1024 * 1024).unwrap();
        assert_eq!(f["byteCount"], content.len());
        assert_eq!(f["sha256"], digest(&content));
    }
    assert_eq!(cursor.position() as usize, output.len());
    input.push(1);
    output.clear();
    weft_ferrule_host::handle_stream(&mut Cursor::new(input), &mut output).unwrap();
    let reply = support::response_frame(&output);
    assert_eq!(reply["status"], "invalidRequest");
    assert!(reply["files"].as_array().unwrap().is_empty());
}
