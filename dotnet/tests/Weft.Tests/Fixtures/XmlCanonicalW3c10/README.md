# W3C Canonical XML 1.0 examples

Source: [Canonical XML Version 1.0, sections 3.1–3.7](https://www.w3.org/TR/2001/REC-xml-c14n-20010315#Examples), **W3C Recommendation 15 March 2001**. Author/editor: John Boyer, PureEdge Solutions Inc.

Copyright © 2001 W3C® (MIT, INRIA, Keio), All Rights Reserved. [Document use license](https://www.w3.org/Consortium/Legal/copyright-documents-19990405). The full notice is retained in `W3C-NOTICE.txt`.

## Scope

These are source fixtures for Weft's dormant complete-document, inclusive C14N 1.0 with-comments core, not a claim of general W3C conformance, worker qualification, hard memory/time enforcement, or support through the public comparison dispatch. No private or Ferrule reference corpus is used.

| Section | Treatment | Reason |
| --- | --- | --- |
| 3.1 | Refusal | Original document contains an external DTD. |
| 3.2 | Admitted input and published golden | Complete DTD-free document. Input and canonical output are identical, 169 bytes, ending in `>` without a newline. |
| 3.3 | Refusal | Original document contains a DTD supplying a default attribute. |
| 3.4 | Refusal | Original document contains a DTD declaring typed attributes. |
| 3.5 | Refusal | Original document contains a DTD and external entity references. |
| 3.6 | Refusal | Original declaration specifies ISO-8859-1, outside Weft's UTF-8 domain. The displayed input is all ASCII, so its octets need no transcoding; the declaration remains untouched. |
| 3.7 | Refusal | Original document contains a DTD and the example requests an XPath subset. The subset expression is retained as evidence, never executed or represented as a complete-document golden. |

The six original inputs must produce `InvalidDocument` at the core's `Safety` phase, not a canonical mismatch or a successful raw-byte equality shortcut. Each is compared against itself and against the admitted document on either side. Section 3.7's DTD refusal does **not** independently qualify subset handling.

Only section 3.2 has a stored golden. No DTD is stripped, entity expanded, declaration rewritten, or published subset relabeled to create additional official success cases. Two explicitly self-authored negative controls in the tests change section 3.2 whitespace; they are not W3C examples.

## Byte provenance

`recommendation-examples.json` records the source URL, section URLs, raw `<code>` inner HTML, SHA-256 of each fragment, complete input octets as Base64, and SHA-256 of those octets. The source response body's SHA-256 is `09c02ef3bc0f8364b00b16fb06092637c08e8f38cf223d47a5862eacc2956bc0`, computed before decoding its ISO-8859-1 HTML. This identifies the retrieved page, not the JSON file or a separately published XML download. Tests use the retained fixtures offline.

The examples are transcribed from the official HTML, not Markdown or an XML serializer:

1. Locate the section heading by its anchor and the first example table after it.
2. Retain the exact inner HTML of its `Input Document` cell's `<code>` element. For section 3.2 also retain the `Canonical Form` cell and both adjacent literal HTML comments. For section 3.7 retain the `Document Subset Expression` cell separately.
3. Remove only HTML-source CR/LF layout characters. Replace each literal `<br/>` with one LF and each literal `&nbsp;` with one ASCII space. HTML-decode once, preserving XML entity references such as `&#169;` rather than decoding them a second time. Do not trim the result; final `<br/>` markers remain final LFs in the affected original inputs.
4. All seven resulting input strings are ASCII. Store those exact octets in Base64 without a BOM or newline conversion. Section 3.2's literal HTML comments independently confirm its three-space/six-space indentation and whitespace-only text. Only the single framing LF at each end of those comments is excluded from that cross-check, as the published note explicitly says the document ends in `>`.

The source's unrendered comments differ from its displayed `<code>` whitespace in some other examples. Those comments are not substituted for the retained code fragments. No expected canonical bytes from refused examples are used.

Fixture discovery follows the existing source-backed repository-root pattern in `XmlSafetyParityFixtureTests.cs`; no project copy rule or new build property is required.
