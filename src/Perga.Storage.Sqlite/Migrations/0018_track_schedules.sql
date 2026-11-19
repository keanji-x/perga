CREATE TABLE track_schedules (
    id TEXT NOT NULL PRIMARY KEY,
    track_id TEXT NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
    target_session_id TEXT REFERENCES agent_sessions(id) ON DELETE CASCADE,
    source_kind TEXT NOT NULL CHECK (source_kind IN ('agent','user','plugin')),
    schedule_kind TEXT NOT NULL CHECK (schedule_kind IN ('once','cron')),
    title TEXT NOT NULL,
    prompt TEXT NOT NULL,
    cron_expr TEXT,
    timezone TEXT NOT NULL,
    run_at_ms INTEGER,
    next_fire_at_ms INTEGER,
    last_fired_at_ms INTEGER,
    max_occurrences INTEGER,
    fired_count INTEGER NOT NULL DEFAULT 0,
    coalesce_key TEXT,
    state TEXT NOT NULL CHECK (state IN ('active','done','canceled')),
    created_at_ms INTEGER NOT NULL,
    updated_at_ms INTEGER NOT NULL
);

CREATE INDEX idx_track_schedules_due
    ON track_schedules(state, next_fire_at_ms)
    WHERE state = 'active' AND next_fire_at_ms IS NOT NULL;

CREATE INDEX idx_track_schedules_track
    ON track_schedules(track_id, state, next_fire_at_ms);
