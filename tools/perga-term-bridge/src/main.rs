#![forbid(unsafe_code)]

use anyhow::{Context, Result};
use clap::Parser;
use perga_term_bridge::runtime::{PortablePtyBackend, ProcessContainment, SessionManager};
use perga_term_bridge::{ListenConfig, start_server};
use std::io::Write;
use std::net::SocketAddr;
use std::path::PathBuf;
use std::sync::Arc;

#[derive(Debug, Parser)]
#[command(
    name = "perga-term-bridge",
    about = "Perga cross-platform terminal bridge daemon"
)]
struct Cli {
    /// Listen on a Unix Domain Socket path (can be combined with --listen-tcp).
    #[arg(long = "listen-uds")]
    listen_uds: Option<PathBuf>,

    /// Listen on a TCP host:port address, e.g. 127.0.0.1:0 (can be combined with --listen-uds).
    #[arg(long = "listen-tcp")]
    listen_tcp: Option<String>,

    /// Optionally wrap PTY child processes in Linux `unshare` PID/user namespace when supported.
    #[arg(long = "linux-unshare", default_value_t = false)]
    linux_unshare: bool,
}

#[tokio::main]
async fn main() -> Result<()> {
    let cli = Cli::parse();
    let tcp_addr: Option<SocketAddr> = match cli.listen_tcp {
        Some(raw) => Some(
            raw.parse::<SocketAddr>()
                .with_context(|| format!("invalid --listen-tcp socket address: {raw:?}"))?,
        ),
        None => None,
    };

    let containment = if cli.linux_unshare {
        ProcessContainment::UnshareIfAvailable {
            unshare_bin: PathBuf::from("/usr/bin/unshare"),
        }
    } else {
        ProcessContainment::StandardSubprocess
    };

    let manager = Arc::new(SessionManager::new(Arc::new(PortablePtyBackend::new(
        containment,
    ))));
    let server = start_server(
        ListenConfig {
            uds_path: cli.listen_uds,
            tcp_addr,
        },
        manager,
    )
    .await?;

    {
        let mut stdout = std::io::stdout().lock();
        if let Some(uds) = &server.uds_path {
            let _ = writeln!(stdout, "PERGA_TERM_BRIDGE_UDS={}", uds.display());
        }
        if let Some(tcp) = &server.tcp_addr {
            let _ = writeln!(stdout, "PERGA_TERM_BRIDGE_TCP=http://{tcp}");
        }
        let _ = stdout.flush();
    }

    tokio::select! {
        res = server.wait() => res,
        _ = tokio::signal::ctrl_c() => Ok(()),
    }
}
