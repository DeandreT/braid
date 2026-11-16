#![forbid(unsafe_code)]

use wasm_bindgen::prelude::*;
use braid_ferrule_core::edit::{EditFailure, Session};

/// The only JavaScript-facing document is Braid's view. Native project JSON
/// remains opaque bytes and is decoded exclusively by Ferrule.
#[wasm_bindgen]
pub struct EditorSession {
    session: Session,
}

#[wasm_bindgen]
impl EditorSession {
    #[wasm_bindgen(constructor)]
    pub fn new(native: &[u8], operations: &str) -> Result<EditorSession, JsValue> {
        Ok(Self {
            session: Session::open(native, operations.as_bytes()).map_err(error)?,
        })
    }

    pub fn apply(&mut self, operation: &str) -> Result<bool, JsValue> {
        if operation.len() > braid_ferrule_core::edit::MAX_OPERATIONS_BYTES {
            return Err(error(EditFailure::new(
                "operationsLimitExceeded",
                "The operation exceeds 1 MiB.",
            )));
        }
        let operation = serde_json::from_str(operation).map_err(|failure| {
            error(EditFailure::new("invalidOperations", &failure.to_string()))
        })?;
        self.session.apply(operation).map_err(error)
    }

    pub fn undo(&mut self) -> Result<bool, JsValue> {
        self.session.undo().map_err(error)
    }
    pub fn redo(&mut self) -> Result<bool, JsValue> {
        self.session.redo().map_err(error)
    }

    pub fn view(&self) -> Result<String, JsValue> {
        let view = self.session.view("entry.native.json").map_err(error)?;
        serde_json::to_string(&view).map_err(|failure| {
            error(EditFailure::new(
                "resultLimitExceeded",
                &failure.to_string(),
            ))
        })
    }
}

/// Built into the separate qualification artifact only, never the application module.
#[cfg(feature = "qualification")]
#[wasm_bindgen]
pub fn qualify_checkpoint(native: &[u8], operations: &str) -> Result<String, JsValue> {
    let checkpoint = Session::open(native, operations.as_bytes())
        .and_then(|session| session.checkpoint("entry.native.json"))
        .map_err(error)?;
    serde_json::to_string(&serde_json::json!({
        "nativeDigest": checkpoint.native_digest,
        "nativeByteCount": checkpoint.native.len(),
        "validation": checkpoint.view.validation,
        "projection": checkpoint.view.projection,
    }))
    .map_err(|failure| {
        error(EditFailure::new(
            "resultLimitExceeded",
            &failure.to_string(),
        ))
    })
}

fn error(failure: EditFailure) -> JsValue {
    match serde_json::to_string(&failure) {
        Ok(json) => JsValue::from_str(&json),
        Err(_) => JsValue::from_str(
            "{\"code\":\"editFailure\",\"message\":\"Edit failed.\",\"messageTruncated\":false,\"operationIndex\":null}",
        ),
    }
}
