-- Area default execution target, per-track bound execution target,
-- and persisted user-added SSH execution targets.
ALTER TABLE areas ADD COLUMN execution_target_id TEXT NOT NULL DEFAULT 'local';
ALTER TABLE tracks ADD COLUMN execution_target_id TEXT NOT NULL DEFAULT 'local';

CREATE TABLE IF NOT EXISTS execution_targets (
    id TEXT PRIMARY KEY,
    label TEXT NOT NULL,
    kind TEXT NOT NULL DEFAULT 'ssh',
    host TEXT NOT NULL,
    port INTEGER NOT NULL DEFAULT 22,
    ssh_user TEXT NOT NULL,
    identity_file TEXT NULL,
    workspaces_root TEXT NOT NULL,
    created_at_ms INTEGER NOT NULL,
    updated_at_ms INTEGER NOT NULL
);
