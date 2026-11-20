# Provider permission defaults

Configure the default session permission for any provider supported by the
managed installation in its existing provider table:

```toml
[providers.codex]
permission_mode = "ask"
```

The Kernel's existing permission resolver applies the value when creating a
Lead, ordinary session, or Worker attempt, or when switching a session to a new
provider. An explicit selection wins. An omitted setting uses the provider's
default. Reopening a stored session uses its saved selection.

Validate TOML shape and value type in the launcher. At Host startup validate
static permission IDs against the AgentKit profile. Providers with dynamic
catalogs validate the selected ID and its acceptance through the existing
session handshake in the actual workspace, before sending a prompt. A rejected
mode or a conflicting reported current mode fails that execution. Bootstrap
marks the configured mode as the default where the provider advertises that ID
or declares it as a launch mode.

The shared default selects native session permissions. Task workspace access
still determines the outer sandbox. Perga's handling of approval requests is
unchanged. Validate defaults for all three session creation paths, explicit
overrides, invalid static configuration, dynamic launch selection, and the
TOML-to-Host projection.

Update both the Host and the stable launcher to use this setting. Refresh
an existing installation's launcher with `configure-services` and restart
the Host. An older Host uses its original defaults for new sessions and
continues to read stored selections. Remove the new key before restoring a
launcher that predates it.
