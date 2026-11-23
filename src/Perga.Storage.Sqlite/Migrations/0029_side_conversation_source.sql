ALTER TABLE agent_sessions ADD COLUMN source_session_id TEXT REFERENCES agent_sessions(id) ON DELETE SET NULL;
