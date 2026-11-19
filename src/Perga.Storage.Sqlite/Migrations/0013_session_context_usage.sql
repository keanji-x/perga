ALTER TABLE agent_sessions ADD COLUMN context_used_tokens INTEGER NULL;
ALTER TABLE agent_sessions ADD COLUMN context_window_tokens INTEGER NULL;
ALTER TABLE agent_sessions ADD COLUMN context_usage_at_ms INTEGER NULL;
ALTER TABLE agent_sessions ADD COLUMN context_usage_invalidated INTEGER NOT NULL DEFAULT 0 CHECK (context_usage_invalidated IN (0, 1));
