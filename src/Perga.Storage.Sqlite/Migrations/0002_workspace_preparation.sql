CREATE TABLE track_workspace_preparations (
    track_id TEXT PRIMARY KEY REFERENCES tracks(id) ON DELETE CASCADE,
    intent_json TEXT NOT NULL
);
