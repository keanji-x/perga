# Perga

A task-driven organization of AI agents for long-horizon work.

Perga moves multi-day and multi-week work — maintaining a repository, driving engineering OKRs, or running ongoing research — out of scrolling chat tabs. Each goal gets its own isolated **Track**: conversation stays in the side pane while the main view continuously distills a read-only living report backed by kernel-verified facts. Orchestrate the CLIs you already use (Claude Code, Codex, ACP) with lightweight protocols, enforced cross-model review, and zero-token external watches.

## Key Capabilities

- **Living reports over drifting chat.** Keep conversation in the margin and anchor long-running state in a read-only report combining schema-validated ledgers with non-forgeable kernel facts (task status, actual model, gate exit codes, timestamps). Context limits trigger native compaction or report-anchored epoch handover.
- **Heterogeneous agents with shared control.** A single Lead coordinates Claude Code, Codex, and ACP workers inside the same track. Execution is structurally separated from acceptance, reviews can be enforced on a different model, and humans share the exact same live stream, steering, interrupt, and model controls as the Lead.
- **Unified flows and zero-token watches.** A lightweight declarative `flow.json` unifies parallel dependencies, `cron`/`at` schedules, and zero-token external probes (`prChecks` or shell commands). Nothing spends tokens while idle; state changes and exceptions wake the Lead to decide next steps.
- **Real isolated workspaces, local sovereignty.** Isolate tracks via Git worktrees, Jujutsu (`jj`), Linux OverlayFS (shared prewarmed build caches), or remote SSH targets. Terminals run in an independent Rust bridge (`perga-term-bridge`) that survives host upgrades, and all state stays in a local SQLite database you own.
- **Zero-template start, progressive structure.** Start any track with a single sentence. Attach a lightweight built-in protocol (27–40 lines) or ask the Lead to author one only when recurring rules and guardrails emerge.

## Built-in Protocols

- [`agents-delivery`](templates/agents-delivery) — Zero-schema delivery protocol: `builtin:flow` orchestration, enforced read-write verification gates, and enforced read-only cross-model review routing.
- [`okr-loop`](templates/okr-loop) — Stable Objective, four-column KR ledger (`builtin:table`) with kernel-stamped updates, daily `cron` metric collection, enforced triage routing, and `builtin:flow` tracking.
- [`equity-research`](templates/equity-research) — Frameless valuation range axis, event expectation timeline (`builtin:list`), evergreen thesis notes, weekly `cron` valuation refresh, on-demand `at` event checks, and enforced cross-model review routing.

## Quickstart

Download the latest verified archive from GitHub Releases (requires the **ASP.NET Core 11** runtime and at least one configured agent CLI such as `claude`, `codex`, or an ACP provider):

```sh
sha256sum -c perga-linux-x64.tar.gz.sha256
tar -xzf perga-linux-x64.tar.gz
cd perga
PERGA_DATA_DIR="$PWD/data" PERGA_STATIC_WEB_DIR="$PWD/web" \
  dotnet Perga.Host.dll --urls http://127.0.0.1:4041
```

Open [http://127.0.0.1:4041/](http://127.0.0.1:4041/) in your browser (installable as a PWA on desktop and mobile).

### Codex in Linux Sandboxes

Sandboxed tasks store Codex state in an isolated session directory under Perga's data directory. Configure the OpenAI API key for sandboxed tasks via the Host setting `Perga__CodexSandboxApiKey`. Standard Codex CLI ChatGPT login continues to be managed by the CLI itself.

## CI and Releases

Pushes to `main`, pull requests targeting `main`, and manual runs of **CI and release** in GitHub Actions run .NET tests, Rust terminal bridge checks, and frontend lint, unit tests, and production builds. Chromium browser tests run as a separate job. After checks pass, Actions verifies that the packaged host boots, the terminal bridge responds, and static web assets are served, then uploads the `perga-linux-x64` artifact.

Pushing a `vX.Y.Z` tag (for example `v0.1.0`) runs the same pipeline and publishes the verified Linux x64 archive and SHA-256 checksum to GitHub Releases.

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
