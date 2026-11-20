ALTER TABLE agent_sessions ADD COLUMN unknown_reason TEXT;
ALTER TABLE agent_sessions ADD COLUMN unknown_turn_id TEXT;
ALTER TABLE agent_sessions ADD COLUMN unknown_last_error TEXT;
ALTER TABLE agent_sessions ADD COLUMN reconnect_invalidated INTEGER NOT NULL DEFAULT 0 CHECK (reconnect_invalidated IN (0, 1));

UPDATE agent_sessions AS s
SET status = 'unknown', unknown_reason = 'stream_lost',
    unknown_turn_id = r.turn_id, native_thread_id = r.native_id,
    reconnect_invalidated = CASE WHEN length(trim(r.native_id)) = 0 THEN 1 ELSE 0 END
FROM provider_session_resume r WHERE r.session_id = s.id;
UPDATE agent_sessions SET unknown_reason = 'stream_lost',
    unknown_turn_id = (SELECT id FROM session_turns WHERE session_id = agent_sessions.id ORDER BY turn_index DESC LIMIT 1)
WHERE status = 'unknown' AND unknown_reason IS NULL;
DROP TABLE provider_session_resume;

-- Epoch boundaries are session items, independent of a model turn.
CREATE TABLE session_items_v19 (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL REFERENCES agent_sessions(id) ON DELETE CASCADE,
    turn_id TEXT,
    kind TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    created_at_ms INTEGER NOT NULL,
    accepted_input_id TEXT UNIQUE,
    message_ordinal INTEGER REFERENCES session_messages(ordinal) ON DELETE SET NULL,
    FOREIGN KEY (session_id, turn_id) REFERENCES session_turns(session_id, id) ON DELETE CASCADE,
    FOREIGN KEY (accepted_input_id, session_id, turn_id) REFERENCES session_inputs(id, session_id, turn_id),
    CHECK (accepted_input_id IS NULL OR kind = 'user'),
    CHECK ((kind = 'epoch_boundary' AND turn_id IS NULL) OR (kind != 'epoch_boundary' AND turn_id IS NOT NULL)
        OR (turn_id IS NULL AND kind IN ('user','system')
            AND COALESCE(json_extract(payload_json,'$.status') = 'interrupted', 0)
            AND COALESCE(json_extract(payload_json,'$.archived') = 1, 0)))
);
INSERT INTO session_items_v19 SELECT id, session_id, turn_id, kind, payload_json, created_at_ms, accepted_input_id, message_ordinal FROM session_items;
-- Retain the historical high watermark, including IDs of deleted items.
INSERT INTO sqlite_sequence(name, seq)
SELECT 'session_items_v19', seq FROM sqlite_sequence
WHERE name = 'session_items'
  AND NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'session_items_v19');
UPDATE sqlite_sequence
SET seq = MAX(seq, COALESCE((SELECT seq FROM sqlite_sequence WHERE name = 'session_items'), 0))
WHERE name = 'session_items_v19';
DROP TABLE session_items;
ALTER TABLE session_items_v19 RENAME TO session_items;
CREATE INDEX idx_items_session ON session_items(session_id, id);
CREATE INDEX idx_items_turn ON session_items(turn_id);
CREATE UNIQUE INDEX idx_items_message_ordinal ON session_items(message_ordinal) WHERE message_ordinal IS NOT NULL;
CREATE INDEX idx_session_items_blob_ref ON session_items (json_extract(payload_json, '$.blobRef'))
    WHERE json_type(payload_json, '$.blobRef') = 'text';
CREATE TRIGGER session_items_message_insert BEFORE INSERT ON session_items
WHEN NEW.message_ordinal IS NOT NULL AND (NEW.kind != 'user' OR NOT EXISTS (
    SELECT 1 FROM session_messages m WHERE m.ordinal = NEW.message_ordinal AND m.session_id = NEW.session_id
        AND (m.turn_id = NEW.turn_id OR m.delivered_turn_id = NEW.turn_id
            OR (NEW.turn_id IS NULL AND m.turn_id IS NULL AND m.delivery = 'interrupted'
                AND json_extract(NEW.payload_json,'$.status') = 'interrupted'
                AND json_extract(NEW.payload_json,'$.archived') = 1))
))
BEGIN
    SELECT RAISE(ABORT, 'session item does not match its message');
END;
CREATE TRIGGER session_items_message_update BEFORE UPDATE OF message_ordinal, session_id, turn_id, kind ON session_items
WHEN NEW.message_ordinal IS NOT NULL AND (NEW.kind != 'user' OR NOT EXISTS (
    SELECT 1 FROM session_messages m WHERE m.ordinal = NEW.message_ordinal AND m.session_id = NEW.session_id
        AND (m.turn_id = NEW.turn_id OR m.delivered_turn_id = NEW.turn_id
            OR (NEW.turn_id IS NULL AND m.turn_id IS NULL AND m.delivery = 'interrupted'
                AND json_extract(NEW.payload_json,'$.status') = 'interrupted'
                AND json_extract(NEW.payload_json,'$.archived') = 1))
))
BEGIN
    SELECT RAISE(ABORT, 'session item does not match its message');
END;
