# Track OverlayFS

An opted-in Linux Host can offer OverlayFS for a selected Git repository or
ordinary directory. Each Track has one writable workspace. Agents, terminals,
command tasks, and gates use the same merged directory. Attempts share it, so
discarding an Attempt leaves its edits available to the Track.

VcsKit prepares and publishes immutable snapshots under the configured
workspace root. Git snapshots contain a standalone checkout of a resolved
commit. Directory snapshots preserve the source tree and are identified by its
contents and metadata. An optional build command can warm the snapshot before
publication. Each Track persists its snapshot path and its own upper, work, and
merged paths in the existing workspace specification. Git initialization runs
in a disposable private mount, and the initialized workspace directory is
published atomically. Interrupted setup can start again without inheriting
an unfinished Git operation. Refreshing the source produces a new snapshot. Published snapshots remain available for reuse and
restart.

A Linux launcher creates private user, mount, and PID namespaces. Its mount process
keeps namespace-scoped mount privileges. The Host runs with the original UID
and GID and drops the launcher capabilities. Inherited pipes carry mount
requests. The launcher derives paths from validated workspace and snapshot IDs. Host-managed terminals inherit the mount view. Availability requires an
actual probe in the configured workspace root and a compatible terminal setup.

Mounting is repeatable. Recovery mounts the saved snapshot and upper again
before launching work. Deletion stops workspace consumers and unmounts before committing the Track
delete. Private files are reclaimed after the commit. A failed unmount or
database write preserves the private files for retry. A crash after the
commit can leave unmounted files for operator cleanup. Snapshot publication uses a staging directory and atomic rename.
Verification should cover two simultaneous Tracks, a real mount and remount,
Git branch independence, separate build outputs, and safe deletion.

## Run on Linux

Use Linux 5.11 or later with OverlayFS and unprivileged user namespaces
available. The launcher also uses a private PID namespace to stop leftover
workspace consumers on shutdown. It uses Python 3.9 or later, util-linux `unshare` and `setpriv`,
and GNU `cp` and `tar`. Keep the Host-managed terminal bridge enabled so it
inherits the same mount namespace. Choose a local workspace filesystem where
OverlayFS can mount a writable upper. Keep the workspace root outside Git
and Jujutsu repositories. Perga probes this before offering the
workspace option.

Build the Host, then run its DLL directly through the launcher as the same
user who owns the workspaces:

```sh
dotnet build src/Perga.Host
python3 tools/perga-overlayfs/launch.py \
  --data-dir /path/to/perga-data \
  --workspaces /path/to/perga-workspaces \
  -- dotnet src/Perga.Host/bin/Debug/net11.0/Perga.Host.dll
```

Select **OverlayFS workspace** in the Track creation form. A Git baseline
uses the selected commit or the source checkout's `HEAD` and preserves its
configured fetch and push destinations for `origin`. Each Track starts a separate branch. To incorporate new
upstream changes in an existing Track, run the corresponding Git command from
that Track. To use an updated baseline for a new Track, update the source
checkout or select a different base revision. For an ordinary directory,
Perga checks the source contents when creating a Track and reuses the
matching snapshot, including file attributes and access-control lists. An
ordinary source must be free of nested Git or Jujutsu metadata and attributes
reserved by OverlayFS. Choose a Jujutsu workspace for a Jujutsu-only source.
Existing Tracks keep their original snapshot.

For a Rust project, optionally set
`Perga__OverlayWorkspaceWarmCommand='cargo build --locked'` before starting
the launcher. The command runs once per new baseline before it is published.
Run it from the repository root or use `--manifest-path` for the desired
workspace. Use a build command that writes generated outputs such as
`target/` inside its working directory. Set
`Perga__OverlayWorkspaceCacheVersion` to a new value when changing external
toolchain or system dependencies. All Tracks based on that snapshot see the
prewarmed outputs. Each Track's build and clean operations affect its own
view. The Host account's toolchain and download caches remain available by
the usual tool configuration.

The workspace root is exclusive to one launcher at a time. Published
snapshots are retained so Tracks can remount after restart and new Tracks
can reuse them. Deleting a Track removes its private workspace. OverlayFS
isolates writes through the workspace path, while absolute paths and tools
outside this directory follow the process's existing sandbox policy.
