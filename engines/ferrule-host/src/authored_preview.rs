//! Independent authored-preview dispatch. Saved preview parsers never receive
//! this request shape; framing and execution limits remain owned by its core handler.
use std::io::{self, Read, Write};

use braid_ferrule_core::authored_document::transport;
use serde::Deserialize;

pub const MAX_HEADER_BYTES: usize = transport::MAX_HEADER_BYTES;

/// None leaves both the payload reader and writer untouched for another route.
pub fn try_handle(
    header: &[u8],
    reader: &mut impl Read,
    writer: &mut impl Write,
) -> Option<io::Result<()>> {
    #[derive(Deserialize)]
    struct ProtocolProbe {
        #[serde(rename = "protocolVersion")]
        protocol_version: Option<String>,
    }
    if serde_json::from_slice::<ProtocolProbe>(header)
        .is_ok_and(|p| p.protocol_version.as_deref() == Some(transport::PROTOCOL))
    {
        Some(transport::handle(header, reader, writer))
    } else {
        None
    }
}
