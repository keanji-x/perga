CREATE TABLE auth_users (
    id TEXT PRIMARY KEY NOT NULL,
    username TEXT NOT NULL UNIQUE COLLATE NOCASE,
    display_name TEXT NOT NULL,
    role TEXT NOT NULL DEFAULT 'owner' CHECK (role IN ('owner', 'member')),
    password_hash TEXT NOT NULL,
    credential_version INTEGER NOT NULL DEFAULT 1 CHECK (credential_version >= 1),
    session_generation INTEGER NOT NULL DEFAULT 1 CHECK (session_generation >= 1),
    password_updated_at_ms INTEGER NOT NULL,
    disabled_at_ms INTEGER,
    created_at_ms INTEGER NOT NULL,
    updated_at_ms INTEGER NOT NULL
);

CREATE UNIQUE INDEX idx_auth_users_single_owner ON auth_users(role) WHERE role = 'owner';

CREATE TABLE auth_sessions (
    id TEXT PRIMARY KEY NOT NULL,
    user_id TEXT NOT NULL REFERENCES auth_users(id) ON DELETE CASCADE,
    token_hash TEXT NOT NULL UNIQUE CHECK (length(token_hash) = 64 AND token_hash NOT GLOB '*[^0-9a-f]*'),
    created_at_ms INTEGER NOT NULL,
    last_seen_at_ms INTEGER NOT NULL,
    expires_at_ms INTEGER NOT NULL,
    revoked_at_ms INTEGER,
    user_agent TEXT,
    created_ip TEXT
);

CREATE INDEX idx_auth_sessions_active_user ON auth_sessions(user_id) WHERE revoked_at_ms IS NULL;
CREATE INDEX idx_auth_sessions_expires ON auth_sessions(expires_at_ms);
CREATE INDEX idx_auth_sessions_revoked ON auth_sessions(revoked_at_ms) WHERE revoked_at_ms IS NOT NULL;
