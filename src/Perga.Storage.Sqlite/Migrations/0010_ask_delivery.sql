-- Preserve previously published wake semantics; historical ownership is unknown.
ALTER TABLE asks ADD COLUMN delivery TEXT NOT NULL DEFAULT 'wake' CHECK (delivery IN ('wake', 'hold'));
ALTER TABLE asks ADD COLUMN session_id TEXT;
ALTER TABLE asks ADD COLUMN turn_id TEXT;

-- Replayed v1 ask events previously had the same wake presentation as old ask rows.
-- Keep that published meaning without inventing historical approval ownership.
UPDATE events
SET payload_json = json_set(payload_json, '$.delivery', 'wake')
WHERE kind = 'ask.changed' AND json_type(payload_json) = 'object'
  AND json_type(payload_json, '$.delivery') IS NULL;
