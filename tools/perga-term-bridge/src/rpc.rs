//! JSON-RPC 2.0 dispatcher over HTTP (`POST /rpc` and `POST /`).
//! Implements `term.ensure`, `term.observe`, `term.action`, `term.resize`, and `term.kill`.

use crate::BRIDGE_SERVICE_NAME;
use crate::runtime::{EnsureSpec, SessionManager};
use crate::view::Cursor;
use crate::wait_plan::WaitPlan;
use axum::Json;
use axum::extract::State;
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::collections::BTreeMap;
use std::path::PathBuf;
use std::sync::Arc;

pub const JSONRPC_PARSE_ERROR: i32 = -32700;
pub const JSONRPC_INVALID_REQUEST: i32 = -32600;
pub const JSONRPC_METHOD_NOT_FOUND: i32 = -32601;
pub const JSONRPC_INVALID_PARAMS: i32 = -32602;
pub const JSONRPC_TERMINAL_NOT_FOUND: i32 = -32001;
pub const JSONRPC_TERMINAL_ERROR: i32 = -32000;

#[derive(Debug, Deserialize)]
pub struct JsonRpcRequest {
    pub jsonrpc: Option<String>,
    #[serde(default)]
    pub id: Value,
    pub method: String,
    #[serde(default)]
    pub params: Value,
}

#[derive(Debug, Serialize)]
pub struct JsonRpcResponse {
    pub jsonrpc: &'static str,
    pub id: Value,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<JsonRpcError>,
}

#[derive(Debug, Serialize)]
pub struct JsonRpcError {
    pub code: i32,
    pub message: String,
}

impl JsonRpcResponse {
    pub fn ok(id: Value, result: Value) -> Self {
        Self {
            jsonrpc: "2.0",
            id,
            result: Some(result),
            error: None,
        }
    }

    pub fn err(id: Value, code: i32, message: impl Into<String>) -> Self {
        Self {
            jsonrpc: "2.0",
            id,
            result: None,
            error: Some(JsonRpcError {
                code,
                message: message.into(),
            }),
        }
    }
}

fn default_cols() -> u16 {
    120
}

fn default_rows() -> u16 {
    40
}

fn default_grace_ms() -> u64 {
    2000
}

#[derive(Debug, Deserialize)]
struct EnsureParams {
    terminal_id: String,
    program: String,
    #[serde(default)]
    args: Vec<String>,
    cwd: String,
    #[serde(default)]
    env: BTreeMap<String, String>,
    #[serde(default = "default_cols")]
    cols: u16,
    #[serde(default = "default_rows")]
    rows: u16,
}

#[derive(Debug, Deserialize)]
struct ObserveParams {
    terminal_id: String,
    #[serde(default)]
    scroll_offset: usize,
    #[serde(default)]
    wait_for: Option<String>,
    #[serde(default)]
    wait_ms: Option<u64>,
    #[serde(default)]
    settle_ms: Option<u64>,
    #[serde(default)]
    wait_text: Option<Vec<String>>,
    #[serde(default)]
    wait_text_absent: Option<Vec<String>>,
}

#[derive(Debug, Serialize)]
struct RpcObserveFrame {
    cols: u16,
    rows: u16,
    cursor: Cursor,
    alternate: bool,
    scroll_offset: usize,
    history_rows: usize,
    modes: u32,
    text: Vec<String>,
}

#[derive(Debug, Serialize)]
struct RpcObserveResult {
    revision: u64,
    frame: RpcObserveFrame,
    exited: bool,
    exit_code: Option<i32>,
}

#[derive(Debug, Deserialize)]
struct ActionParams {
    terminal_id: String,
    kind: String,
    #[serde(default)]
    payload: Value,
}

#[derive(Debug, Deserialize)]
struct ResizeParams {
    terminal_id: String,
    cols: u16,
    rows: u16,
}

#[derive(Debug, Deserialize)]
struct KillParams {
    terminal_id: String,
    #[serde(default = "default_grace_ms")]
    grace_ms: u64,
}

pub async fn handle_rpc_http(
    State(manager): State<Arc<SessionManager>>,
    body: String,
) -> Json<Value> {
    let parsed: Value = match serde_json::from_str(&body) {
        Ok(v) => v,
        Err(err) => {
            let resp = JsonRpcResponse::err(
                Value::Null,
                JSONRPC_PARSE_ERROR,
                format!("invalid JSON payload: {err}"),
            );
            return Json(serde_json::to_value(resp).unwrap_or(Value::Null));
        }
    };

    if let Some(batch) = parsed.as_array() {
        if batch.is_empty() {
            let resp =
                JsonRpcResponse::err(Value::Null, JSONRPC_INVALID_REQUEST, "empty JSON-RPC batch");
            return Json(serde_json::to_value(resp).unwrap_or(Value::Null));
        }
        let mut results = Vec::with_capacity(batch.len());
        for item in batch {
            let resp = dispatch_one(&manager, item.clone()).await;
            results.push(serde_json::to_value(resp).unwrap_or(Value::Null));
        }
        return Json(Value::Array(results));
    }

    let resp = dispatch_one(&manager, parsed).await;
    Json(serde_json::to_value(resp).unwrap_or(Value::Null))
}

pub async fn dispatch_one(manager: &SessionManager, raw: Value) -> JsonRpcResponse {
    let id = raw.get("id").cloned().unwrap_or(Value::Null);
    let req: JsonRpcRequest = match serde_json::from_value(raw) {
        Ok(r) => r,
        Err(err) => {
            return JsonRpcResponse::err(
                id,
                JSONRPC_INVALID_REQUEST,
                format!("invalid JSON-RPC request: {err}"),
            );
        }
    };

    if let Some(ver) = &req.jsonrpc
        && ver != "2.0"
    {
        return JsonRpcResponse::err(
            req.id,
            JSONRPC_INVALID_REQUEST,
            format!("unsupported jsonrpc version {ver:?}; expected \"2.0\""),
        );
    }

    match req.method.as_str() {
        "bridge.ping" => JsonRpcResponse::ok(
            req.id,
            json!({ "ok": true, "service": BRIDGE_SERVICE_NAME }),
        ),
        "term.ensure" => {
            let params: EnsureParams = match serde_json::from_value(req.params) {
                Ok(p) => p,
                Err(err) => {
                    return JsonRpcResponse::err(
                        req.id,
                        JSONRPC_INVALID_PARAMS,
                        format!("invalid term.ensure params: {err}"),
                    );
                }
            };
            let spec = EnsureSpec {
                terminal_id: params.terminal_id,
                program: params.program,
                args: params.args,
                cwd: PathBuf::from(params.cwd),
                env: params.env,
                cols: params.cols,
                rows: params.rows,
            };
            match manager.ensure(spec).await {
                Ok(outcome) => JsonRpcResponse::ok(
                    req.id,
                    json!({
                        "terminal_id": outcome.terminal_id,
                        "pid": outcome.pid,
                        "created": outcome.created
                    }),
                ),
                Err(err) => {
                    JsonRpcResponse::err(req.id, JSONRPC_TERMINAL_ERROR, format!("{err:#}"))
                }
            }
        }
        "term.observe" => {
            let params: ObserveParams = match serde_json::from_value(req.params) {
                Ok(p) => p,
                Err(err) => {
                    return JsonRpcResponse::err(
                        req.id,
                        JSONRPC_INVALID_PARAMS,
                        format!("invalid term.observe params: {err}"),
                    );
                }
            };
            let wait_plan = match WaitPlan::from_observe_rpc(
                params.wait_for.as_deref(),
                params.wait_ms,
                params.settle_ms,
                params.wait_text,
                params.wait_text_absent,
            ) {
                Ok(plan) => plan,
                Err(err) => {
                    return JsonRpcResponse::err(
                        req.id,
                        JSONRPC_INVALID_PARAMS,
                        format!("invalid term.observe wait parameters: {err:#}"),
                    );
                }
            };
            let Some(terminal) = manager.get(&params.terminal_id).await else {
                return JsonRpcResponse::err(
                    req.id,
                    JSONRPC_TERMINAL_NOT_FOUND,
                    format!("terminal not found: {}", params.terminal_id),
                );
            };
            match terminal.observe(params.scroll_offset, wait_plan).await {
                Ok(obs) => {
                    let result = RpcObserveResult {
                        revision: obs.revision,
                        frame: RpcObserveFrame {
                            cols: obs.frame.cols,
                            rows: obs.frame.rows,
                            cursor: obs.frame.cursor,
                            alternate: obs.frame.alternate,
                            scroll_offset: obs.frame.scroll_offset,
                            history_rows: obs.frame.history_rows,
                            modes: obs.frame.modes,
                            text: obs.frame.text,
                        },
                        exited: obs.exited,
                        exit_code: obs.exit_code,
                    };
                    match serde_json::to_value(result) {
                        Ok(val) => JsonRpcResponse::ok(req.id, val),
                        Err(err) => JsonRpcResponse::err(
                            req.id,
                            JSONRPC_TERMINAL_ERROR,
                            format!("failed to serialize observation: {err}"),
                        ),
                    }
                }
                Err(err) => {
                    JsonRpcResponse::err(req.id, JSONRPC_TERMINAL_ERROR, format!("{err:#}"))
                }
            }
        }
        "term.action" => {
            let params: ActionParams = match serde_json::from_value(req.params) {
                Ok(p) => p,
                Err(err) => {
                    return JsonRpcResponse::err(
                        req.id,
                        JSONRPC_INVALID_PARAMS,
                        format!("invalid term.action params: {err}"),
                    );
                }
            };
            let Some(terminal) = manager.get(&params.terminal_id).await else {
                return JsonRpcResponse::err(
                    req.id,
                    JSONRPC_TERMINAL_NOT_FOUND,
                    format!("terminal not found: {}", params.terminal_id),
                );
            };
            let kind = params.kind.to_ascii_lowercase();
            match terminal.send_action(&kind, &params.payload).await {
                Ok(ack_revision) => {
                    JsonRpcResponse::ok(req.id, json!({ "ack_revision": ack_revision }))
                }
                Err(err) => {
                    JsonRpcResponse::err(req.id, JSONRPC_TERMINAL_ERROR, format!("{err:#}"))
                }
            }
        }
        "term.resize" => {
            let params: ResizeParams = match serde_json::from_value(req.params) {
                Ok(p) => p,
                Err(err) => {
                    return JsonRpcResponse::err(
                        req.id,
                        JSONRPC_INVALID_PARAMS,
                        format!("invalid term.resize params: {err}"),
                    );
                }
            };
            let Some(terminal) = manager.get(&params.terminal_id).await else {
                return JsonRpcResponse::err(
                    req.id,
                    JSONRPC_TERMINAL_NOT_FOUND,
                    format!("terminal not found: {}", params.terminal_id),
                );
            };
            match terminal.resize(params.cols, params.rows) {
                Ok(()) => JsonRpcResponse::ok(req.id, json!({ "ok": true })),
                Err(err) => {
                    JsonRpcResponse::err(req.id, JSONRPC_TERMINAL_ERROR, format!("{err:#}"))
                }
            }
        }
        "term.kill" => {
            let params: KillParams = match serde_json::from_value(req.params) {
                Ok(p) => p,
                Err(err) => {
                    return JsonRpcResponse::err(
                        req.id,
                        JSONRPC_INVALID_PARAMS,
                        format!("invalid term.kill params: {err}"),
                    );
                }
            };
            let Some(terminal) = manager.get(&params.terminal_id).await else {
                return JsonRpcResponse::err(
                    req.id,
                    JSONRPC_TERMINAL_NOT_FOUND,
                    format!("terminal not found: {}", params.terminal_id),
                );
            };
            match terminal.kill(params.grace_ms).await {
                Ok(outcome) => JsonRpcResponse::ok(
                    req.id,
                    json!({
                        "exited": outcome.exited,
                        "exit_code": outcome.exit_code
                    }),
                ),
                Err(err) => {
                    JsonRpcResponse::err(req.id, JSONRPC_TERMINAL_ERROR, format!("{err:#}"))
                }
            }
        }
        unknown => JsonRpcResponse::err(
            req.id,
            JSONRPC_METHOD_NOT_FOUND,
            format!("unknown JSON-RPC method: {unknown}"),
        ),
    }
}
