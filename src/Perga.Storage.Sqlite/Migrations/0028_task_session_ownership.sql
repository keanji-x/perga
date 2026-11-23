ALTER TABLE tasks ADD COLUMN owns_session INTEGER NOT NULL DEFAULT 1;

-- Template replacement can archive a task under a new key while retaining its original Flow block ID.
UPDATE tasks
SET owns_session = 0
WHERE (block_id = 'flow:' || task_key
       OR (archived_at_ms IS NOT NULL AND task_key = '@archived:' || id AND substr(block_id, 1, 5) = 'flow:'))
  AND json_type(context_json, '$.flowNode') IS 'true'
  AND (kind = 'flow_wake' OR json_type(context_json, '$.reuseSession') IS 'true');
