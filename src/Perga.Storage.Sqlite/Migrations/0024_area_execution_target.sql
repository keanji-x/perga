-- Area-level execution target isolation (default 'local' for existing and standard areas).
ALTER TABLE areas ADD COLUMN execution_target_id TEXT NOT NULL DEFAULT 'local';
