//! Binary WebSocket endpoint `GET /ws/{terminal_id}`.
//! Frame framing:
//! - `0x01 + <raw_pty_bytes>`: bidirectional PTY byte stream (including immediate 64KB ring-buffer replay on connect)
//! - `0x02 + <utf8_json>`: control frames (`{"type":"resize","cols":120,"rows":40}` from client;
//!   `{"type":"exit","exit_code":0}` from server)

use crate::runtime::{ManagedTerminal, SessionBroadcast, SessionManager};
use axum::extract::ws::{Message, WebSocket, WebSocketUpgrade};
use axum::extract::{Path, State};
use axum::http::StatusCode;
use axum::response::{IntoResponse, Response};
use bytes::Bytes;
use serde::Deserialize;
use serde_json::json;
use std::sync::Arc;
use tokio::sync::broadcast::error::RecvError;

pub const FRAME_PTY_DATA: u8 = 0x01;
pub const FRAME_CONTROL_JSON: u8 = 0x02;

#[derive(Debug, Deserialize)]
#[serde(tag = "type", rename_all = "lowercase")]
enum ClientControlFrame {
    Resize { cols: u16, rows: u16 },
}

pub fn encode_pty_frame(raw: &[u8]) -> Bytes {
    let mut framed = Vec::with_capacity(1 + raw.len());
    framed.push(FRAME_PTY_DATA);
    framed.extend_from_slice(raw);
    Bytes::from(framed)
}

pub fn encode_exit_frame(exit_code: Option<i32>) -> Bytes {
    let payload = serde_json::to_vec(&json!({
        "type": "exit",
        "exit_code": exit_code
    }))
    .unwrap_or_else(|_| b"{\"type\":\"exit\",\"exit_code\":null}".to_vec());
    let mut framed = Vec::with_capacity(1 + payload.len());
    framed.push(FRAME_CONTROL_JSON);
    framed.extend_from_slice(&payload);
    Bytes::from(framed)
}

pub async fn handle_ws_upgrade(
    Path(terminal_id): Path<String>,
    State(manager): State<Arc<SessionManager>>,
    ws: WebSocketUpgrade,
) -> Response {
    let Some(terminal) = manager.get(&terminal_id).await else {
        return (
            StatusCode::NOT_FOUND,
            format!("terminal not found: {terminal_id}"),
        )
            .into_response();
    };
    ws.on_upgrade(move |socket| drive_ws_session(socket, terminal))
}

async fn drive_ws_session(mut socket: WebSocket, terminal: Arc<ManagedTerminal>) {
    let mut sub = terminal.attach_ws();

    // Immediately push recent 64KB ring-buffer history as `0x01 + <bytes>` frame.
    if !sub.history.is_empty()
        && socket
            .send(Message::Binary(encode_pty_frame(&sub.history)))
            .await
            .is_err()
    {
        return;
    }

    let mut sent_exit = false;
    if let Some(exit_code) = sub.initial_exit {
        sent_exit = true;
        if socket
            .send(Message::Binary(encode_exit_frame(exit_code)))
            .await
            .is_err()
        {
            return;
        }
    }

    let mut seen_seq = sub.snapshot_seq;
    loop {
        tokio::select! {
            incoming = socket.recv() => {
                match incoming {
                    Some(Ok(Message::Binary(frame))) => {
                        if frame.is_empty() {
                            continue;
                        }
                        match frame[0] {
                            FRAME_PTY_DATA => {
                                let raw = &frame[1..];
                                if !raw.is_empty() {
                                    let _ = terminal.write_raw(raw).await;
                                }
                            }
                            FRAME_CONTROL_JSON => {
                                if let Ok(ctrl) = serde_json::from_slice::<ClientControlFrame>(&frame[1..]) {
                                    match ctrl {
                                        ClientControlFrame::Resize { cols, rows } => {
                                            let _ = terminal.resize(cols, rows);
                                        }
                                    }
                                }
                            }
                            _ => {}
                        }
                    }
                    Some(Ok(Message::Ping(payload))) => {
                        if socket.send(Message::Pong(payload)).await.is_err() {
                            break;
                        }
                    }
                    Some(Ok(Message::Close(_))) | None => break,
                    Some(Ok(_)) => {}
                    Some(Err(_)) => break,
                }
            }
            broadcast_msg = sub.rx.recv() => {
                match broadcast_msg {
                    Ok(SessionBroadcast::Output { seq, bytes }) => {
                        if seq <= seen_seq || bytes.is_empty() {
                            continue;
                        }
                        seen_seq = seq;
                        if socket.send(Message::Binary(encode_pty_frame(&bytes))).await.is_err() {
                            break;
                        }
                    }
                    Ok(SessionBroadcast::Exited { exit_code }) => {
                        if !sent_exit {
                            sent_exit = true;
                            if socket.send(Message::Binary(encode_exit_frame(exit_code))).await.is_err() {
                                break;
                            }
                        }
                    }
                    Err(RecvError::Lagged(_)) => {
                        let (resync_bytes, resync_seq) = terminal.snapshot_history();
                        seen_seq = resync_seq;
                        if !resync_bytes.is_empty()
                            && socket.send(Message::Binary(encode_pty_frame(&resync_bytes))).await.is_err()
                        {
                            break;
                        }
                    }
                    Err(RecvError::Closed) => break,
                }
            }
        }
    }
}
