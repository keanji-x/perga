ALTER TABLE mails ADD COLUMN reply_to_mail_id TEXT;
ALTER TABLE mails ADD COLUMN wake_pending INTEGER NOT NULL DEFAULT 0 CHECK (wake_pending IN (0, 1));
CREATE INDEX idx_mails_pending_wake ON mails(created_at_ms, id) WHERE wake_pending = 1;
