CREATE TABLE track_wakes (
    ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
    id TEXT NOT NULL UNIQUE,
    track_id TEXT NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
    target_session_id TEXT REFERENCES agent_sessions(id) ON DELETE CASCADE,
    kind TEXT NOT NULL CHECK (kind IN ('mail','schedule','task_settled','ask_answered','report_edited','boot')),
    coalesce_key TEXT,
    payload_json TEXT NOT NULL DEFAULT '{}',
    state TEXT NOT NULL CHECK (state IN ('pending','delivering','done','dropped')),
    reason TEXT,
    turn_id TEXT,
    attempts INTEGER NOT NULL DEFAULT 0,
    created_at_ms INTEGER NOT NULL,
    updated_at_ms INTEGER NOT NULL
);
CREATE UNIQUE INDEX idx_track_wakes_coalesce_pending ON track_wakes(track_id, coalesce_key) WHERE state='pending' AND coalesce_key IS NOT NULL;
CREATE INDEX idx_track_wakes_active ON track_wakes(track_id, state, ordinal) WHERE state IN ('pending','delivering');
CREATE INDEX idx_track_wakes_gc ON track_wakes(state, updated_at_ms, ordinal) WHERE state IN ('done','dropped');
-- Transfer supported-release mail wake intent into the Track-owned ledger.
INSERT INTO track_wakes (id, track_id, target_session_id, kind, coalesce_key, payload_json,
                         state, reason, turn_id, attempts, created_at_ms, updated_at_ms)
SELECT 'wk_mail_' || m.id, m.to_track_id, NULL, 'mail', 'mail:' || m.id,
       json_object('prompt', 'New cross-track mail ''' || m.id || ''' from track ''' || m.from_track_id || ''': ' || m.summary || char(10) || char(10) || m.body,
                   'presentation', 'system_mail'),
       'pending', NULL, NULL, 0, m.created_at_ms, m.created_at_ms
FROM mails m JOIN tracks t ON t.id = m.to_track_id
WHERE m.wake_pending = 1 AND t.closed_at_ms IS NULL
ORDER BY m.created_at_ms ASC, m.id ASC;
UPDATE mails SET wake_pending = 0 WHERE wake_pending = 1;

DROP INDEX idx_session_messages_input_turn;
CREATE INDEX idx_session_messages_input_turn ON session_messages(turn_id) WHERE source='session' AND turn_id IS NOT NULL;
