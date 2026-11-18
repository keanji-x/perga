CREATE TABLE worker_messages (
    id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL REFERENCES agent_sessions(id) ON DELETE CASCADE,
    attempt_id TEXT NOT NULL REFERENCES task_attempts(attempt_id) ON DELETE CASCADE,
    author_id TEXT NOT NULL,
    request_key TEXT NOT NULL,
    request_hash TEXT NOT NULL,
    target_turn_id TEXT,
    result_turn_id TEXT,
    content TEXT NOT NULL,
    delivery TEXT NOT NULL CHECK (delivery IN ('queued', 'dispatching', 'delivered', 'rejected', 'unknown', 'interrupted')),
    created_at_ms INTEGER NOT NULL,
    UNIQUE (session_id, author_id, request_key)
);

CREATE INDEX idx_worker_messages_queue ON worker_messages(session_id, delivery, created_at_ms, id);

CREATE TABLE worker_settings (
    session_id TEXT PRIMARY KEY REFERENCES agent_sessions(id) ON DELETE CASCADE,
    version INTEGER NOT NULL,
    model TEXT,
    reasoning_effort TEXT,
    approval_mode TEXT NOT NULL CHECK (approval_mode IN ('ask', 'deny')),
    author_id TEXT NOT NULL
);
