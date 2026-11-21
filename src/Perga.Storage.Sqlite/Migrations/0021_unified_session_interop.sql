ALTER TABLE agent_sessions ADD COLUMN parent_session_id TEXT REFERENCES agent_sessions(id) ON DELETE SET NULL;
ALTER TABLE agent_sessions ADD COLUMN spawn_depth INTEGER NOT NULL DEFAULT 0 CHECK (spawn_depth >= 0);
ALTER TABLE agent_sessions ADD COLUMN config_version INTEGER NOT NULL DEFAULT 0 CHECK (config_version >= 0);
ALTER TABLE agent_sessions ADD COLUMN notify_parent TEXT NOT NULL DEFAULT 'none'
    CHECK (notify_parent IN ('none', 'turn_end', 'task_settled'));
ALTER TABLE agent_sessions ADD COLUMN system_prompt TEXT;

UPDATE agent_sessions
SET config_version = (SELECT version FROM worker_settings w WHERE w.session_id = agent_sessions.id),
    model = (SELECT model FROM worker_settings w WHERE w.session_id = agent_sessions.id),
    reasoning_effort = (SELECT reasoning_effort FROM worker_settings w WHERE w.session_id = agent_sessions.id)
WHERE EXISTS (SELECT 1 FROM worker_settings w WHERE w.session_id = agent_sessions.id);

UPDATE agent_sessions
SET parent_session_id = (
        SELECT r.author_id FROM creation_receipts r
        JOIN agent_sessions p ON p.id = r.author_id AND p.track_id = r.scope_id
        WHERE r.operation = 'worker-dispatch' AND r.scope_id = agent_sessions.track_id
          AND r.resource_id = agent_sessions.bound_task_id LIMIT 1),
    spawn_depth = 1,
    notify_parent = 'task_settled'
WHERE bound_task_id IS NOT NULL;

DROP TABLE worker_settings;
ALTER TABLE asks ADD COLUMN target_session_id TEXT REFERENCES agent_sessions(id) ON DELETE SET NULL;
CREATE INDEX idx_sessions_parent ON agent_sessions(track_id, parent_session_id) WHERE parent_session_id IS NOT NULL;
CREATE INDEX idx_creation_receipts_resource ON creation_receipts(operation, scope_id, resource_id);
CREATE INDEX idx_asks_target ON asks(target_session_id) WHERE target_session_id IS NOT NULL;

UPDATE creation_receipts SET operation = 'session-create' WHERE operation = 'session';
