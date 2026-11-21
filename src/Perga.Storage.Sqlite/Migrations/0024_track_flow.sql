ALTER TABLE tracks ADD COLUMN flow_spec_json TEXT NOT NULL DEFAULT '{"rev":0,"nodes":[]}';
ALTER TABLE tracks ADD COLUMN flow_rev INTEGER NOT NULL DEFAULT 0;
ALTER TABLE tracks ADD COLUMN flow_file_sha256 TEXT;

ALTER TABLE tasks ADD COLUMN trigger_run_key TEXT;
ALTER TABLE tasks ADD COLUMN outcome TEXT NOT NULL DEFAULT 'none';
ALTER TABLE tasks ADD COLUMN outcome_summary TEXT;
ALTER TABLE tasks ADD COLUMN archived_at_ms INTEGER;

ALTER TABLE track_schedules ADD COLUMN node_key TEXT;
