CREATE TABLE memory_tracks (
    scope_key TEXT PRIMARY KEY,
    area_id TEXT NULL REFERENCES areas(id) ON DELETE CASCADE,
    track_id TEXT NOT NULL UNIQUE REFERENCES tracks(id) ON DELETE CASCADE,
    created_at_ms INTEGER NOT NULL
);
CREATE UNIQUE INDEX idx_memory_tracks_area ON memory_tracks(area_id) WHERE area_id IS NOT NULL;
