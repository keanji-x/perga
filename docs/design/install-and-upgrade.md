# Perga install and hot upgrade — Phases 1 and 2

## Outcome and delivery boundary

One user-owned installation has immutable, verified releases and independently
managed Host and terminal bridge processes. A web/templates update does not
restart either process. A Host update drains dispatch, restarts only the Host,
and rolls back automatically if verification fails.

Phase 1 delivers `tools/perga-deploy/perga-ctl`, its Python 3 standard-library
controller, installer, and isolated tests. Phase 2 implements the Host system API,
Kernel drain admission, Lead-only MCP handoff, and durable receipt consumer.
The Web UI banner and refresh flow below remain contracts for a subsequent phase.
See [the backend implementation design](system-upgrade-phase2.md) for boundaries
and verification. `Program.cs` exposes `/healthz` and the Phase 2 system endpoints,
`PergaHostOptions` accepts the external bridge endpoint, and
`SqliteMigrator` makes `perga.db.bak-v{N}` before forward migrations. The CLI
requires the new API; it never interprets an unavailable endpoint as idle.
This is an L2 change because deployment controls process and persistent state;
the Planner arranges the two independent reviews.

## Installation layout

Managed instances keep operator settings in `config/perga.toml`. The Python
launcher and release controller share the same parser. The launcher passes
managed paths, provider executables and Worker home to the Host's existing
configuration binder. The proxy section sets standard proxy variables in the
launched process so provider CLIs can use them. Login files stay in the provider
home directory. Existing `config.env` installations continue to start and upgrade
with their current configuration. TOML takes precedence when both files exist.

Default root is `~/.local/share/perga`; use `--root` and `--name` for multiple
instances (for example a separately managed `perga-4240`). Never run the examples
against an existing live instance without an explicit maintenance window.

```text
<root>/
  data/                         # stable DB, workspaces, all mutable application data
    perga.db
    perga.db.bak-v{N}            # online SQLite snapshot before schema upgrade
    perga.db.failed-<timestamp>  # failed database and matching -wal/-shm files
  config/perga.toml              # stable instance configuration, mode 0600
  bin/perga-ctl, bin/*.py     # stable controller and Host launchers
  releases/<id>/
    manifest.json
    host/Perga.Host              # executable apphost plus publish dependencies
    web/                        # fe/web/dist contents
    templates/
    termBridge/perga-term-bridge
  current -> releases/<id>
  previous -> releases/<old-id>
  runtime/
    upgrade.lock                # flock inode is permanent; never unlink it
    upgrade-state.json          # durable intent and last transition
    upgrade-result.json         # terminal receipt
    system-management-token     # Host-generated owner-only capability for perga-ctl
    upgrade.log                 # detached runner output
```

Root/runtime permissions are 0700; processes run as the installing user. Release
IDs match `[A-Za-z0-9][A-Za-z0-9._-]{0,127}`. Links are relative and replaced by
`rename` on the same filesystem, followed by directory fsync. Releases are copied
into staging, verified, renamed once, and never edited in place by the controller.
Don't put mutable configuration, uploads, or SQLite inside a release. This is
same-user orchestration, not protection from a compromised account.

Manifest format 1 is JSON with `formatVersion: 1`, `releaseId`, nonnegative integer
`schemaVersion` (maximum migration version supported by the release), `webBuildId`,
and `components` mapping **exactly** `host`, `web`, `templates`, `termBridge` to
lowercase SHA-256 strings. Each component hash is the SHA-256 of compact UTF-8 JSON
`[[relativePosixPath, fileSha256], ...]`, sorted lexicographically by path, with
`ensure_ascii=True`. Empty directories do not affect the digest; links and special
files are rejected. Required apphost/bridge execute bits are validated separately.
A webBuildId is required and must change when the web digest changes. The CLI
verifies bytes, not just manifest declarations, before deciding what changed.
Manifests provide integrity; authenticity comes from the trusted source policy.

## Dual systemd user services

The installer writes `<name>-term.service` and `<name>.service` under an explicitly
selectable `--unit-dir` (normally `~/.config/systemd/user`). It runs daemon-reload,
enables both, starts the bridge first, then the Host. `--no-start` stages and enables
units for offline provisioning. `install.sh` simply invokes `perga-ctl install`.
Unit roots must use ordinary absolute path characters (no spaces or systemd
specifiers); unsafe paths fail before writing files.

Bridge unit essentials:

```ini
[Service]
Type=simple
ExecStart=<root>/current/termBridge/perga-term-bridge --listen-uds %t/<name>/term.sock
RuntimeDirectory=<name>
RuntimeDirectoryMode=0700
UMask=0077
Restart=on-failure
```

Host unit essentials:

```ini
[Unit]
Wants=<name>-term.service
After=<name>-term.service
[Service]
Type=simple
WorkingDirectory=<root>
ExecStart=/usr/bin/python3 <root>/bin/perga_host.py --name <name> --term-socket %t/<name>/term.sock --require-toml
UMask=0077
Restart=on-failure
```

Both install into `default.target`. In the default mode, the Host uses the
separate bridge. Host restart has no `PartOf` or `BindsTo` relationship that
kills that bridge. A running bridge keeps the old executable
mapping across `current` swaps, preserving terminal sessions. A changed bridge
requires `--restart-term-bridge`, explicitly accepting terminal loss; `--force`
does not grant that permission. Upgrade stops Host before changing bridge.
Set `instance.overlayfs = true` to enable the [OverlayFS launcher](../track-overlayfs.md#managed-installations).
In that mode, the Host-managed bridge shares the workspace mount namespace
and its terminals end when the Host stops.
Use `loginctl enable-linger` separately if services must survive logout; the
installer never changes host-wide linger policy.

## Instance settings

Managed TOML configuration requires Python 3.11 or later, including
`/usr/bin/python3` used by the user service. The installer writes the required
`instance.name` and `instance.host_url`. Other sections and keys are optional.
The DB, web and template paths are derived from the installation root.

```toml
[instance]
name = "perga-4240"
host_url = "http://127.0.0.1:4240"
overlayfs = true
# workspaces_dir = "/absolute/path/to/workspaces"
# release_url_prefix = "https://releases.example.com/perga/"

[providers.agy]
executable = "/absolute/path/to/agy"

[providers.codex]
executable = "/absolute/path/to/codex"
# worker_home = "/absolute/path/to/private/codex-home"

[providers.opencode]
executable = "/absolute/path/to/opencode"

[providers.claude]
executable = "/absolute/path/to/claude"

[proxy]
url = "http://127.0.0.1:2080"
# no_proxy = ["internal.example.com"]
```

Unset executables use existing CLI discovery. Explicit paths apply to provider
probes and sessions and require a Host release with provider configuration support.
Use the `agy` or `antigravity` executable name for Antigravity's permission modes.
The proxy URL applies to HTTP and HTTPS for the Host and its provider processes.
Loopback addresses always bypass the configured proxy. With no proxy section,
the Host inherits the system service's proxy environment. The controller uses a
direct connection to the local Host for management traffic. See
[shared Codex Worker home](codex-installation-login.md) for directory and login
requirements. Restart the Host after changing instance settings.

### Move an existing managed installation to TOML

Start with the same name and running Host URL. Create `config/perga.toml` with
mode 0600 and move the applicable OverlayFS flag, proxy, workspace path, release
URL prefix, provider paths and Worker home into it. From the updated Perga
checkout, refresh the stable tools and service files using the existing root,
name and unit directory:

```sh
tools/perga-deploy/perga-ctl --root /path/to/installation --name perga-4240 configure-services
```

`configure-services` validates the current release and settings, updates the
stable tool and unit files, and reloads systemd units without restarting the Host
or bridge. Remove settings moved to TOML and any old `ExecStart` override from
the installation's service drop-ins, reload systemd, and restart the Host in
the maintenance window. Keep
the old `config.env` for rollback. To return to it, move the TOML file out of the
config directory, rerun `configure-services`, restore the original drop-in
settings, reload systemd and restart the Host. Legacy units with only
`config.env` continue to use the existing environment configuration.

## Controller operations and source policy

Run `perga-ctl --help` and `<command> --help` for exact flags. `--root`, `--name` and
`--timeout` may precede or follow the command. A release tarball contains manifest and component
directories at its root (no enclosing directory). Example offline provisioning:

```sh
tools/perga-deploy/install.sh --root /tmp/perga-demo --name perga-demo --from-tarball /tmp/perga-release.tar.gz --unit-dir /tmp/perga-demo-units --no-start
tools/perga-deploy/perga-ctl --root /tmp/perga-demo --name perga-demo status
```

* `install`: verify/copy a release; create layout, controller, config and units.
* `adopt`: requires both named units stopped. Inspect SQLite through a temporary copy
  (including WAL/SHM) to avoid creating sidecars in data. Snapshot existing flat payload into
  an `adopted-<id>` release without moving or rewriting `data/`. Supports
  `host/` or flat apphost files, `web/` or `wwwroot/`, `templates/`, and either
  `termBridge/` or a root `perga-term-bridge`. Legacy payload remains for operator
  cleanup after verification. Existing units are backed up before replacement.
* `status`: links plus state/receipt, no HTTP required. `check`: verify a candidate
  and print component changes/restart requirements; without source, validate current.
* `upgrade`: reserves a queued job under flock, then starts `apply` through
  `systemd-run --user` in an independent transient service cgroup, with a stable
  log and job ID. A matching job ID admits the queued runner; it reacquires flock.
  Other jobs cannot overtake the reservation. Startup failures get a failed receipt;
  an interrupted launcher leaves explicit recovery intent. A plain new process
  session would still be killed with the Host cgroup, so it is insufficient.
  `--wait` runs synchronously for automation. `apply` is the same runner
  entry point and can also be invoked directly. `--notify-track-id` is opaque receipt
  routing data, never a shell argument to another program.
* `rollback`: launches in an independent transient user unit and returns `{jobId, unit, log}`
  like `upgrade`; `--wait` runs synchronously. The queued reservation preserves the
  prior recovery state, which is restored if launch fails. If the unit dies before
  consuming its reservation, `rollback --wait` explicitly recovers it under flock;
  stale workers with a mismatched job ID cannot consume a newer reservation.
  The runner drains the current Host, returns to the previous release, restores
  the schema backup if necessary, and verifies health. Also recovers an interrupted state using its saved old
  release/schema. `doctor`: validate manifests, links, config, SQLite integrity,
  service status, API health and unfinished intent; report repair needs without attempting repairs.

Sources are mutually exclusive: `--from-tarball` is an explicitly trusted local
artifact; `--from-repo` builds a trusted local source tree in a temporary copy
(`dotnet publish`, `npm ci && npm run build`, `cargo build --release --locked`).
Build tools are prerequisites, not Python dependencies. The generated manifest
uses SQL migration filenames for schemaVersion. `--from-release` is an HTTPS
archive URL under the exact configured `instance.release_url_prefix`, with redirects
rejected. Provision that allowlist out of band; no default arbitrary download
origin. No remote shell, arbitrary systemctl command, or user-provided build
command is accepted. Local builds execute trusted build scripts and must not be
exposed as arbitrary worker arguments.

Tar extraction rejects absolute/traversing paths, links, duplicate paths and
special devices; extraction has count/size limits. The controller uses argument
arrays, loopback HTTP with proxy bypass and no redirects, bounded requests and
polling, nonblocking `flock`, and a minimal subprocess environment. `--force`
allows proceeding after an explicit Drain timeout; it never bypasses failed Drain
requests, checksum validation, source allowlists or bridge permissions. MCP must
not expose it by default. Units and the mutex are scoped to the configured instance.

## Upgrade transaction and recovery

Acquire the mutex before staging or changing state; contention fails immediately.
Refuse a new upgrade over unfinished intent. Verify installed and candidate bytes,
reject downgrading schema support, save old release/schema and job ID, then:

1. If only web/templates differ (and schemaVersion is unchanged), atomically change
   current/previous and POST Reload. Verify both `/healthz` and system info with
   the new releaseId and webBuildId. No Drain or systemctl call is made.
2. Otherwise POST Drain, poll until `draining: true, activeDispatches: 0` with a
   deadline, and stop Host. Read actual `schema_migrations` maximum, not SQLite's
   unrelated `PRAGMA user_version`. Create a fresh online backup with sqlite3's
   backup API before starting a release that can migrate the DB.
3. Save intent before swapping. Swap current/previous. Restart bridge only when
   changed and permitted, start Host, and poll health plus exact info identity.
4. Atomically write terminal receipt, then terminal state. Receipt includes jobId,
   old/new release IDs, status, error/rollback error, notifyTrackId, timestamp and
   changed components. A failed operation exits nonzero even if rollback succeeds.

After activation failure stop the candidate Host **before** examining/restoring
SQLite. If actual schema exceeds old supported version (or the database is corrupt), require a valid
`perga.db.bak-v{oldActualVersion}`, preserve failed DB plus WAL/SHM under one
unique `.failed-<timestamp>` family, and restore via a staged copy + atomic rename. Persist a restore intent marker
before touching DB files so an interrupted restore is repeated safely.
No backup means fail closed with Host stopped; never boot old code on a newer DB.
Old binary starts only after link and DB recovery. A reload failure swaps back and
reloads the old assets. A changed bridge is restarted with old bytes on rollback.
Backups stay available. Manual rollback follows the same health and DB checks;
intent recovery uses persisted IDs, even if current was already swapped.

State writes are atomic/fsynced. A killed runner leaves unfinished intent; `doctor`
reports it and subsequent upgrades refuse until explicit `rollback`. No silent
success or concurrent retry. Failures during rollback remain `recovery-required`
and retain both primary and rollback errors. Controller receipts are a local
source of truth; the Phase 2 Kernel consumer provides durable notification deduplication.
Rollback reverses schema only through a pre-upgrade snapshot and can lose data
written by the new release: Drain and early health verification bound that window.

## Host, Kernel and Web contracts

`GET /api/v1/system/info` publishes releaseId, webBuildId, schemaVersion, draining,
activeDispatches and runtime bridge identity. During assets-only reload releaseId
reflects the activated manifest; separately report hostBuildId/bridgeBuildId so
resident executable identity remains explicit. `/healthz` proves liveness only.
New code must verify identity and readiness, not accept any HTTP 200 as success.

`POST /api/v1/system/drain` accepts `{draining: true}` to close Kernel dispatch
admission, pause new scheduler/provider launches, and allows existing dispatches to settle. It returns
202 promptly. Info counts actual in-flight provider launches/turns and does not
count idle terminals or the initiating Lead's completed MCP call. Already accepted
work stays durably queued. The initiating MCP tool schedules the detached runner
in an independent transient user service (including the rollback runner) and
returns before Drain waits: never wait for the Today track or the initiating
turn to finish while it synchronously awaits its own upgrade result. Drain
timeout before Host shutdown is undone with `{draining: false}` on the same
endpoint, reopening dispatch without interrupting busy work. Once Host was stopped,
restarting the unchanged Host reopens dispatch. Timeouts leave an error unless explicitly forced by the operator.

`POST /api/v1/system/reload` takes `{releaseId, webBuildId}`. Host resolves current
once, checks that it resolves directly inside `releases/`, validates manifest
structure, identity, schema version and component hash fields, and requires real
`web`/`templates` directories without root symlinks. It swaps static-file and
template snapshots atomically,
invalidates caches, and returns success only when info reports that identity.
Keep at most three previous web asset providers; dispose evicted providers. HTML and
system/info use no-cache. Template reload affects new tracks only; existing track
snapshots remain stable. No DB mutation or service restart occurs on Reload. `perga-ctl` owns full byte
integrity verification before activation; Host boot/reload never rehashes release
trees or reads Host/bridge binaries. Standard .NET file attributes reject manifest,
receipt and token symlinks. This is not a portable special-file type detector;
installation and runtime files remain owner-controlled.

The installed controller copies every adjacent `.py` module: `perga_ctl.py` owns
workflow orchestration and CLI parsing, `perga_manifest.py` owns release staging
and integrity, `perga_sqlite.py` owns database recovery, `perga_systemd.py` owns
service setup and detached launch, and `perga_platform.py` owns process/HTTP and
atomic file primitives. Both upgrade and rollback use the same detached launcher;
the Host only invokes perga-ctl and reads its returned job ID.

Kernel publishes Lead-only `perga_system_upgrade_check`,
`perga_system_upgrade_apply`, `perga_system_upgrade_rollback`. Determine Lead from
persisted track/session identity at call time, never caller-supplied role flags;
workers, including unbound subagents, are denied with a stable error code.
Apply/rollback accept allowlisted source IDs/options and notify track identity,
never arbitrary paths, commands, service names, URLs, environment, or `--force`.
The Host adapter handles HTTP/MCP transport; Kernel owns authorization, admission,
and receipt routing; SQLite implements its durable ports. Management writes need
same-user authorization/CSRF protection, including when proxied off loopback. Phase 2 requires a bearer token from
`runtime/system-management-token` (mode 0600); perga-ctl reads this file on every
request. Origin-bearing requests must be same-origin, and remote/proxied writes
require HTTPS. `Perga:SystemManagementTokenSha256` optionally adds an owner-provisioned
verifier. `Perga:InstallationRoot` (or `PERGA_INSTALL_ROOT`) selects the installation;
it is inferred from the installed `PERGA_STATIC_WEB_DIR=<root>/current/web` when absent.
`Perga:UpgradeSources:<id>:{Kind,Location}` maps MCP source IDs to owner-provisioned
`Tarball`, `Repo`, or `Release` sources. MCP exposes no bridge-restart override.
`Perga:BridgeBuildId` / `PERGA_TERM_BRIDGE_BUILD_ID` reports externally provisioned
resident bridge identity; it is `unknown` when the bridge does not report an identity,
rather than assuming that its resident executable follows `current`.

On post-upgrade boot, BootReconciler imports the terminal receipt through a
validated port after interrupted-attempt reconciliation, enqueues a system wake
for notifyTrackId's current Lead, and records jobId consumption transactionally.
Do not await that Lead's provider run during startup. A failed/closed target is
recorded and retried or surfaced administratively without blocking other tracks.
Receipt creation occurs after health verification, which may be after boot:
startup import plus a background file watcher/poller must handle late receipts
and reload-only upgrades too. Persisted deduplication makes replay/restart safe.
No dependence on Today readiness, foreground UI, or an open MCP connection.

Web shows a persistent upgrade/draining banner and completion/failure receipt.
Poll system/info (and refresh after reconnect); when webBuildId changes, offer a
single refresh, retain unsent draft text, and avoid reload loops. A changed
releaseId with the same webBuildId requires no browser refresh. Include browser
flow tests and update published API schemas when implementing these later phases.

## Verification

`python3 -m unittest discover -s tools/perga-deploy/tests -v` uses temporary roots,
SQLite DBs, HTTP fakes, an actual local HTTP server and injected systemctl, plus a real detached Python child behind a fake transient-service launcher. It
checks install/adopt, zero-restart reload, Drain ordering, bridge policy, automatic
rollback with backup restore, failure/retry state and process-level flock exclusion.
`sh -n tools/perga-deploy/install.sh` and `git diff --check` finish Phase 1 checks.
No tests access the live installation or user service directory. Real systemd,
provider dispatch, and browser behavior require dedicated environment validation. Phase 2 adds controlled
Host endpoint, Kernel dispatch and receipt-restart integration tests.
