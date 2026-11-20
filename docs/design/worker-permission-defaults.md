# Configured Worker permission defaults

An installation can choose the launch permission mode for new Worker attempts
by provider. Store the choice on the new session along with its provider and
model. Session recovery continues to use the mode already stored for that
session. A provider with no configured entry uses its current default.

```toml
[providers.codex]
worker_permission_mode = "ask"
```

Codex declares `never`, `ask`, and `full`. Use the exact ID. Codex's `full`
still sets its approval policy to `never`. It widens Codex's own file access
within the task sandbox.

The managed TOML loader validates the section, provider name, and scalar value.
The Host uses AgentKit's provider permission profile to validate the exact mode
ID during startup. A mode available only through a live workspace catalog
cannot be validated as an installation default. Allow static modes and declared
launch modes. Reject bad values at startup and include the configuration key
and the valid IDs in the error.

Managed installations need the Host and launcher versions that support
`worker_permission_mode`. Refresh the stable launcher with `configure-services`
when upgrading it, then restart the Host to load the setting.
If rolling back the Host to a release predating this setting, new Workers use
that release's default. Remove the key before restoring an older TOML launcher.

The launch setting is separate from the decision policy for provider approval
requests received by Perga. Task sandbox selection still follows task access.
Verify TOML projection, startup rejection of invalid modes, and selection on
new Worker sessions through the Host dispatch path.
