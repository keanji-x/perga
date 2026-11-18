CREATE TABLE IF NOT EXISTS provider_session_resume (
    session_id      TEXT PRIMARY KEY REFERENCES agent_sessions(id) ON DELETE CASCADE,
    turn_id         TEXT NOT NULL,
    native_id       TEXT NOT NULL,
    quiescent       INTEGER NOT NULL CHECK (quiescent IN (0, 1))
);
