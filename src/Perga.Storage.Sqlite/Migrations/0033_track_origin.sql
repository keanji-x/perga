ALTER TABLE tracks ADD COLUMN origin_track_id TEXT REFERENCES tracks(id) ON DELETE SET NULL;
CREATE INDEX IF NOT EXISTS idx_tracks_origin ON tracks(origin_track_id) WHERE origin_track_id IS NOT NULL;
