-- Forward migration: add per-session permission_mode on agent_sessions
ALTER TABLE agent_sessions ADD COLUMN permission_mode TEXT;
