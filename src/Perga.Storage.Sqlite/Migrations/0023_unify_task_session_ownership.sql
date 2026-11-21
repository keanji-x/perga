PRAGMA foreign_keys = OFF;

ALTER TABLE tasks ADD COLUMN session_id TEXT REFERENCES agent_sessions(id) ON DELETE SET NULL;
ALTER TABLE tasks ADD COLUMN run_count INTEGER NOT NULL DEFAULT 0 CHECK (run_count >= 0);
ALTER TABLE tasks ADD COLUMN worker_result_json TEXT;
ALTER TABLE tasks ADD COLUMN meta_json TEXT NOT NULL DEFAULT '{}';
ALTER TABLE tasks ADD COLUMN gate_verdict_json TEXT;
ALTER TABLE tasks ADD COLUMN started_at_ms INTEGER;
ALTER TABLE tasks ADD COLUMN finished_at_ms INTEGER;

UPDATE tasks
SET session_id = COALESCE(
        (SELECT s.id FROM agent_sessions s WHERE s.bound_task_id = tasks.id ORDER BY s.created_at_ms DESC, s.id DESC LIMIT 1),
        (SELECT a.session_id FROM task_attempts a JOIN agent_sessions s ON s.id = a.session_id WHERE a.task_id = tasks.id ORDER BY (a.attempt_id = tasks.latest_attempt_id) DESC, a.started_at_ms DESC, a.attempt_id DESC LIMIT 1)
    ),
    run_count = (SELECT COUNT(*) FROM task_attempts a WHERE a.task_id = tasks.id),
    worker_result_json = (
        SELECT a.worker_result_json FROM task_attempts a
        WHERE a.task_id = tasks.id
        ORDER BY (a.attempt_id = tasks.latest_attempt_id) DESC, a.started_at_ms DESC, a.attempt_id DESC
        LIMIT 1
    ),
    meta_json = COALESCE((
        SELECT a.meta_json FROM task_attempts a
        WHERE a.task_id = tasks.id
        ORDER BY (a.attempt_id = tasks.latest_attempt_id) DESC, a.started_at_ms DESC, a.attempt_id DESC
        LIMIT 1
    ), '{}'),
    gate_verdict_json = (
        SELECT a.gate_verdict_json FROM task_attempts a
        WHERE a.task_id = tasks.id
        ORDER BY (a.attempt_id = tasks.latest_attempt_id) DESC, a.started_at_ms DESC, a.attempt_id DESC
        LIMIT 1
    ),
    started_at_ms = (
        SELECT a.started_at_ms FROM task_attempts a
        WHERE a.task_id = tasks.id
        ORDER BY (a.attempt_id = tasks.latest_attempt_id) DESC, a.started_at_ms DESC, a.attempt_id DESC
        LIMIT 1
    ),
    finished_at_ms = (
        SELECT a.finished_at_ms FROM task_attempts a
        WHERE a.task_id = tasks.id
        ORDER BY (a.attempt_id = tasks.latest_attempt_id) DESC, a.started_at_ms DESC, a.attempt_id DESC
        LIMIT 1
    );

ALTER TABLE tasks DROP COLUMN latest_attempt_id;
CREATE INDEX idx_tasks_session ON tasks(session_id) WHERE session_id IS NOT NULL;
CREATE INDEX idx_tasks_gate_blob_ref ON tasks (json_extract(gate_verdict_json, '$.blobRef'))
    WHERE json_type(gate_verdict_json, '$.blobRef') = 'text';

ALTER TABLE terminals ADD COLUMN session_id TEXT REFERENCES agent_sessions(id) ON DELETE SET NULL;
UPDATE terminals
SET session_id = (
    SELECT a.session_id FROM task_attempts a
    JOIN agent_sessions s ON s.id = a.session_id
    WHERE a.attempt_id = terminals.attempt_id
)
WHERE attempt_id IS NOT NULL;
ALTER TABLE terminals DROP COLUMN attempt_id;

ALTER TABLE agent_sessions DROP COLUMN bound_task_id;
ALTER TABLE agent_sessions DROP COLUMN bound_attempt_id;

CREATE TABLE session_messages_v22 (
    ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
    id TEXT NOT NULL,
    session_id TEXT NOT NULL REFERENCES agent_sessions(id) ON DELETE CASCADE,
    kind TEXT NOT NULL CHECK (kind IN ('input', 'queue', 'steer', 'worker')),
    author_id TEXT NOT NULL,
    request_key TEXT NOT NULL,
    request_hash TEXT NOT NULL,
    content TEXT NOT NULL,
    target_turn_id TEXT,
    turn_id TEXT,
    delivered_turn_id TEXT,
    delivery TEXT NOT NULL CHECK (delivery IN ('queued', 'dispatching', 'delivered', 'rejected', 'unknown', 'interrupted', 'withdrawn')),
    created_at_ms INTEGER NOT NULL,
    resolved_at_ms INTEGER,
    UNIQUE (session_id, id),
    UNIQUE (session_id, author_id, request_key)
);

-- Keep the first original value. For duplicates, probe suffixes against ALL
-- original values. Generated suffixes end with a unique ordinal, so generated
-- values cannot collide with each other even when their original bases differ.
WITH RECURSIVE
id_probe(ordinal, session_id, candidate) AS (
    SELECT m.ordinal, m.session_id, m.id || '_' || m.ordinal
    FROM session_messages m WHERE EXISTS (
        SELECT 1 FROM session_messages o
        WHERE o.session_id=m.session_id AND o.id=m.id AND o.ordinal<m.ordinal
    )
    UNION ALL
    SELECT p.ordinal, p.session_id, p.candidate || '_' || p.ordinal
    FROM id_probe p WHERE EXISTS (
        SELECT 1 FROM session_messages o WHERE o.session_id=p.session_id AND o.id=p.candidate
    )
),
key_probe(ordinal, session_id, author_id, candidate) AS (
    SELECT m.ordinal, m.session_id, m.author_id, m.request_key || '#' || m.ordinal
    FROM session_messages m WHERE EXISTS (
        SELECT 1 FROM session_messages o WHERE o.session_id=m.session_id AND o.author_id=m.author_id
            AND o.request_key=m.request_key AND o.ordinal<m.ordinal
    )
    UNION ALL
    SELECT p.ordinal, p.session_id, p.author_id, p.candidate || '#' || p.ordinal
    FROM key_probe p WHERE EXISTS (
        SELECT 1 FROM session_messages o WHERE o.session_id=p.session_id AND o.author_id=p.author_id
            AND o.request_key=p.candidate
    )
)
INSERT INTO session_messages_v22 (
    ordinal, id, session_id, kind, author_id, request_key, request_hash, content,
    target_turn_id, turn_id, delivered_turn_id, delivery, created_at_ms, resolved_at_ms
)
SELECT ordinal,
       COALESCE((SELECT p.candidate FROM id_probe p WHERE p.ordinal=m.ordinal
           AND NOT EXISTS (SELECT 1 FROM session_messages o WHERE o.session_id=p.session_id AND o.id=p.candidate)), m.id),
       session_id,
       kind,
       author_id,
       COALESCE((SELECT p.candidate FROM key_probe p WHERE p.ordinal=m.ordinal
           AND NOT EXISTS (SELECT 1 FROM session_messages o WHERE o.session_id=p.session_id
               AND o.author_id=p.author_id AND o.request_key=p.candidate)), m.request_key),
       request_hash,
       content,
       target_turn_id,
       turn_id,
       delivered_turn_id,
       delivery,
       created_at_ms,
       resolved_at_ms
FROM session_messages m
ORDER BY ordinal ASC;

INSERT INTO sqlite_sequence(name, seq)
SELECT 'session_messages_v22', seq FROM sqlite_sequence
WHERE name = 'session_messages'
  AND NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'session_messages_v22');
UPDATE sqlite_sequence
SET seq = MAX(seq, COALESCE((SELECT seq FROM sqlite_sequence WHERE name = 'session_messages'), 0))
WHERE name = 'session_messages_v22';

DROP TRIGGER IF EXISTS session_items_message_insert;
DROP TRIGGER IF EXISTS session_items_message_update;
DROP TABLE session_messages;
ALTER TABLE session_messages_v22 RENAME TO session_messages;
CREATE INDEX idx_session_messages_queue ON session_messages(session_id, delivery, ordinal);
CREATE INDEX idx_session_messages_boot ON session_messages(delivery, session_id);
CREATE INDEX idx_session_messages_input_turn ON session_messages(turn_id) WHERE turn_id IS NOT NULL;

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

DROP TABLE IF EXISTS worker_messages;
DROP TABLE task_attempts;
