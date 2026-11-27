CREATE INDEX IF NOT EXISTS idx_items_session_assistant_latest
    ON session_items(session_id, id DESC) WHERE kind = 'assistant_text';
