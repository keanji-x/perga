# Perga

A task-driven organization of AI agents for long-horizon work.

The Perga Host and local terminal bridge run natively on **macOS on Apple Silicon (arm64)** and **Linux x64**. Both packages include the web interface. See the [quickstart](#quickstart) for installation and [runtime differences](#runtime-differences) for platform-specific features.

Perga moves multi-day and multi-week work — maintaining a repository, driving engineering OKRs, or running ongoing research — out of scrolling chat tabs. Each goal gets its own isolated **Track**: conversation stays in the side pane while the main view continuously distills a read-only living report backed by kernel-verified facts. Orchestrate the CLIs you already use (Claude Code, Codex, ACP) with lightweight protocols, enforced cross-model review, and zero-token external watches.

## Key Capabilities

- **Living reports over drifting chat.** Keep conversation in the margin and anchor long-running state in a read-only report combining schema-validated ledgers with non-forgeable kernel facts (task status, actual model, gate exit codes, timestamps). Context limits trigger native compaction or report-anchored epoch handover.
- **Heterogeneous agents with shared control.** A single Lead coordinates Claude Code, Codex, and ACP workers inside the same track. Execution is structurally separated from acceptance, reviews can be enforced on a different model, and humans share the exact same live stream, steering, interrupt, and model controls as the Lead.
- **Unified flows and zero-token watches.** A lightweight declarative `flow.json` unifies parallel dependencies, `cron`/`at` schedules, and zero-token external probes (`prChecks` or shell commands). Nothing spends tokens while idle; state changes and exceptions wake the Lead to decide next steps.
- **Real isolated workspaces, local sovereignty.** Isolate tracks via Git worktrees, Jujutsu (`jj`), Linux OverlayFS (shared prewarmed build caches), or remote SSH targets. Terminals run in a Rust bridge (`perga-term-bridge`), and all state stays in a local SQLite database you own.
- **Zero-template start, progressive structure.** Start any track with a single sentence. Attach a lightweight built-in protocol (27–40 lines) or ask the Lead to author one only when recurring rules and guardrails emerge.

## Built-in Protocols

- [`agents-delivery`](templates/agents-delivery) — Zero-schema delivery protocol: `builtin:flow` orchestration, enforced read-write verification gates, and enforced read-only cross-model review routing.
- [`okr-loop`](templates/okr-loop) — Stable Objective, four-column KR ledger (`builtin:table`) with kernel-stamped updates, daily `cron` metric collection, enforced triage routing, and `builtin:flow` tracking.
- [`equity-research`](templates/equity-research) — Frameless valuation range axis, event expectation timeline (`builtin:list`), evergreen thesis notes, weekly `cron` valuation refresh, on-demand `at` event checks, and enforced cross-model review routing.

## Quickstart

Install the **ASP.NET Core 11 runtime** for your platform. The bundled OAR bridge uses **Node.js 24 or later** to run agent sessions. Install and configure the provider CLI you want to use, such as `claude` or `codex`.

Download the archive and its matching `.sha256` file from the same [GitHub Release](https://github.com/keanji-x/perga/releases). To try a CI build, download and unzip the matching package artifact from the workflow run. In the directory containing both files, use the commands for your machine.

**Linux x64** (`uname -sm` prints `Linux x86_64`)

```sh
sha256sum -c perga-linux-x64.tar.gz.sha256 && tar -xzf perga-linux-x64.tar.gz
```

**macOS on Apple Silicon** (`uname -sm` prints `Darwin arm64`)

```sh
shasum -a 256 -c perga-darwin-arm64.tar.gz.sha256 && tar -xzf perga-darwin-arm64.tar.gz
```

On either platform, enter the extracted directory. Set a persistent data directory outside the package, or keep your existing data directory when upgrading. On first use, create the owner account and choose its password at the prompt.

```sh
cd perga
export PERGA_DATA_DIR="${PERGA_DATA_DIR:-$HOME/.local/share/perga}"
dotnet Perga.Host.dll auth init-owner --username owner --allow-migrate
```

To start the Host, run the following from the same `perga` directory. Use the same data directory on subsequent starts to keep your owner account and tracks.

```sh
PERGA_DATA_DIR="${PERGA_DATA_DIR:-$HOME/.local/share/perga}" PERGA_STATIC_WEB_DIR="$PWD/web" \
  dotnet Perga.Host.dll --urls http://127.0.0.1:4041
```

Open [Perga on localhost](http://127.0.0.1:4041/) and sign in. The web app is installable as a PWA. The Host starts the bundled native terminal bridge when needed.

### Build from source

Start in the repository root on the target machine with the .NET 11 SDK, Node.js 24 or later, npm, rustup and a native C linker installed. On macOS, Xcode Command Line Tools supply the linker.

```sh
(cd tools/perga-oar-bridge && npm ci && npm run build)
(cd fe && npm ci && npm run build)
case "$(uname -sm)" in
  "Linux x86_64") target=x86_64-unknown-linux-musl ;;
  "Darwin arm64") target=aarch64-apple-darwin ;;
  *) echo "Unsupported platform" >&2; exit 1 ;;
esac
rustup target add "$target"
(cd tools/perga-term-bridge && cargo build --locked --release --target "$target")
out=$(mktemp -d)
bash tools/publish-host.sh "$out/perga" "tools/perga-term-bridge/target/$target/release/perga-term-bridge"
cp -R fe/web/dist "$out/perga/web"
cd "$out/perga"
```

You are now in the package directory. On first use, start with `export PERGA_DATA_DIR` in the owner setup above, then run the Host command.

### Runtime differences

| Feature | Platform behavior |
| --- | --- |
| Track workspaces | The OverlayFS driver shares prewarmed snapshots on a Linux Host with the mount helper. On macOS, the workspace picker offers Git worktrees, Jujutsu workspaces, directory copies, or direct access according to the source and installed tools. |
| Local terminals | macOS uses its native pseudo-terminal (PTY). The optional `Perga__EnableTerminalLinuxUnshare` setting uses Linux user and PID namespaces when available and defaults to off. |
| Terminals over SSH | Perga can copy its bridge automatically between Linux machines with matching x86-64 or arm64 CPUs. With a macOS Host, install a bridge built for the target on its `PATH`, or set the target's `TerminalBridgeExecutablePath` to that executable. |
| Workspace files | Linux can read an opened file's path and list an opened directory through `/proc/self/fd`. macOS checks local files with `fgetattrlist` and lists directories by their resolved path. Over SSH, the extra check on an opened file depends on the **target** exposing `/proc/self/fd`. |
| Remote command cancellation | On an SSH target with the `setsid` utility, Perga starts remote shell commands and agent subprocesses in their own process group. Without it, cancellation can stop the tracked process while leaving its children running. |

On macOS, use the physical path for workspaces under the system temporary directory. File and template lookups can fail when `/var/` and `/private/var/` refer to the same workspace. Run migrations against the same database one at a time to avoid a file-sharing error.

### Linux-specific deployment

The managed installer and hot-upgrade services use Linux systemd user units. See [managed installation](docs/design/install-and-upgrade.md) and [OverlayFS prerequisites](docs/track-overlayfs.md).

## CI and Releases

Pushes to `main`, pull requests targeting `main`, and manual runs of **CI and release** run the .NET, Rust, OAR and frontend checks. Chromium tests run in a separate advisory job. Linux x64 uses the tested static musl terminal bridge. The macOS arm64 runner builds and tests its native bridge. Each runner extracts its archive, boots the Host with eager terminal bridge startup, checks `/healthz`, and fetches the JavaScript entry named by the served homepage.

Actions uploads `perga-linux-x64` and `perga-darwin-arm64`. Pushing a `vX.Y.Z` tag (for example `v0.1.0`) publishes both verified archives and their SHA-256 checksums to GitHub Releases.

## Documentation

### Product
- [Vision and Fundamental Tensions](docs/product/vision.md) — Core premise, four orthogonal tensions, AI vs. human organizational asymmetries, and long-term vision
- [Product Design and Prioritized Pain Points](docs/product/design.md) — Concrete `P0`–`P3` pain points, minimal solutions, deliberate trade-offs, deferred scope, and paradigm comparison
- [Scenario Overview](docs/product/scenarios/README.md) — Progressive complexity across `agents-delivery`, `okr-loop`, and `equity-research`
- [Software Development Scenarios](docs/product/scenarios/software-development.md) — Bottom-up user flows across 6 engineering scenarios (bug fixing, features, RFC design, module maintenance, ops, and refactoring), commonalities, differences, and design questions

### Architecture & Operations
- [Architecture Reference](DESIGN.md) — 18-project .NET topology, dependency matrix, core modules, and contracts
- [Unified Track Flow Architecture](docs/design/unified-track-flow-architecture.md) — Declarative `flow.json` engine and read-only `builtin:flow` projection
- [Template Taste & Information Design](docs/design/template-taste.md) — Editorial read-only report principles
- [Linux OverlayFS Workspaces](docs/track-overlayfs.md) — Prewarmed build cache sharing and namespace mount setup
- [Self-Hosted Installation & Hot Upgrades](docs/design/install-and-upgrade.md) — Dual systemd user services and zero-downtime upgrades

## License

Licensed under the [Apache License 2.0](LICENSE).
