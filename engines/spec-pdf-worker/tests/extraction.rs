use sha2::{Digest, Sha256};
use weft_spec_pdf_worker::{MAX_INPUT_BYTES, MAX_RESULT_BYTES, PageText, Status, encode, extract};

// A real minimal PDF with xref offsets and one separately addressable stream per page.
fn pdf(texts: &[&str]) -> Vec<u8> {
    let mut objects = vec![
        "<< /Type /Catalog /Pages 2 0 R >>".to_string(),
        format!(
            "<< /Type /Pages /Count {} /Kids [{}] >>",
            texts.len(),
            (0..texts.len())
                .map(|n| format!("{} 0 R", 4 + n * 2))
                .collect::<Vec<_>>()
                .join(" ")
        ),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>".to_string(),
    ];
    for (n, text) in texts.iter().enumerate() {
        objects.push(format!("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {} 0 R >>", 5 + n * 2));
        let stream = if text.is_empty() {
            String::new()
        } else {
            format!("BT /F1 12 Tf 30 700 Td ({text}) Tj ET")
        };
        objects.push(format!(
            "<< /Length {} >>\nstream\n{stream}\nendstream",
            stream.len()
        ));
    }
    let mut data = b"%PDF-1.4\n".to_vec();
    let mut offsets = vec![0];
    for (i, object) in objects.iter().enumerate() {
        offsets.push(data.len());
        data.extend_from_slice(format!("{} 0 obj\n{object}\nendobj\n", i + 1).as_bytes());
    }
    let xref = data.len();
    data.extend_from_slice(format!("xref\n0 {}\n0000000000 65535 f \n", offsets.len()).as_bytes());
    for offset in offsets.iter().skip(1) {
        data.extend_from_slice(format!("{offset:010} 00000 n \n").as_bytes());
    }
    data.extend_from_slice(
        format!(
            "trailer\n<< /Size {} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n",
            offsets.len()
        )
        .as_bytes(),
    );
    data
}

#[test]
fn real_pages_preserve_empty_page_and_source_identity() {
    let bytes = pdf(&["first", "", "third"]);
    let result = extract(&bytes);
    assert_eq!(result.status, Status::Partial);
    assert_eq!(
        result.source_sha256,
        format!("{:x}", Sha256::digest(&bytes))
    );
    assert_eq!(result.source_byte_count, bytes.len());
    assert_eq!(result.pages.len(), 3);
    assert_eq!(result.pages[0].text, "first");
    assert!(result.pages[1].missing_text);
    assert_eq!(result.pages[2].page, 3);
    assert_eq!(result.pages[2].text, "third");
}

#[test]
fn no_usable_text_is_explicit_not_successful_empty_extraction() {
    let result = extract(&pdf(&["", ""]));
    assert_eq!(result.status, Status::NeedsOcr);
    assert_eq!(result.pages.len(), 2);
    assert!(result.pages.iter().all(|p| p.missing_text));
}

#[test]
fn malformed_and_oversized_input_are_atomic() {
    for bytes in [Vec::new(), b"not a PDF; private data".to_vec()] {
        let result = extract(&bytes);
        assert_eq!(result.status, Status::Malformed);
        assert!(result.pages.is_empty());
        assert!(
            !String::from_utf8(encode(&result).unwrap())
                .unwrap()
                .contains("private data")
        );
    }
    let result = extract(&vec![b' '; MAX_INPUT_BYTES + 1]);
    assert_eq!(result.status, Status::LimitExceeded);
    assert!(result.pages.is_empty());
}

#[test]
fn serialized_expansion_fails_atomically_without_changing_source_identity() {
    let mut result = extract(&pdf(&["hello"]));
    let digest = result.source_sha256.clone();
    result.pages = vec![PageText {
        page: 1,
        text: "\0".repeat(MAX_RESULT_BYTES / 2),
        missing_text: false,
    }];
    let bytes = encode(&result).unwrap();
    assert!(bytes.len() < 1024);
    let value: serde_json::Value = serde_json::from_slice(&bytes).unwrap();
    assert_eq!(value["sourceSha256"], digest);
    assert_eq!(value["status"], "limitExceeded");
    assert_eq!(value["pages"], serde_json::json!([]));
}

#[test]
fn actual_pdf_page_limit_is_enforced() {
    let result = extract(&pdf(&vec![""; 257]));
    assert_eq!(result.status, Status::LimitExceeded);
    assert!(result.pages.is_empty());
}

#[test]
fn process_protocol_returns_only_bound_source_and_page_evidence() {
    use std::io::Write;
    use std::process::{Command, Stdio};
    for bytes in [
        pdf(&["hello", "", "world"]),
        b"private invalid content".to_vec(),
    ] {
        let mut child = Command::new(env!("CARGO_BIN_EXE_weft-spec-pdf-worker"))
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .unwrap();
        child.stdin.take().unwrap().write_all(&bytes).unwrap();
        let output = child.wait_with_output().unwrap();
        assert!(output.status.success());
        assert!(output.stderr.is_empty());
        assert!(output.stdout.len() <= MAX_RESULT_BYTES);
        let value: serde_json::Value = serde_json::from_slice(&output.stdout).unwrap();
        assert_eq!(
            value["sourceSha256"],
            format!("{:x}", Sha256::digest(&bytes))
        );
        if bytes.starts_with(b"%PDF") {
            assert_eq!(value["pages"][2]["page"], 3);
            assert_eq!(value["pages"][2]["text"], "world");
        } else {
            assert_eq!(value["status"], "malformed");
            assert!(
                !String::from_utf8(output.stdout)
                    .unwrap()
                    .contains("private invalid content")
            );
        }
    }
}

#[test]
fn malformed_children_are_not_silently_renumbered_as_valid_citations() {
    for invalid in [
        lopdf::Object::Null,
        lopdf::Object::Reference((9999, 0)),
        lopdf::Object::Reference((4, 0)),
    ] {
        let mut doc = lopdf::Document::load_mem(&pdf(&["first", "second"])).unwrap();
        doc.get_dictionary_mut((2, 0)).unwrap().set(
            "Kids",
            vec![
                lopdf::Object::Reference((4, 0)),
                invalid,
                lopdf::Object::Reference((6, 0)),
            ],
        );
        doc.get_dictionary_mut((2, 0)).unwrap().set("Count", 3);
        let mut bytes = Vec::new();
        doc.save_to(&mut bytes).unwrap();
        let result = extract(&bytes);
        assert_eq!(result.status, Status::Malformed);
        assert!(result.pages.is_empty());
    }
}

#[test]
fn deeply_nested_page_trees_are_refused_before_traversal_can_omit_pages() {
    use lopdf::{Object, dictionary};
    let mut doc = lopdf::Document::load_mem(&pdf(&["last"])).unwrap();
    let mut child = (4, 0);
    for _ in 0..257 {
        let parent = doc.add_object(dictionary! { "Type" => "Pages", "Count" => 1,
        "Kids" => vec![Object::Reference(child)] });
        doc.get_dictionary_mut(child).unwrap().set("Parent", parent);
        child = parent;
    }
    doc.get_dictionary_mut((2, 0))
        .unwrap()
        .set("Kids", vec![Object::Reference(child)]);
    doc.get_dictionary_mut(child).unwrap().set("Parent", (2, 0));
    let mut bytes = Vec::new();
    doc.save_to(&mut bytes).unwrap();
    let result = extract(&bytes);
    assert_eq!(result.status, Status::LimitExceeded);
    assert!(result.pages.is_empty());
}

#[test]
fn page_reference_alias_cannot_duplicate_a_page_under_a_second_identity() {
    let mut doc = lopdf::Document::load_mem(&pdf(&["only actual page"])).unwrap();
    let alias = doc.add_object(lopdf::Object::Reference((4, 0)));
    doc.get_dictionary_mut((2, 0)).unwrap().set(
        "Kids",
        vec![
            lopdf::Object::Reference((4, 0)),
            lopdf::Object::Reference(alias),
        ],
    );
    doc.get_dictionary_mut((2, 0)).unwrap().set("Count", 2);
    let mut bytes = Vec::new();
    doc.save_to(&mut bytes).unwrap();
    let result = extract(&bytes);
    assert_eq!(result.status, Status::Malformed);
    assert!(result.pages.is_empty());
}

#[test]
fn password_protected_documents_are_explicitly_refused() {
    let mut doc = lopdf::Document::load_mem(&pdf(&["hidden fixture content"])).unwrap();
    doc.trailer.set(
        "ID",
        vec![
            lopdf::Object::string_literal("test-document-id"),
            lopdf::Object::string_literal("test-document-id"),
        ],
    );
    let state = lopdf::EncryptionState::try_from(lopdf::EncryptionVersion::V2 {
        document: &doc,
        owner_password: "fixture-owner",
        user_password: "fixture-reader",
        key_length: 128,
        permissions: lopdf::Permissions::PRINTABLE,
    })
    .unwrap();
    doc.encrypt(&state).unwrap();
    let mut bytes = Vec::new();
    doc.save_to(&mut bytes).unwrap();
    let result = extract(&bytes);
    assert_eq!(result.status, Status::Encrypted);
    assert!(result.pages.is_empty());
}

#[test]
fn image_only_page_preserves_page_number_and_requests_accessible_text() {
    use lopdf::{Stream, dictionary};
    let mut doc = lopdf::Document::load_mem(&pdf(&[""])).unwrap();
    let image = doc.add_object(Stream::new(
        dictionary! {
            "Type" => "XObject", "Subtype" => "Image", "Width" => 1, "Height" => 1,
            "ColorSpace" => "DeviceRGB", "BitsPerComponent" => 8,
        },
        vec![0, 0, 0],
    ));
    doc.get_dictionary_mut((4, 0)).unwrap().set(
        "Resources",
        dictionary! {
            "XObject" => dictionary! { "Im1" => image }
        },
    );
    doc.objects.insert(
        (5, 0),
        Stream::new(dictionary! {}, b"q 10 0 0 10 30 700 cm /Im1 Do Q".to_vec()).into(),
    );
    let mut bytes = Vec::new();
    doc.save_to(&mut bytes).unwrap();
    let result = extract(&bytes);
    assert_eq!(result.status, Status::NeedsOcr);
    assert_eq!(result.pages.len(), 1);
    assert_eq!(result.pages[0].page, 1);
    assert!(result.pages[0].missing_text);
}
