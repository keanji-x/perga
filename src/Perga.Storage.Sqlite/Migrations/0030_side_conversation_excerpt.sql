ALTER TABLE session_turns ADD COLUMN source_excerpt TEXT;
CREATE INDEX idx_sessions_source ON agent_sessions(source_session_id) WHERE source_session_id IS NOT NULL;
