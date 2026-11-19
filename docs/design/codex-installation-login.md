# Shared Codex Worker home

Set `Perga__CodexWorkerHomeDirectory` to the absolute path of an existing
directory with mode `0700` on a Linux Host. The owner can sign
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
