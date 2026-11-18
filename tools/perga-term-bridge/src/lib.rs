#![forbid(unsafe_code)]

//! Perga cross-platform terminal bridge daemon (`perga-term-bridge`).
//! Exposes JSON-RPC 2.0 over HTTP (`POST /rpc`, `POST /`) and binary WebSocket (`GET /ws/{terminal_id}`)
//! over Unix Domain Sockets (`--listen-uds`) and/or TCP (`--listen-tcp`).

pub mod actions;
pub mod rpc;
pub mod runtime;
pub mod screen_diff;
pub mod view;
pub mod wait_plan;
pub mod ws;

use crate::runtime::SessionManager;
use anyhow::{Context, Result, bail};
use axum::Json;
use axum::Router;
use axum::routing::{get, post};
use serde_json::{Value, json};
use std::net::SocketAddr;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use tokio::net::TcpListener;

/// Protocol name exposed over UDS/TCP JSON-RPC 2.0 and binary WebSocket.
pub const BRIDGE_SERVICE_NAME: &str = "perga-term-bridge";

async fn health_handler() -> Json<Value> {
    Json(json!({
        "status": "ok",
        "service": BRIDGE_SERVICE_NAME
    }))
}

/// Build the Axum router serving JSON-RPC (`POST /rpc`, `POST /`), WebSocket (`GET /ws/{terminal_id}`), and `GET /health`.
pub fn build_router(manager: Arc<SessionManager>) -> Router {
    Router::new()
        .route("/", post(rpc::handle_rpc_http))
        .route("/rpc", post(rpc::handle_rpc_http))
        .route("/health", get(health_handler))
        .route("/ws/{terminal_id}", get(ws::handle_ws_upgrade))
        .with_state(manager)
}

#[derive(Clone, Debug, Default)]
pub struct ListenConfig {
    pub uds_path: Option<PathBuf>,
    pub tcp_addr: Option<SocketAddr>,
}

/// Bound endpoints of a running [`BridgeServer`].
pub struct BoundServer {
    pub uds_path: Option<PathBuf>,
    pub tcp_addr: Option<SocketAddr>,
    shutdown_tx: tokio::sync::watch::Sender<bool>,
    tasks: Vec<tokio::task::JoinHandle<Result<()>>>,
}

impl BoundServer {
    pub async fn shutdown(self) -> Result<()> {
        let _ = self.shutdown_tx.send(true);
        for task in self.tasks {
            if let Ok(res) = task.await {
                res?;
            }
        }
        if let Some(uds) = &self.uds_path {
            let _ = std::fs::remove_file(uds);
        }
        Ok(())
    }

    pub async fn wait(self) -> Result<()> {
        let uds_cleanup = self.uds_path.clone();
        let mut set = tokio::task::JoinSet::new();
        for task in self.tasks {
            set.spawn(async move { task.await.unwrap_or_else(|e| Err(anyhow::Error::from(e))) });
        }
        let mut first_err = None;
        while let Some(joined) = set.join_next().await {
            match joined {
                Ok(Ok(())) => {}
                Ok(Err(err)) => {
                    if first_err.is_none() {
                        first_err = Some(err);
                    }
                    let _ = self.shutdown_tx.send(true);
                }
                Err(join_err) => {
                    if first_err.is_none() {
                        first_err = Some(anyhow::Error::from(join_err));
                    }
                    let _ = self.shutdown_tx.send(true);
                }
            }
        }
        if let Some(uds) = &uds_cleanup {
            let _ = std::fs::remove_file(uds);
        }
        match first_err {
            Some(err) => Err(err),
            None => Ok(()),
        }
    }
}

#[cfg(unix)]
fn prepare_uds_listener(path: &Path) -> Result<tokio::net::UnixListener> {
    use std::os::unix::fs::FileTypeExt;
    if let Some(parent) = path.parent()
        && !parent.as_os_str().is_empty()
    {
        std::fs::create_dir_all(parent).with_context(|| {
            format!("failed to create UDS parent directory {}", parent.display())
        })?;
    }
    if let Ok(meta) = std::fs::symlink_metadata(path) {
        if meta.file_type().is_socket() {
            // Probe if an active listener already owns it; only unlink stale sockets.
            if std::os::unix::net::UnixStream::connect(path).is_ok() {
                bail!(
                    "UDS endpoint {} is already in use by an active process",
                    path.display()
                );
            }
            let _ = std::fs::remove_file(path);
        } else {
            bail!(
                "UDS endpoint {} already exists and is not a socket",
                path.display()
            );
        }
    }
    tokio::net::UnixListener::bind(path)
        .with_context(|| format!("failed to bind UDS listener at {}", path.display()))
}

/// Bind TCP and/or UDS listeners and spawn their Axum server loops.
pub async fn start_server(
    config: ListenConfig,
    manager: Arc<SessionManager>,
) -> Result<BoundServer> {
    if config.uds_path.is_none() && config.tcp_addr.is_none() {
        bail!("at least one of --listen-uds or --listen-tcp must be specified");
    }

    let app = build_router(manager);
    let (shutdown_tx, shutdown_rx) = tokio::sync::watch::channel(false);
    let mut tasks = Vec::new();
    let mut bound_tcp = None;
    let mut bound_uds = None;

    if let Some(addr) = config.tcp_addr {
        let listener = TcpListener::bind(addr)
            .await
            .with_context(|| format!("failed to bind TCP listener at {addr}"))?;
        let local_addr = listener.local_addr()?;
        bound_tcp = Some(local_addr);
        let router = app.clone();
        let mut rx = shutdown_rx.clone();
        tasks.push(tokio::spawn(async move {
            axum::serve(listener, router)
                .with_graceful_shutdown(async move {
                    while !*rx.borrow_and_update() {
                        if rx.changed().await.is_err() {
                            break;
                        }
                    }
                })
                .await
                .context("TCP server error")
        }));
    }

    if let Some(uds_path) = config.uds_path {
        #[cfg(unix)]
        {
            let listener = prepare_uds_listener(&uds_path)?;
            bound_uds = Some(uds_path.clone());
            let router = app.clone();
            let mut rx = shutdown_rx.clone();
            tasks.push(tokio::spawn(async move {
                axum::serve(listener, router)
                    .with_graceful_shutdown(async move {
                        while !*rx.borrow_and_update() {
                            if rx.changed().await.is_err() {
                                break;
                            }
                        }
                    })
                    .await
                    .context("UDS server error")
            }));
        }
        #[cfg(not(unix))]
        {
            let _ = uds_path;
            bail!("--listen-uds is only supported on Unix platforms");
        }
    }

    Ok(BoundServer {
        uds_path: bound_uds,
        tcp_addr: bound_tcp,
        shutdown_tx,
        tasks,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn service_name_matches_perga() {
        assert_eq!(BRIDGE_SERVICE_NAME, "perga-term-bridge");
    }
}
