#![forbid(unsafe_code)]

use anyhow::Result;
use bytes::Bytes;
use futures_util::{SinkExt, StreamExt};
use http_body_util::{BodyExt, Full};
use hyper::Request;
use hyper_util::client::legacy::Client;
use hyper_util::rt::TokioExecutor;
use perga_term_bridge::runtime::RING_BUFFER_CAPACITY;
use perga_term_bridge::ws::{FRAME_CONTROL_JSON, FRAME_PTY_DATA};
use serde_json::{Value, json};
use std::io::{BufRead, BufReader};
use std::process::{Child, Command, Stdio};
use std::time::Duration;
use tempfile::TempDir;
use tokio_tungstenite::tungstenite::Message as WsMessage;

struct BridgeProcess {
    child: Child,
    _dir: TempDir,
    uds_path: std::path::PathBuf,
    tcp_url: String,
}

impl BridgeProcess {
    fn spawn_dual() -> Result<Self> {
        let dir = tempfile::tempdir()?;
        let uds_path = dir.path().join("bridge.sock");
        let mut child = Command::new(env!("CARGO_BIN_EXE_perga-term-bridge"))
            .arg("--listen-uds")
            .arg(&uds_path)
            .arg("--listen-tcp")
            .arg("127.0.0.1:0")
            .arg("--linux-unshare")
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()?;

        let stdout = child.stdout.take().expect("piped stdout");
        let mut reader = BufReader::new(stdout);
        let mut seen_uds = None;
        let mut seen_tcp = None;

        for _ in 0..10 {
            let mut line = String::new();
            let n = reader.read_line(&mut line)?;
            if n == 0 {
                break;
            }
            let trimmed = line.trim();
            if let Some(rest) = trimmed.strip_prefix("PERGA_TERM_BRIDGE_UDS=") {
                seen_uds = Some(std::path::PathBuf::from(rest));
            } else if let Some(rest) = trimmed.strip_prefix("PERGA_TERM_BRIDGE_TCP=") {
                seen_tcp = Some(rest.to_owned());
            }
            if seen_uds.is_some() && seen_tcp.is_some() {
                break;
            }
        }

        Ok(Self {
            child,
            _dir: dir,
            uds_path: seen_uds.expect("missing PERGA_TERM_BRIDGE_UDS banner"),
            tcp_url: seen_tcp.expect("missing PERGA_TERM_BRIDGE_TCP banner"),
        })
    }
}

impl Drop for BridgeProcess {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

async fn rpc_tcp(base_url: &str, method: &str, params: Value) -> Result<Value> {
    let client = Client::builder(TokioExecutor::new()).build_http::<Full<Bytes>>();
    let uri: hyper::Uri = format!("{base_url}/rpc").parse()?;
    let payload = serde_json::to_vec(&json!({
        "jsonrpc": "2.0",
        "id": 1,
        "method": method,
        "params": params
    }))?;
    let req = Request::builder()
        .method("POST")
        .uri(uri)
        .header("content-type", "application/json")
        .body(Full::new(Bytes::from(payload)))?;
    let resp = client.request(req).await?;
    let body = resp.into_body().collect().await?.to_bytes();
    let json_resp: Value = serde_json::from_slice(&body)?;
    if let Some(err) = json_resp.get("error") {
        anyhow::bail!("JSON-RPC error: {err}");
    }
    Ok(json_resp["result"].clone())
}

#[cfg(unix)]
async fn rpc_uds(uds_path: &std::path::Path, method: &str, params: Value) -> Result<Value> {
    let stream = tokio::net::UnixStream::connect(uds_path).await?;
    let io = hyper_util::rt::TokioIo::new(stream);
    let (mut sender, conn) = hyper::client::conn::http1::handshake(io).await?;
    tokio::spawn(async move {
        let _ = conn.await;
    });

    let payload = serde_json::to_vec(&json!({
        "jsonrpc": "2.0",
        "id": 1,
        "method": method,
        "params": params
    }))?;
    let req = Request::builder()
        .method("POST")
        .uri("http://localhost/rpc")
        .header("host", "localhost")
        .header("content-type", "application/json")
        .body(Full::new(Bytes::from(payload)))?;
    let resp = sender.send_request(req).await?;
    let body = resp.into_body().collect().await?.to_bytes();
    let json_resp: Value = serde_json::from_slice(&body)?;
    if let Some(err) = json_resp.get("error") {
        anyhow::bail!("JSON-RPC UDS error: {err}");
    }
    Ok(json_resp["result"].clone())
}

#[tokio::test]
async fn dual_listener_jsonrpc_and_ws_64kb_replay_and_exit_roundtrip() -> Result<()> {
    let bridge = BridgeProcess::spawn_dual()?;
    let cwd = std::env::temp_dir();

    // 1. Ensure over UDS, then verify idempotent ensure over TCP.
    let first = rpc_uds(
        &bridge.uds_path,
        "term.ensure",
        json!({
            "terminal_id": "t_dual",
            "program": "/bin/sh",
            "args": [],
            "cwd": cwd.to_string_lossy(),
            "env": { "PERGA_BRIDGE_MARKER": "verified" },
            "cols": 100,
            "rows": 30
        }),
    )
    .await?;
    assert_eq!(first["terminal_id"], "t_dual");
    assert_eq!(first["created"], true);
    let pid = first["pid"].as_i64().unwrap();
    assert!(pid > 0);

    let second = rpc_tcp(
        &bridge.tcp_url,
        "term.ensure",
        json!({
            "terminal_id": "t_dual",
            "program": "/bin/sh",
            "args": [],
            "cwd": cwd.to_string_lossy(),
            "env": {},
            "cols": 100,
            "rows": 30
        }),
    )
    .await?;
    assert_eq!(second["created"], false);
    assert_eq!(second["pid"].as_i64().unwrap(), pid);

    // 2. Emit >64KB output so ring buffer caps at exactly 64KB, then print sentinel.
    let action_res = rpc_tcp(
        &bridge.tcp_url,
        "term.action",
        json!({
            "terminal_id": "t_dual",
            "kind": "submit",
            "payload": {
                "text": "dd if=/dev/zero bs=1024 count=80 2>/dev/null | tr '\\0' 'A'; printf '\\nHIST_TAIL_%s\\n' \"$PERGA_BRIDGE_MARKER\""
            }
        }),
    )
    .await?;
    assert!(action_res["ack_revision"].as_u64().unwrap() >= 1);

    let obs = rpc_tcp(
        &bridge.tcp_url,
        "term.observe",
        json!({
            "terminal_id": "t_dual",
            "scroll_offset": 0,
            "wait_for": "text",
            "wait_ms": 5000,
            "settle_ms": 100,
            "wait_text": ["HIST_TAIL_verified"],
            "wait_text_absent": []
        }),
    )
    .await?;
    assert_eq!(obs["exited"], false);
    let joined = obs["frame"]["text"]
        .as_array()
        .unwrap()
        .iter()
        .filter_map(Value::as_str)
        .collect::<Vec<_>>()
        .join("\n");
    assert!(joined.contains("HIST_TAIL_verified"), "{joined}");

    // 3. Connect WebSocket to `/ws/t_dual` and verify immediate 64KB `0x01` replay frame.
    let ws_url = format!("{}/ws/t_dual", bridge.tcp_url.replacen("http://", "ws://", 1));
    let (mut ws, _) = tokio_tungstenite::connect_async(&ws_url).await?;

    let first_frame = tokio::time::timeout(Duration::from_secs(3), ws.next())
        .await?
        .expect("ws stream ended")?;
    let WsMessage::Binary(replay_frame) = first_frame else {
        anyhow::bail!("expected binary replay frame, got {first_frame:?}");
    };
    assert_eq!(replay_frame[0], FRAME_PTY_DATA);
    assert_eq!(replay_frame.len() - 1, RING_BUFFER_CAPACITY);
    let replay_str = String::from_utf8_lossy(&replay_frame[1..]);
    assert!(replay_str.contains("HIST_TAIL_verified"));

    // 4. Send `0x02` resize control frame over WS and verify via `term.observe`.
    let mut resize_ctrl = vec![FRAME_CONTROL_JSON];
    resize_ctrl.extend_from_slice(br#"{"type":"resize","cols":132,"rows":42}"#);
    ws.send(WsMessage::Binary(Bytes::from(resize_ctrl))).await?;

    tokio::time::sleep(Duration::from_millis(60)).await;
    let resized_obs = rpc_uds(
        &bridge.uds_path,
        "term.observe",
        json!({
            "terminal_id": "t_dual",
            "wait_for": "none"
        }),
    )
    .await?;
    assert_eq!(resized_obs["frame"]["cols"], 132);
    assert_eq!(resized_obs["frame"]["rows"], 42);

    // 5. Send `0x01` live command over WS that prints live marker and exits with code 9.
    let mut cmd_frame = vec![FRAME_PTY_DATA];
    cmd_frame.extend_from_slice(b"printf 'WS_LIVE_OK\\n'; exit 9\r");
    ws.send(WsMessage::Binary(Bytes::from(cmd_frame))).await?;

    let mut collected_live = String::new();
    let mut exit_ctrl: Option<Value> = None;
    let deadline = tokio::time::Instant::now() + Duration::from_secs(5);
    while tokio::time::Instant::now() < deadline {
        let next = tokio::time::timeout(Duration::from_secs(2), ws.next()).await?;
        let Some(Ok(msg)) = next else {
            break;
        };
        if let WsMessage::Binary(data) = msg
            && !data.is_empty()
        {
            match data[0] {
                FRAME_PTY_DATA => {
                    collected_live.push_str(&String::from_utf8_lossy(&data[1..]));
                }
                FRAME_CONTROL_JSON => {
                    let ctrl: Value = serde_json::from_slice(&data[1..])?;
                    if ctrl["type"] == "exit" {
                        exit_ctrl = Some(ctrl);
                        break;
                    }
                }
                _ => {}
            }
        }
    }
    assert!(collected_live.contains("WS_LIVE_OK"), "{collected_live}");
    let exit_ctrl = exit_ctrl.expect("expected 0x02 exit control frame");
    assert_eq!(exit_ctrl["exit_code"], 9);

    // 6. Verify `term.kill` on an active process terminates cleanly and reports exited=true.
    let _ = rpc_tcp(
        &bridge.tcp_url,
        "term.ensure",
        json!({
            "terminal_id": "t_kill",
            "program": "/bin/sh",
            "args": ["-c", "sleep 30"],
            "cwd": cwd.to_string_lossy(),
            "env": {},
            "cols": 80,
            "rows": 24
        }),
    )
    .await?;
    let killed = rpc_tcp(
        &bridge.tcp_url,
        "term.kill",
        json!({
            "terminal_id": "t_kill",
            "grace_ms": 300
        }),
    )
    .await?;
    assert_eq!(killed["exited"], true);

    Ok(())
}
