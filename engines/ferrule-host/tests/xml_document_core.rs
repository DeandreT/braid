//! Self-authored fixtures. These tests exercise the dormant core only: they do
//! not assert that preview/compiled XML or native/generated parity is qualified.
use braid_ferrule_host::xml_document::{
    self as xml, Error, Metadata, Resource, Role, Safety, SchemaHints, SchemaLocation,
};
use ferrule_mapping::FormatOptions;
use serde_json::{Value, json};

const NATIVE: &[u8] = include_bytes!("fixtures/xml-closed.native.json");

fn safety() -> Safety {
    Safety::new(xml::MAX_DOCUMENT_BYTES).unwrap()
}
fn input_metadata() -> Metadata {
    Metadata::from_options(
        &FormatOptions {
            xml_document: true,
            ..Default::default()
        },
        Role::Input,
    )
    .unwrap()
}
fn hinted_metadata(token: &str) -> Metadata {
    Metadata {
        schema_hints: Some(SchemaHints {
            no_namespace_location: Some(token.into()),
            locations: vec![],
        }),
        ..input_metadata()
    }
}
fn native() -> Value {
    serde_json::from_slice(NATIVE).unwrap()
}
fn native_with_named_xml_boundaries() -> Value {
    let mut value = native();
    value["extra_sources"] = json!([{
        "name":"lookup", "path":"never-open", "schema":value["source"],
        "options":{"xml_document":true}
    }]);
    value["extra_targets"] = json!([{
        "name":"copy", "schema":value["target"], "root":value["root"],
        "options":{"xml_document":true}
    }]);
    value
}
fn admit(
    value: &Value,
) -> Result<(braid_ferrule_host::decode::DecodedProject, xml::Admission), Error> {
    xml::admit_native(&serde_json::to_vec(value).unwrap())
}
fn options(value: Value, role: Role) -> Result<Metadata, Error> {
    Metadata::from_options(&serde_json::from_value(value).unwrap(), role)
}

#[test]
fn physical_nodes_merge_text_cdata_and_references_but_not_comments_or_pis() {
    let stats = safety().validate_bytes(b" \n<?xml-stylesheet href='never-open'?>\n<r>a<![CDATA[b]]>&amp;&#65;<!--c-->d<?pi x?>e<n/>f</r><!--after--> ").unwrap();
    // document, PI, root, ab&A, comment, d, PI, e, n, f, comment
    assert_eq!(stats.nodes, 11);
    assert_eq!(stats.maximum_depth, 2);
    assert_eq!(
        safety()
            .validate_bytes(b"<r><![CDATA[]]></r>")
            .unwrap()
            .nodes,
        3
    );
}

#[test]
fn all_dtd_forms_are_rejected_but_doctype_like_comment_and_cdata_text_is_safe() {
    for document in [
        "<!DOCTYPE r><r/>",
        "<!DOCTYPE r SYSTEM 'file:///private'><r/>",
        "<!DOCTYPE r PUBLIC 'id' 'https://example.invalid/schema'><r/>",
        "<!DOCTYPE r [<!ENTITY x 'value'>]><r>&x;</r>",
        "<!DOCTYPE r [<!ENTITY % x SYSTEM 'file:///private'>%x;]><r/>",
        "<!DOCTYPE r [<!ENTITY unused 'x'>]><r/>",
    ] {
        assert_eq!(
            safety().validate_bytes(document.as_bytes()),
            Err(Error::DtdProhibited)
        );
    }
    assert!(
        safety()
            .validate_bytes(b"<!-- <!DOCTYPE harmless> --><r><![CDATA[<!DOCTYPE harmless>]]></r>")
            .is_ok()
    );
}

#[test]
fn only_predefined_and_valid_xml_numeric_references_are_accepted() {
    assert!(safety().validate_bytes("<r a='&lt;&gt;&amp;&apos;&quot;&#9;&#xA;'>&lt;&gt;&amp;&apos;&quot;&#13;&#x1F600;😀</r>".as_bytes()).is_ok());
    for body in [
        "&unknown;",
        "&AMP;",
        "&;",
        "&amp",
        "&#0;",
        "&#xD800;",
        "&#xFFFE;",
        "&#x110000;",
        "&#-1;",
        "&#+1;",
        "&#x;",
        "&#X41;",
        "&#999999999999999999999;",
    ] {
        assert!(
            safety()
                .validate_bytes(format!("<r>{body}</r>").as_bytes())
                .is_err(),
            "{body}"
        );
        assert!(
            safety()
                .validate_bytes(format!("<r a='{body}'/>").as_bytes())
                .is_err(),
            "{body}"
        );
    }
    // Entity-looking text in these constructs is not an entity reference.
    assert!(
        safety()
            .validate_bytes(b"<r><!--&unknown;--><![CDATA[&unknown;]]><?pi &unknown;?></r>")
            .is_ok()
    );
}

#[test]
fn encoding_declaration_and_one_root_are_not_guessed() {
    for document in [
        "<r/>",
        "\u{feff}<r/>",
        "<?xml version='1.0'?><r/>",
        "\u{feff}<?xml version='1.0' encoding='uTf-8' standalone='yes'?><r/>",
    ] {
        assert!(
            safety().validate_bytes(document.as_bytes()).is_ok(),
            "{document}"
        );
    }
    for document in [
        "",
        " ",
        "<!--only-->",
        "<r/><s/>",
        "<r>",
        "</r>",
        "<r></s>",
        "before<r/>",
        "<r/>after",
        "&#32;<r/>",
        "<![CDATA[ ]]><r/>",
        "<r/>\u{feff}",
        "\u{feff}\u{feff}<r/>",
        "<?xml version='1.1'?><r/>",
        "<?xml version='1.0' encoding='UTF-16'?><r/>",
        "<?xml encoding='UTF-8' version='1.0'?><r/>",
        "<?xml version='1.0' unknown='x'?><r/>",
        "<?xml version='1.0' version='1.0'?><r/>",
        "<?xml version='1.0' standalone='YES'?><r/>",
        "<?xml version='1.0' standalone='no' encoding='UTF-8'?><r/>",
        "<?xml version='1.0'encoding='UTF-8'?><r/>",
        " <?xml version='1.0'?><r/>",
        "<!--x--><?xml version='1.0'?><r/>",
        "<?XML version='1.0'?><r/>",
        "<r><?xml version='1.0'?></r>",
        "<r>]]></r>",
        "<r>\0</r>",
        "<r><!--a--b--></r>",
        "<r><!--a---></r>",
        "<1r/>",
        "<:r/>",
        "<r:/>",
    ] {
        assert!(
            safety().validate_bytes(document.as_bytes()).is_err(),
            "{document:?}"
        );
    }
    assert_eq!(
        safety().validate_bytes(b"<r>\xff</r>"),
        Err(Error::InvalidEncoding)
    );
    assert_eq!(
        safety().validate_bytes(b"\xff\xfe<\0r\0/\0>\0"),
        Err(Error::InvalidEncoding)
    );
}

#[test]
fn physical_names_are_bmp_but_values_may_be_supplementary() {
    assert!(
        safety()
            .validate_bytes("<é a='😀'>😀</é>".as_bytes())
            .is_ok()
    );
    assert!(safety().validate_bytes("<𐀀/>".as_bytes()).is_err());
    assert!(safety().validate_bytes("<r 𐀀='x'/>".as_bytes()).is_err());
}

#[test]
fn physical_name_components_use_the_pinned_reader_table_not_fifth_edition_ranges() {
    // Samples of several distinct Fifth Edition-only ranges, not a U+037F
    // blacklist. The shared fixture covers every prefix/local/PI position.
    for ch in [
        '\u{037f}', '\u{0346}', '\u{200c}', '\u{203f}', '\u{3001}', '\u{f900}',
    ] {
        for document in [
            format!("<{ch}/>"),
            format!("<r><ignored><a{ch}/></ignored></r>"),
            format!("<r a{ch}='value'/>"),
            format!("<r xmlns:a{ch}='urn:unused'/>"),
            format!("<?a{ch}?><r/>"),
        ] {
            assert_eq!(
                safety().validate_bytes(document.as_bytes()),
                Err(Error::UnsupportedName)
            );
        }
        let values = format!(
            "<p:r xmlns:p='urn:{ch}' a='{ch}'>{ch}<![CDATA[{ch}]]><!--{ch}--><?pi {ch}?></p:r>"
        );
        let stats = safety().validate_bytes(values.as_bytes()).unwrap();
        assert_eq!(stats.bytes, values.len());
    }
    // These remain valid non-ASCII starts or continuations in the old table.
    for document in [
        "<é/>",
        "<\u{0386}/>",
        "<a\u{0387}/>",
        "<a\u{02d0}/>",
        "<a\u{0345}/>",
    ] {
        assert!(safety().validate_bytes(document.as_bytes()).is_ok());
    }
    for document in ["<\u{0387}/>", "<\u{02d0}/>", "<\u{0345}/>"] {
        assert_eq!(
            safety().validate_bytes(document.as_bytes()),
            Err(Error::UnsupportedName)
        );
    }
}

#[test]
fn processing_instruction_targets_are_colon_free_but_data_and_qnames_are_not() {
    for document in ["<?a:b?><r/>", "<?:a?><r/>", "<?a:?><r/>"] {
        assert_eq!(
            safety().validate_bytes(document.as_bytes()),
            Err(Error::UnsupportedName)
        );
    }
    assert!(
        safety()
            .validate_bytes("<?é a:b?><p:r xmlns:p='urn:a:b' p:a='a:b'/>".as_bytes())
            .is_ok()
    );
    // The reserved XML target keeps its existing syntax refusal category.
    assert_eq!(
        safety().validate_bytes(b"<?XmL?><r/>"),
        Err(Error::InvalidInput)
    );
}

#[test]
fn namespaces_and_duplicate_expanded_attributes_are_checked_without_resolution() {
    for document in [
        "<r xmlns='urn:r'><c xmlns=''/></r>",
        "<p:r xmlns:p='urn:r' xmlns:q='urn:q' q:a='1'/>",
        "<r xml:lang='en' xmlns:xml='http://www.w3.org/XML/1998/namespace'/>",
        "<r xmlns:a='urn:&#120;' xmlns:b='urn:y' a:x='1' b:x='2'/>",
        "<r xmlns:p='urn:outer'><p:c xmlns:p='urn:inner'/><p:c/></r>",
        "<r xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xsi:schemaLocation='urn:r https://example.invalid/never-fetch.xsd'/>",
    ] {
        assert!(
            safety().validate_bytes(document.as_bytes()).is_ok(),
            "{document}"
        );
    }
    for document in [
        "<p:r/>",
        "<r p:a='1'/>",
        "<xmlns:r/>",
        "<r a='1' a='2'/>",
        "<r a='1'b='2'/>",
        "<r a='<'/>",
        "<r xmlns:p=''/>",
        "<r xmlns:xml='urn:wrong'/>",
        "<r xmlns:xmlns='urn:wrong'/>",
        "<r xmlns='http://www.w3.org/XML/1998/namespace'/>",
        "<r xmlns:p='http://www.w3.org/2000/xmlns/'/>",
        "<r xmlns:a='urn:x' xmlns:b='urn:&#120;' a:x='1' b:x='2'/>",
        "<r><p:c xmlns:p='urn:inner'/><p:c/></r>",
    ] {
        assert!(
            safety().validate_bytes(document.as_bytes()).is_err(),
            "{document}"
        );
    }
}

#[test]
fn depth_checks_every_element_even_unknown_schema_content() {
    let document = |depth| {
        format!(
            "{}{}",
            "<unknown>".repeat(depth),
            "</unknown>".repeat(depth)
        )
    };
    assert_eq!(
        safety()
            .validate_bytes(document(64).as_bytes())
            .unwrap()
            .maximum_depth,
        64
    );
    assert_eq!(
        safety().validate_bytes(document(65).as_bytes()),
        Err(Error::ResourceLimit(Resource::PhysicalDepth))
    );
    let empty_at_limit = format!("{}<empty/>{}", "<r>".repeat(63), "</r>".repeat(63));
    assert!(safety().validate_bytes(empty_at_limit.as_bytes()).is_ok());
    let empty_over = format!("<r>{empty_at_limit}</r>");
    assert_eq!(
        safety().validate_bytes(empty_over.as_bytes()),
        Err(Error::ResourceLimit(Resource::PhysicalDepth))
    );
}

#[test]
fn node_budget_exact_boundary_includes_document_and_ignored_elements() {
    let document = format!("<r>{}</r>", "<n/>".repeat(xml::MAX_NODES - 2));
    assert_eq!(
        safety().validate_bytes(document.as_bytes()).unwrap().nodes,
        xml::MAX_NODES
    );
    let over = document.replace("</r>", "<n/></r>");
    assert_eq!(
        safety().validate_bytes(over.as_bytes()),
        Err(Error::ResourceLimit(Resource::PhysicalNodes))
    );
}

#[test]
fn attribute_limit_includes_namespace_declarations() {
    let attrs = (0..255).map(|i| format!(" a{i}='v'")).collect::<String>();
    let exact = format!("<r xmlns='urn:r'{attrs}/>");
    assert_eq!(
        safety()
            .validate_bytes(exact.as_bytes())
            .unwrap()
            .maximum_attributes,
        256
    );
    let over = format!("<r xmlns='urn:r'{attrs} extra='v'/>");
    assert_eq!(
        safety().validate_bytes(over.as_bytes()),
        Err(Error::ResourceLimit(Resource::Attributes))
    );
}

#[test]
fn document_and_aggregate_budgets_count_original_utf8_bytes() {
    let exact = format!("<r>{}</r>", "x".repeat(xml::MAX_DOCUMENT_BYTES - 7));
    assert_eq!(
        safety().validate_bytes(exact.as_bytes()).unwrap().bytes,
        xml::MAX_DOCUMENT_BYTES
    );
    assert_eq!(
        safety().validate_bytes(format!("{exact} ").as_bytes()),
        Err(Error::ResourceLimit(Resource::DocumentBytes))
    );
    let smaller = Safety::new(8).unwrap();
    assert!(smaller.validate_bytes(b"<r></r> ").is_ok());
    assert_eq!(
        smaller.validate_bytes("<r>é</r>".as_bytes()),
        Err(Error::ResourceLimit(Resource::DocumentBytes))
    );
    assert!(
        smaller
            .validate_set(&[b"<r/>", b"<r/>"], Role::Input)
            .is_ok()
    );
    assert_eq!(
        smaller.validate_set(&[b"<r/>", b"<r/> "], Role::Input),
        Err(Error::ResourceLimit(Resource::AggregateBytes))
    );
    assert!(Safety::new(0).is_err());
    assert!(Safety::new(xml::MAX_DOCUMENT_BYTES + 1).is_err());
}

#[test]
fn complete_set_validation_and_direction_specific_counts_have_no_partial_result() {
    let documents = vec![b"<r/>".as_slice(); 64];
    assert!(safety().validate_set(&documents, Role::Output).is_ok());
    assert_eq!(
        safety().validate_set(&documents[..17], Role::Input),
        Err(Error::ResourceLimit(Resource::DocumentCount))
    );
    assert!(safety().validate_set(&documents[..16], Role::Input).is_ok());
    assert_eq!(
        safety().validate_set(&vec![b"<r/>".as_slice(); 65], Role::Output),
        Err(Error::ResourceLimit(Resource::DocumentCount))
    );
    assert_eq!(
        safety().validate_set(&[b"<r/>", b"<r>&private;</r>"], Role::Output),
        Err(Error::InvalidInput)
    );
}

#[test]
fn cancellation_is_polled_inside_large_attributes_and_before_each_document() {
    let document = format!("<r a='{}'/>", "x".repeat(32_000));
    let mut polls = 0;
    let result = safety().validate_bytes_with_cancel(document.as_bytes(), || {
        polls += 1;
        polls > 20
    });
    assert_eq!(result, Err(Error::Cancelled));
    assert_eq!(
        safety().validate_set_with_cancel(&[b"<r/>"], Role::Input, || true),
        Err(Error::Cancelled)
    );
}

#[test]
fn ordinary_large_attribute_is_borrowed_and_attribute_overflow_precedes_value_materialization() {
    let huge = "x".repeat(1_000_000);
    assert!(
        safety()
            .validate_bytes(format!("<r a='{huge}'/>").as_bytes())
            .is_ok()
    );
    let attrs = (0..256).map(|i| format!(" a{i}=''")).collect::<String>();
    assert_eq!(
        safety().validate_bytes(format!("<r{attrs} extra='{huge}'/>").as_bytes()),
        Err(Error::ResourceLimit(Resource::Attributes))
    );
}

#[test]
fn exact_options_accept_only_identity_and_output_hints() {
    assert!(options(json!({"xml_document":true}), Role::Input).is_ok());
    assert_eq!(options(json!({}), Role::Input), Err(Error::InvalidOptions));
    let hints = json!({"xml_document":true,"xml_schema_hints":{"no_namespace_location":"https://example.invalid/x.xsd","locations":[{"namespace":"urn:one","location":"one.xsd"}]}});
    let metadata = options(hints.clone(), Role::Output).unwrap();
    assert_eq!(
        metadata.schema_hints.unwrap().locations[0].namespace,
        "urn:one"
    );
    assert_eq!(options(hints, Role::Input), Err(Error::InvalidOptions));
    for (key, value) in [
        ("xml_allow_inactive_root_type_members", json!(true)),
        ("xml_root_view_read_policy", json!(true)),
        ("local_xml_file_set", json!(true)),
        ("json_document", json!(true)),
        ("json_lines", json!(true)),
        ("json5", json!(true)),
        ("lenient_segments", json!(true)),
        ("delimiter", json!(";")),
        ("has_header_row", json!(false)),
        ("tabular_kind", json!("csv")),
        ("mfd_decimal_input_names", json!({"1":"x"})),
        ("mfd_optional_parser_input_names", json!({"1":"x"})),
        ("csv_utf8_bom", json!(true)),
        ("xlsx_sheet", json!("Sheet1")),
        ("json_schema_unresolved_reference", json!("private-schema")),
    ] {
        let mut value_options = json!({"xml_document":true});
        value_options[key] = value;
        assert_eq!(
            options(value_options, Role::Input),
            Err(Error::InvalidOptions),
            "{key}"
        );
    }
}

#[test]
fn hint_validation_has_upstream_bounds_and_never_resolves_locations() {
    for hints in [
        json!({}),
        json!({"no_namespace_location":""}),
        json!({"no_namespace_location":"two words"}),
        json!({"no_namespace_location":"\u{0}"}),
        json!({"locations":[{"namespace":"urn:x","location":"a"},{"namespace":"urn:x","location":"b"}]}),
        json!({"no_namespace_location":"x".repeat(4097)}),
        json!({"locations":(0..33).map(|i|json!({"namespace":format!("urn:{i}"),"location":"x"})).collect::<Vec<_>>()}),
    ] {
        assert_eq!(
            options(
                json!({"xml_document":true,"xml_schema_hints":hints}),
                Role::Output
            ),
            Err(Error::InvalidOptions)
        );
    }
    assert!(options(json!({"xml_document":true,"xml_schema_hints":{"no_namespace_location":"file:///never-open"}}), Role::Output).is_ok());
    assert!(options(json!({"xml_document":true,"xml_schema_hints":{"no_namespace_location":"x".repeat(4096)}}), Role::Output).is_ok());
}

#[test]
fn raw_options_reject_unknown_and_duplicate_keys_on_every_boundary_before_normalization() {
    let bare = String::from_utf8(NATIVE.to_vec()).unwrap();
    for bag in [
        r#"{"xml_document":true,"unknown":false}"#,
        r#"{"xml_document":true,"xml_document":true}"#,
        r#"{"xml_document":true,"xml_\u0064ocument":true}"#,
    ] {
        let changed = bare.replacen(r#"{"xml_document":true}"#, bag, 1);
        assert_eq!(
            xml::validate_raw_options(changed.as_bytes()),
            Err(Error::InvalidOptions)
        );
    }
    let mut value = native();
    value["extra_targets"] = json!([{"name":"extra","schema":value["target"],"root":value["root"],"options":{"xml_document":true,"unknown":false}}]);
    assert_eq!(
        xml::validate_raw_options(&serde_json::to_vec(&value).unwrap()),
        Err(Error::InvalidOptions)
    );
    value.as_object_mut().unwrap().remove("extra_targets");
    value["extra_sources"] = json!([{"name":"unused","path":"never-open","schema":value["source"],"options":{"xml_document":true,"unknown":false}}]);
    assert_eq!(
        xml::validate_raw_options(&serde_json::to_vec(&value).unwrap()),
        Err(Error::InvalidOptions)
    );
}

#[test]
fn raw_guard_handles_bare_and_actual_versioned_native_envelopes() {
    assert!(xml::validate_raw_options(NATIVE).is_ok());
    // A version-2 envelope with no float slots is valid for this fixture. Its
    // real decoder must agree, not merely the option-location guard.
    let wrapped = json!({"__ferrule_file":{"kind":"project","version":2,"float_bits":{}},"document":native()});
    assert!(xml::validate_raw_options(&serde_json::to_vec(&wrapped).unwrap()).is_ok());
    assert!(admit(&wrapped).is_ok());
    let actual = ferrule_mapping::project_file::encode_pretty(
        &braid_ferrule_host::decode::decode_native(NATIVE)
            .unwrap()
            .project,
    )
    .unwrap();
    assert!(xml::admit_native(actual.as_bytes()).is_ok());
    let mut wrapped_bad = wrapped;
    wrapped_bad["document"]["target_options"]["unknown"] = json!(true);
    assert_eq!(
        xml::validate_raw_options(&serde_json::to_vec(&wrapped_bad).unwrap()),
        Err(Error::InvalidOptions)
    );
}

#[test]
fn native_byte_cap_and_all_saved_default_options_are_preserved() {
    let mut padded = NATIVE.to_vec();
    padded.resize(4 * 1024 * 1024, b' ');
    assert!(xml::validate_raw_options(&padded).is_ok());
    padded.push(b' ');
    assert_eq!(
        xml::validate_raw_options(&padded),
        Err(Error::ResourceLimit(Resource::NativeBytes))
    );
    let mut all_defaults = serde_json::to_value(FormatOptions::default()).unwrap();
    all_defaults["xml_document"] = json!(true);
    assert!(options(all_defaults, Role::Input).is_ok());
}

#[test]
fn nested_hint_objects_reject_unknown_and_duplicate_raw_members() {
    let bare = String::from_utf8(NATIVE.to_vec()).unwrap();
    for hints in [
        r#"{"no_namespace_location":"x","unknown":false}"#,
        r#"{"no_namespace_location":"x","no_namespace_location":"y"}"#,
        r#"{"locations":[{"namespace":"urn:x","location":"a","unknown":1}]}"#,
        r#"{"locations":[{"namespace":"urn:x","location":"a","location":"b"}]}"#,
    ] {
        let target =
            format!(r#""target_options":{{"xml_document":true,"xml_schema_hints":{hints}}}"#);
        let changed = bare.replace(r#""target_options":{"xml_document":true}"#, &target);
        assert!(xml::admit_native(changed.as_bytes()).is_err());
    }
}

#[test]
fn metadata_is_closed_versioned_role_correct_and_explicitly_nullable() {
    let metadata = input_metadata();
    let wire = serde_json::to_value(&metadata).unwrap();
    assert_eq!(
        wire,
        json!({"schema":"braid.xml-boundary.v1","safetyProfile":"braid.xml-safe.v1","schemaHints":null})
    );
    assert_eq!(
        serde_json::from_value::<Metadata>(wire.clone()).unwrap(),
        metadata
    );
    for key in ["schema", "safetyProfile", "schemaHints"] {
        let mut missing = wire.clone();
        missing.as_object_mut().unwrap().remove(key);
        assert!(
            serde_json::from_value::<Metadata>(missing).is_err(),
            "{key}"
        );
    }
    let mut extra = wire;
    extra["csvDialect"] = json!(null);
    assert!(serde_json::from_value::<Metadata>(extra).is_err());
    let mut future = metadata;
    future.schema = "future".into();
    assert_eq!(future.validate(Role::Input), Err(Error::InvalidMetadata));
}

#[test]
fn metadata_records_require_objects_at_every_level() {
    let top_array = r#"["braid.xml-boundary.v1","braid.xml-safe.v1",null]"#;
    let hints_array = r#"{"schema":"braid.xml-boundary.v1","safetyProfile":"braid.xml-safe.v1","schemaHints":[null,[]]}"#;
    let pair_array = r#"{"schema":"braid.xml-boundary.v1","safetyProfile":"braid.xml-safe.v1","schemaHints":{"noNamespaceLocation":null,"locations":[["urn:one","one.xsd"]]}}"#;
    for encoded in [top_array, hints_array, pair_array] {
        assert!(
            serde_json::from_str::<Metadata>(encoded).is_err(),
            "{encoded}"
        );
        assert!(serde_json::from_slice::<Metadata>(encoded.as_bytes()).is_err());
        assert!(
            serde_json::from_value::<Metadata>(serde_json::from_str::<Value>(encoded).unwrap())
                .is_err()
        );
    }
    for encoded in ["[]", "[null,[]]", r#"["one.xsd",[]]"#] {
        assert!(serde_json::from_str::<SchemaHints>(encoded).is_err());
    }
    for encoded in ["[]", r#"["urn:one","one.xsd"]"#] {
        assert!(serde_json::from_str::<SchemaLocation>(encoded).is_err());
    }
    let valid = r#"{"schema":"braid.xml-boundary.v1","safetyProfile":"braid.xml-safe.v1","schemaHints":{"noNamespaceLocation":null,"locations":[{"namespace":"urn:one","location":"one.xsd"}]}}"#;
    let metadata = serde_json::from_str::<Metadata>(valid).unwrap();
    metadata.validate(Role::Output).unwrap();
    assert_eq!(
        metadata
            .schema_hints
            .as_ref()
            .unwrap()
            .no_namespace_location,
        None
    );
    assert_eq!(
        metadata.schema_hints.as_ref().unwrap().locations[0].namespace,
        "urn:one"
    );
    assert!(
        serde_json::from_str::<SchemaHints>(r#"{"noNamespaceLocation":null,"locations":[]}"#)
            .is_ok()
    );
    assert!(
        serde_json::from_str::<SchemaLocation>(r#"{"namespace":"urn:one","location":"one.xsd"}"#)
            .is_ok()
    );
}

#[test]
fn object_only_metadata_preserves_required_null_unknown_and_duplicate_checks() {
    for encoded in [
        r#"{"schema":"braid.xml-boundary.v1","schema":"braid.xml-boundary.v1","safetyProfile":"braid.xml-safe.v1","schemaHints":null}"#,
        r#"{"schema":"braid.xml-boundary.v1","safetyProfile":"braid.xml-safe.v1","schemaHints":null,"schemaHints":null}"#,
        r#"{"schema":"braid.xml-boundary.v1","safetyProfile":"braid.xml-safe.v1","schemaHints":null,"other":null}"#,
        r#"{"schema":"braid.xml-boundary.v1","safetyProfile":"braid.xml-safe.v1"}"#,
    ] {
        assert!(serde_json::from_str::<Metadata>(encoded).is_err());
    }
    for encoded in [
        r#"{"locations":[]}"#,
        r#"{"noNamespaceLocation":null}"#,
        r#"{"noNamespaceLocation":null,"noNamespaceLocation":null,"locations":[]}"#,
        r#"{"noNamespaceLocation":null,"locations":[],"locations":[]}"#,
        r#"{"noNamespaceLocation":null,"locations":[],"other":null}"#,
        r#"{"noNamespaceLocation":null,"locations":null}"#,
    ] {
        assert!(serde_json::from_str::<SchemaHints>(encoded).is_err());
        let nested = format!(
            r#"{{"schema":"braid.xml-boundary.v1","safetyProfile":"braid.xml-safe.v1","schemaHints":{encoded}}}"#
        );
        assert!(serde_json::from_str::<Metadata>(&nested).is_err());
    }
    for encoded in [
        r#"{"namespace":"urn:one"}"#,
        r#"{"location":"one.xsd"}"#,
        r#"{"namespace":"urn:one","namespace":"urn:one","location":"one.xsd"}"#,
        r#"{"namespace":"urn:one","location":"one.xsd","location":"one.xsd"}"#,
        r#"{"namespace":"urn:one","location":"one.xsd","other":null}"#,
    ] {
        assert!(serde_json::from_str::<SchemaLocation>(encoded).is_err());
        let nested = format!(r#"{{"noNamespaceLocation":null,"locations":[{encoded}]}}"#);
        assert!(serde_json::from_str::<SchemaHints>(&nested).is_err());
    }
    // Explicit null is still a valid required field, not an absent field.
    assert_eq!(serde_json::from_str::<Metadata>(r#"{"schema":"braid.xml-boundary.v1","safetyProfile":"braid.xml-safe.v1","schemaHints":null}"#).unwrap(), input_metadata());
}

#[test]
fn metadata_reservation_uses_default_dotnet_escaping_without_changing_native_wire() {
    let metadata = hinted_metadata("https://e.invalid/\"\\<>&é😀");
    metadata.validate(Role::Output).unwrap();
    let expected = br#""xmlMetadata":{"schema":"braid.xml-boundary.v1","safetyProfile":"braid.xml-safe.v1","schemaHints":{"noNamespaceLocation":"https://e.invalid/\"\\<>&"#;
    let mut encoded = Vec::new();
    let count = metadata
        .write_encoded_property(&mut encoded, xml::MAX_METADATA_BYTES)
        .unwrap();
    assert_eq!(input_metadata().encoded_property_bytes().unwrap(), 102);
    assert_eq!(count, 170); // Native wire stays minimally escaped.
    assert_eq!(metadata.encoded_property_bytes().unwrap(), 201); // Shared .NET reservation.
    assert!(encoded.starts_with(expected));
    assert!(String::from_utf8(encoded.clone()).unwrap().contains("é😀"));
    assert_eq!(count, encoded.len());
    assert_eq!(
        count,
        b"\"xmlMetadata\":".len() + serde_json::to_vec(&metadata).unwrap().len() + 1
    );
    let mut refused = Vec::new();
    assert_eq!(
        metadata.write_encoded_property(&mut refused, 200),
        Err(Error::ResourceLimit(Resource::MetadataBytes))
    );
    assert!(refused.is_empty());
    assert_eq!(
        metadata.write_encoded_property(&mut refused, 201).unwrap(),
        170
    );
    assert_eq!(refused, encoded);
}

#[test]
fn metadata_reservation_matches_pinned_dotnet_golden_string_escapes() {
    // Same cases and canonical spellings as XmlDocumentMetadataTests, plus
    // UTF-16 boundary values. Each empty hinted property has 139 fixed bytes,
    // including the 15-byte property/colon/reserved-comma overhead.
    for (token, escaped) in [
        ("\"", r#"\u0022"#),
        ("\\", r#"\\"#),
        ("&", r#"\u0026"#),
        ("'", r#"\u0027"#),
        ("+", r#"\u002B"#),
        ("<", r#"\u003C"#),
        (">", r#"\u003E"#),
        ("`", r#"\u0060"#),
        ("/", "/"),
        ("\u{7f}", r#"\u007F"#),
        ("é", r#"\u00E9"#),
        ("\u{100}", r#"\u0100"#),
        ("\u{7ff}", r#"\u07FF"#),
        ("\u{800}", r#"\u0800"#),
        ("\u{2028}", r#"\u2028"#),
        ("\u{d7ff}", r#"\uD7FF"#),
        ("\u{e000}", r#"\uE000"#),
        ("\u{fffd}", r#"\uFFFD"#),
        ("\u{10000}", r#"\uD800\uDC00"#),
        ("🚀", r#"\uD83D\uDE80"#),
        ("\u{10ffff}", r#"\uDBFF\uDFFF"#),
    ] {
        let metadata = hinted_metadata(token);
        metadata.validate(Role::Output).unwrap();
        assert_eq!(
            metadata.encoded_property_bytes().unwrap(),
            139 + escaped.len(),
            "{token:?}"
        );
        let native = serde_json::to_vec(&metadata).unwrap().len() + 15;
        assert!(metadata.encoded_property_bytes().unwrap() >= native);
    }
}

#[test]
fn metadata_default_encoder_widths_cover_all_basic_ascii_and_latin1() {
    for codepoint in 0u8..=127 {
        let ch = char::from(codepoint);
        let token = ch.to_string();
        let metadata = hinted_metadata(&token);
        let width = match codepoint {
            b'\\' | 8 | 9 | 10 | 12 | 13 => 2,
            0..=31 | b'"' | b'&' | b'\'' | b'+' | b'<' | b'>' | b'`' | 127 => 6,
            _ => 1,
        };
        assert_eq!(
            metadata.encoded_property_bytes().unwrap(),
            139 + width,
            "U+{codepoint:04X}"
        );
        // The counter is total over strings, but admission still excludes
        // control/space tokens below U+0021, independent of encoding length.
        assert_eq!(metadata.validate(Role::Output).is_ok(), codepoint > 32);
    }
    for codepoint in 128u8..=255 {
        let token = char::from(codepoint).to_string();
        let metadata = hinted_metadata(&token);
        metadata.validate(Role::Output).unwrap();
        assert_eq!(
            metadata.encoded_property_bytes().unwrap(),
            145,
            "U+{codepoint:04X}"
        );
    }
}

#[test]
fn reservation_counts_all_hint_fields_and_each_physical_occurrence() {
    let mut metadata = hinted_metadata("<");
    metadata.schema_hints.as_mut().unwrap().locations = vec![SchemaLocation {
        namespace: "urn:é+".into(),
        location: "😀&".into(),
    }];
    metadata.validate(Role::Output).unwrap();
    // Existing hinted property: 139 + 6. Pair punctuation/keys: 30;
    // namespace: 4 + 6 + 6; location: 12 + 6.
    assert_eq!(metadata.encoded_property_bytes().unwrap(), 209);
    assert_eq!(
        xml::metadata_ledger(&[input_metadata()], &[metadata.clone(), metadata]),
        Ok(520)
    );
}

#[test]
fn dotnet_html_escaping_repro_exceeds_the_shared_operation_limit() {
    let metadata = hinted_metadata(&"<".repeat(4096));
    metadata.validate(Role::Output).unwrap();
    assert_eq!(serde_json::to_vec(&metadata).unwrap().len() + 15, 4235);
    assert_eq!(metadata.encoded_property_bytes().unwrap(), 24_715);
    // 102 + 2 * 24,715 = 49,532, not the native 8,572-byte total.
    assert_eq!(
        xml::metadata_ledger(&[input_metadata()], &[metadata.clone(), metadata]),
        Err(Error::ResourceLimit(Resource::MetadataBytes))
    );
}

#[test]
fn shared_reservation_keeps_exact_aggregate_boundary_for_escaped_hints() {
    let large = hinted_metadata(&"<".repeat(4096));
    let mut second = "<".repeat(1302); // 7,812 encoded bytes.
    let exact = hinted_metadata(&second);
    // 102 + (139 + 24,576) + (139 + 7,812) = 32,768.
    assert_eq!(
        xml::metadata_ledger(&[input_metadata()], &[large.clone(), exact]),
        Ok(32_768)
    );
    second.push('x');
    assert_eq!(
        xml::metadata_ledger(&[input_metadata()], &[large, hinted_metadata(&second)]),
        Err(Error::ResourceLimit(Resource::MetadataBytes))
    );
}

fn sized_metadata(property_bytes: usize) -> Metadata {
    let mut metadata = input_metadata();
    metadata.schema_hints = Some(SchemaHints {
        no_namespace_location: None,
        locations: (0..9)
            .map(|i| SchemaLocation {
                namespace: format!("urn:{i}"),
                location: "x".into(),
            })
            .collect(),
    });
    let mut remaining = property_bytes - metadata.encoded_property_bytes().unwrap();
    for pair in &mut metadata.schema_hints.as_mut().unwrap().locations {
        let add = remaining.min(4095);
        pair.location.push_str(&"x".repeat(add));
        remaining -= add;
    }
    assert_eq!(remaining, 0);
    assert_eq!(metadata.encoded_property_bytes().unwrap(), property_bytes);
    metadata
}

#[test]
fn one_operation_metadata_budget_has_exact_boundary_and_no_dedup_discount() {
    let input = input_metadata();
    let bytes = input.encoded_property_bytes().unwrap();
    let exact = sized_metadata(xml::MAX_METADATA_BYTES - bytes);
    assert_eq!(
        xml::metadata_ledger(std::slice::from_ref(&input), &[exact]),
        Ok(xml::MAX_METADATA_BYTES)
    );
    assert_eq!(
        xml::metadata_ledger(
            std::slice::from_ref(&input),
            &[sized_metadata(xml::MAX_METADATA_BYTES - bytes + 1)]
        ),
        Err(Error::ResourceLimit(Resource::MetadataBytes))
    );
    assert_eq!(
        xml::metadata_ledger(&vec![input.clone(); 16], &vec![input.clone(); 64]),
        Ok(bytes * 80)
    );
    let repeated = sized_metadata(1000);
    assert_eq!(
        xml::metadata_ledger(&[input], &vec![repeated; 40]),
        Err(Error::ResourceLimit(Resource::MetadataBytes))
    );
}

#[test]
fn upstream_valid_large_hint_set_is_refused_by_transport_metadata_budget() {
    let hints = json!({"locations":(0..32).map(|i|json!({"namespace":format!("urn:{i}"),"location":"x".repeat(4096)})).collect::<Vec<_>>()});
    let metadata = options(
        json!({"xml_document":true,"xml_schema_hints":hints}),
        Role::Output,
    )
    .unwrap();
    assert_eq!(
        xml::metadata_ledger(&[input_metadata()], &[metadata]),
        Err(Error::ResourceLimit(Resource::MetadataBytes))
    );
}

#[test]
fn real_pinned_closed_schema_and_emission_proof_are_required() {
    let (decoded, admitted) = xml::admit_native(NATIVE).unwrap();
    assert_eq!(admitted.inputs.len(), 1);
    assert_eq!(admitted.outputs.len(), 1);
    let plan = ferrule_codegen::prepare_boundary_emission(
        &admitted.program,
        ferrule_codegen::CompanionSelection::Complete,
    )
    .unwrap();
    assert!(plan.xml().unwrap().selected_inputs().is_some());
    assert!(
        xml::validate_structured(
            b"<Order><code>A</code><quantity>2</quantity></Order>",
            &decoded.project.source,
            xml::MAX_DOCUMENT_BYTES
        )
        .is_ok()
    );
    assert_eq!(
        xml::validate_structured(
            b"<Order><quantity>private-invalid-number</quantity></Order>",
            &decoded.project.source,
            xml::MAX_DOCUMENT_BYTES
        ),
        Err(Error::InvalidInput)
    );
    assert_eq!(
        xml::validate_structured(
            b"<wrong/>",
            &decoded.project.source,
            xml::MAX_DOCUMENT_BYTES
        ),
        Err(Error::InvalidInput)
    );
    let mut invalid = native();
    invalid["source"]["repeating"] = json!(true);
    assert!(admit(&invalid).is_err());
    invalid = native();
    invalid["target_options"] = json!({"json_document":true});
    assert!(admit(&invalid).is_err());
    invalid = native();
    invalid["root"]["source"] = json!([]);
    assert!(matches!(admit(&invalid), Err(Error::UnsupportedBoundary)));
    invalid = native();
    invalid["extra_targets"] = json!([{
        "name":"iterating", "schema":invalid["target"],
        "root":invalid["root"], "options":{"xml_document":true}
    }]);
    invalid["extra_targets"][0]["root"]["source"] = json!([]);
    assert!(matches!(admit(&invalid), Err(Error::UnsupportedBoundary)));
}

#[test]
fn common_name_table_fences_every_declared_structured_schema_before_lowering() {
    for pointer in [
        "/source",
        "/target",
        "/extra_sources/0/schema",
        "/extra_targets/0/schema",
    ] {
        for name in ["\u{037f}", "a\u{037f}"] {
            for role in ["root", "group", "attribute"] {
                let mut value = native_with_named_xml_boundaries();
                let schema = value.pointer_mut(pointer).unwrap();
                match role {
                    "root" => schema["name"] = json!(name),
                    "group" => schema["kind"]["children"]
                        .as_array_mut()
                        .unwrap()
                        .push(json!({
                            "name":name, "kind":{"kind":"group","children":[
                                {"name":"value","kind":{"kind":"scalar","ty":"string"}}
                            ]}
                        })),
                    _ => schema["kind"]["children"]
                        .as_array_mut()
                        .unwrap()
                        .push(json!({
                            "name":name, "attribute":true, "kind":{"kind":"scalar","ty":"string"}
                        })),
                }
                let typed: ferrule_ir::SchemaNode = serde_json::from_value(schema.clone()).unwrap();
                // This is specifically the common-table gap: the real pinned
                // Structured proof accepts these Fifth Edition local names.
                assert!(ferrule_ir::xml_structured_document_input_is_supported(
                    &typed
                ));
                assert_eq!(
                    xml::bounded_closed_schema(&typed),
                    Err(Error::UnsupportedBoundary),
                    "{pointer} {role}"
                );
                assert!(
                    matches!(admit(&value), Err(Error::UnsupportedBoundary)),
                    "{pointer} {role}"
                );
            }
        }
    }
}

#[test]
fn structured_descriptors_have_local_names_not_authored_qname_prefix_slots() {
    for pointer in [
        "/source",
        "/target",
        "/extra_sources/0/schema",
        "/extra_targets/0/schema",
    ] {
        // Even an otherwise legal prefix is not a SchemaNode local name. Do
        // not split it, infer a URI, or repair a prefixed local-name string.
        for name in [
            "\u{037f}:local",
            "a\u{037f}:local",
            "p:\u{037f}",
            "p:a\u{037f}",
            "é:local",
            "p:local",
        ] {
            for attribute in [false, true] {
                let mut value = native_with_named_xml_boundaries();
                let schema = value.pointer_mut(pointer).unwrap();
                if attribute {
                    schema["kind"]["children"]
                        .as_array_mut()
                        .unwrap()
                        .push(json!({
                            "name":name, "attribute":true, "kind":{"kind":"scalar","ty":"string"}
                        }));
                } else {
                    schema["name"] = json!(name);
                }
                let typed: ferrule_ir::SchemaNode = serde_json::from_value(schema.clone()).unwrap();
                assert!(!ferrule_ir::xml_structured_document_input_is_supported(
                    &typed
                ));
                assert_eq!(
                    xml::bounded_closed_schema(&typed),
                    Err(Error::UnsupportedBoundary)
                );
                assert!(matches!(admit(&value), Err(Error::UnsupportedBoundary)));
            }
        }
    }
}

#[test]
fn proved_text_pseudo_fields_accented_names_and_unicode_namespace_values_remain_admitted() {
    let schema = json!({
        "name":"é", "xml_namespace":{"kind":"qualified","uri":"urn:\u{037f}:😀"},
        "kind":{"kind":"group","children":[
            {"name":"clé","attribute":true,"xml_namespace":{"kind":"qualified","uri":"urn:attribute:\u{037f}"},"kind":{"kind":"scalar","ty":"string"}},
            {"name":"#text","text":true,"kind":{"kind":"scalar","ty":"string"}}
        ]}
    });
    let typed: ferrule_ir::SchemaNode = serde_json::from_value(schema.clone()).unwrap();
    assert!(ferrule_ir::xml_structured_document_input_is_supported(
        &typed
    ));
    assert_eq!(xml::bounded_closed_schema(&typed), Ok(()));
    let xml_prefixed_attribute: ferrule_ir::SchemaNode = serde_json::from_value(json!({
        "name":"Record", "kind":{"kind":"group","children":[
            {"name":"lang","attribute":true,"xml_namespace":{"kind":"qualified","uri":"http://www.w3.org/XML/1998/namespace"},"kind":{"kind":"scalar","ty":"string"}}
        ]}
    })).unwrap();
    assert_eq!(xml::bounded_closed_schema(&xml_prefixed_attribute), Ok(()));
    let mut value = native_with_named_xml_boundaries();
    for pointer in [
        "/source",
        "/target",
        "/extra_sources/0/schema",
        "/extra_targets/0/schema",
    ] {
        *value.pointer_mut(pointer).unwrap() = schema.clone();
    }
    value["graph"]["nodes"]["1"]["path"] = json!(["clé"]);
    value["graph"]["nodes"]["2"]["path"] = json!(["#text"]);
    value["root"]["bindings"][0]["target_field"] = json!("clé");
    value["root"]["bindings"][1]["target_field"] = json!("#text");
    value["extra_targets"][0]["root"] = value["root"].clone();
    let (_, admission) = admit(&value).unwrap();
    assert_eq!((admission.inputs.len(), admission.outputs.len()), (2, 2));
    let document = "<é xmlns='urn:\u{037f}:😀' xmlns:a='urn:attribute:\u{037f}' a:clé='\u{037f}'>\u{037f}😀</é>";
    assert!(xml::validate_structured(document.as_bytes(), &typed, xml::MAX_DOCUMENT_BYTES).is_ok());

    // A text flag alone is not a license to skip arbitrary field names. These
    // are rejected by the real role proof before the common name table runs.
    for (name, text) in [("#text", false), ("\u{037f}", true), ("renamed", true)] {
        let mut invalid = schema.clone();
        invalid["kind"]["children"][1]["name"] = json!(name);
        invalid["kind"]["children"][1]["text"] = json!(text);
        let invalid: ferrule_ir::SchemaNode = serde_json::from_value(invalid).unwrap();
        assert_eq!(
            xml::bounded_closed_schema(&invalid),
            Err(Error::UnsupportedBoundary)
        );
    }
}

#[test]
fn real_upstream_parser_and_projection_resources_are_typed_and_sanitized() {
    let scalar: ferrule_ir::SchemaNode =
        serde_json::from_value(json!({"name":"Value","kind":{"kind":"scalar","ty":"string"}}))
            .unwrap();
    let raw_slots = format!("<Value>{}</Value>", "=".repeat(1_000_001));
    assert!(safety().validate_bytes(raw_slots.as_bytes()).is_ok());
    assert_eq!(
        xml::validate_structured(raw_slots.as_bytes(), &scalar, xml::MAX_DOCUMENT_BYTES),
        Err(Error::ResourceLimit(Resource::StructuredParser))
    );

    // A small physical document amplifies absent fields in the schema-shaped
    // instance. This exercises the real prospective budget, not a fabricated
    // error or a reduced/replaced upstream limit.
    let fields = (0..20)
        .map(|i| json!({"name":format!("field{i}"),"kind":{"kind":"scalar","ty":"string"}}))
        .collect::<Vec<_>>();
    let amplified: ferrule_ir::SchemaNode =
        serde_json::from_value(json!({"name":"Rows","kind":{"kind":"group","children":[
            {"name":"Row","repeating":true,"kind":{"kind":"group","children":fields}}
        ]}}))
        .unwrap();
    let rows = format!("<Rows>{}</Rows>", "<Row/>".repeat(50_001));
    assert!(safety().validate_bytes(rows.as_bytes()).is_ok());
    assert_eq!(
        xml::validate_structured(rows.as_bytes(), &amplified, xml::MAX_DOCUMENT_BYTES),
        Err(Error::ResourceLimit(Resource::StructuredProjection))
    );
}

#[test]
fn every_declared_endpoint_including_unused_hints_is_admitted_before_lowering() {
    let mut value = native();
    value["extra_sources"] = json!([{"name":"lookup","path":"never-open","schema":value["source"],"options":{"xml_document":true}}]);
    value["extra_targets"] = json!([{"name":"copy","schema":value["target"],"root":value["root"],"options":{"xml_document":true,"xml_schema_hints":{"no_namespace_location":"never-fetch.xsd"}}}]);
    let (_, admitted) = admit(&value).unwrap();
    assert_eq!(admitted.inputs.len(), 2);
    assert_eq!(admitted.outputs.len(), 2);
    assert!(admitted.outputs[1].schema_hints.is_some());
    value["extra_sources"][0]["options"] = json!({"json_document":true});
    assert!(admit(&value).is_err());
    value["extra_sources"][0]["options"] = json!({"xml_document":true});
    value["extra_sources"][0]["dynamic_path"] = json!({"node":1,"iteration":[]});
    assert!(admit(&value).is_err());
}

#[test]
fn schema_resource_limits_are_checked_before_recursive_proofs() {
    let mut node = json!({"name":"Leaf","kind":{"kind":"scalar","ty":"string"}});
    for _ in 1..64 {
        node = json!({"name":"Group","kind":{"kind":"group","children":[node]}});
    }
    let exact: ferrule_ir::SchemaNode = serde_json::from_value(node.clone()).unwrap();
    assert!(xml::bounded_closed_schema(&exact).is_ok());
    node = json!({"name":"Root","kind":{"kind":"group","children":[node]}});
    let over: ferrule_ir::SchemaNode = serde_json::from_value(node).unwrap();
    assert_eq!(
        xml::bounded_closed_schema(&over),
        Err(Error::ResourceLimit(Resource::SchemaDepth))
    );
    let fields = (0..xml::MAX_SCHEMA_NODES)
        .map(|i| json!({"name":format!("f{i}"),"kind":{"kind":"scalar","ty":"string"}}))
        .collect::<Vec<_>>();
    let wide: ferrule_ir::SchemaNode =
        serde_json::from_value(json!({"name":"Root","kind":{"kind":"group","children":fields}}))
            .unwrap();
    assert_eq!(
        xml::bounded_closed_schema(&wide),
        Err(Error::ResourceLimit(Resource::SchemaNodes))
    );
    let mut exact = wide;
    if let ferrule_ir::SchemaKind::Group { children, .. } = &mut exact.kind {
        children.pop();
    }
    assert!(xml::bounded_closed_schema(&exact).is_ok());
}

#[test]
fn errors_contain_only_fixed_classifications_not_payloads_or_locations() {
    let error = safety()
        .validate_bytes(b"<r>&secret_customer_value;</r>")
        .unwrap_err();
    assert_eq!(error, Error::InvalidInput);
    assert!(!format!("{error:?} {error}").contains("secret_customer_value"));
    assert!(
        !format!("{}", Error::ResourceLimit(Resource::StructuredProjection)).contains("private")
    );
}
