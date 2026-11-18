CREATE INDEX idx_session_items_blob_ref ON session_items (json_extract(payload_json, '$.blobRef'))
    WHERE json_type(payload_json, '$.blobRef') = 'text';

CREATE INDEX idx_attempts_gate_blob_ref ON task_attempts (json_extract(gate_verdict_json, '$.blobRef'))
    WHERE json_type(gate_verdict_json, '$.blobRef') = 'text';
