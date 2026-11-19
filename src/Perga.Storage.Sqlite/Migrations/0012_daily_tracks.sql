ALTER TABLE areas ADD COLUMN is_system INTEGER NOT NULL DEFAULT 0;
CREATE TABLE daily_tracks (
    date TEXT PRIMARY KEY,
    track_id TEXT NOT NULL UNIQUE REFERENCES tracks(id) ON DELETE CASCADE,
    created_at_ms INTEGER NOT NULL
);
-- Nullable metadata distinguishes unavailable historical facts from recorded values.
ALTER TABLE report_revisions ADD COLUMN event_id INTEGER;
ALTER TABLE report_revisions ADD COLUMN summary TEXT;
ALTER TABLE report_revisions ADD COLUMN leading_comment TEXT;
ALTER TABLE report_revisions ADD COLUMN before_blocks_zstd BLOB;
ALTER TABLE report_revisions ADD COLUMN summary_before TEXT;
ALTER TABLE report_revisions ADD COLUMN leading_comment_before TEXT;
CREATE INDEX idx_report_revisions_event ON report_revisions(event_id);
