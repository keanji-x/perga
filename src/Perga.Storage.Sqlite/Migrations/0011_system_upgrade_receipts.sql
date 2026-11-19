CREATE TABLE system_upgrade_receipts (
    job_id TEXT PRIMARY KEY,
    receipt_json TEXT NOT NULL,
    consumed_at_ms INTEGER NOT NULL,
    notification_status TEXT NOT NULL
);
