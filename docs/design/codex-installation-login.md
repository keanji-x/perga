# Shared Codex Worker home

For a managed Linux Host, set `worker_home` in the `[providers.codex]` table
of `config/perga.toml`. The setting points to an existing directory with
an absolute path and mode `0700`. For a standalone Host, use the
`Perga:CodexWorkerHomeDirectory` application configuration key. The owner can sign
into that directory using Codex device login with file credential storage.
Keep it outside every Track workspace and every path Perga makes available
to other sandboxes. In particular, do not place it under the default
`~/.codex`, `~/.config`, or `~/.local` mounts. Use a separate directory for
each installation.

Perga passes the configured directory as `CODEX_HOME` and bind-mounts it
writable for those Workers. The directory holds the login as well as Codex
configuration and session state. Workers opted into the same directory have
mutual read and write access to that state. Give it only to Workers within
the same trust boundary. Leave ordinary unsandboxed sessions and provider
probes on their existing runtime state path. Workspace mounts retain their
per-task access modes. The setting is incompatible with a separate sandbox
API key.

Unset the option and restart the Host to restore per-session Worker homes.
Keep the shared directory if its login or native transcripts are still
needed. This change requires no Perga database migration.
