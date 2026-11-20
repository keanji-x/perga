CREATE TABLE read_receipt_meta (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    baseline_ms INTEGER NOT NULL CHECK (baseline_ms >= 0)
);
INSERT INTO read_receipt_meta (id, baseline_ms)
VALUES (1, CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER));

CREATE TABLE read_receipts (
    kind TEXT NOT NULL CHECK (kind IN ('track', 'conversation')),
    target_id TEXT NOT NULL,
    read_through_ms INTEGER NOT NULL DEFAULT 0 CHECK (read_through_ms >= 0),
    manual_unread INTEGER NOT NULL DEFAULT 0 CHECK (manual_unread IN (0, 1)),
    updated_at_ms INTEGER NOT NULL,
    PRIMARY KEY (kind, target_id)
);
