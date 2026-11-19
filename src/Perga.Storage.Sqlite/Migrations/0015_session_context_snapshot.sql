UPDATE agent_sessions
SET context_used_tokens = NULL, context_window_tokens = NULL, context_usage_at_ms = NULL;

ALTER TABLE agent_sessions DROP COLUMN context_usage_invalidated;
ALTER TABLE agent_sessions DROP COLUMN context_usage_ratio;

UPDATE events
SET payload_json = json_set(json_remove(payload_json,
    '$.contextUsageRatio', '$.contextUsageInvalidated', '$.contextUsedTokens',
    '$.contextWindowTokens', '$.contextUsageAtMs'), '$.contextUsage', NULL)
WHERE kind = 'session.updated' AND CASE WHEN json_valid(payload_json) THEN json_type(payload_json) = 'object' ELSE 0 END;
