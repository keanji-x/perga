ALTER TABLE session_messages ADD COLUMN attachments_json TEXT;

CREATE TABLE track_media_refs (
    track_id TEXT NOT NULL,
    file_name TEXT NOT NULL,
    PRIMARY KEY (track_id, file_name),
    FOREIGN KEY (track_id) REFERENCES tracks(id) ON DELETE CASCADE
);
