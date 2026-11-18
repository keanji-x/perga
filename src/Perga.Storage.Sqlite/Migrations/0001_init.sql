-- 必须在首次建表前设置，使数据库支持逐页增量回收给 OS 而无需全库阻塞 VACUUM
PRAGMA auto_vacuum = INCREMENTAL;
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;
PRAGMA temp_store = MEMORY;

-- 0. 迁移账本（字节冻结内嵌脚本 + SHA-256 校验）
CREATE TABLE IF NOT EXISTS schema_migrations (
    version             INTEGER PRIMARY KEY,
    name                TEXT NOT NULL,
    checksum_sha256     TEXT NOT NULL,
    applied_at_ms       INTEGER NOT NULL
);

-- 1. 领域分组与声明目录
CREATE TABLE IF NOT EXISTS areas (
    id                  TEXT PRIMARY KEY,
    name                TEXT NOT NULL,
    color               TEXT NOT NULL DEFAULT '#64748b',
    default_cwd         TEXT,
    folders_json        TEXT NOT NULL DEFAULT '[]',
    sort_order          REAL NOT NULL DEFAULT 0,
    created_at_ms       INTEGER NOT NULL,
    updated_at_ms       INTEGER NOT NULL
);

-- 2. Track 核心表（含主理指针、工作区挂载描述与模板快照/状态槽；零 Git 强耦合字段）
CREATE TABLE IF NOT EXISTS tracks (
    id                      TEXT PRIMARY KEY,
    area_id                 TEXT NOT NULL REFERENCES areas(id) ON DELETE CASCADE,
    parent_track_id         TEXT REFERENCES tracks(id) ON DELETE SET NULL,
    parent_attempt_id       TEXT,
    title                   TEXT NOT NULL DEFAULT '',
    cwd                     TEXT NOT NULL,
    workspace_driver        TEXT NOT NULL DEFAULT 'direct',
    workspace_spec_json     TEXT NOT NULL DEFAULT '{}',
    cleanup_cmd             TEXT,
    lead_session_id         TEXT,
    template_id             TEXT,
    template_snapshot       TEXT,
    template_inputs_json    TEXT NOT NULL DEFAULT '{}',
    template_state_json     TEXT NOT NULL DEFAULT '{}',
    enabled_plugins_json    TEXT NOT NULL DEFAULT '[]',
    closed_at_ms            INTEGER,
    pinned_at_ms            INTEGER,
    sort_order              REAL NOT NULL DEFAULT 0,
    created_at_ms           INTEGER NOT NULL,
    updated_at_ms           INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_tracks_area ON tracks(area_id, sort_order);
CREATE INDEX IF NOT EXISTS idx_tracks_parent ON tracks(parent_track_id) WHERE parent_track_id IS NOT NULL;

-- 3. Report 当前文档态
CREATE TABLE IF NOT EXISTS reports (
    track_id            TEXT PRIMARY KEY REFERENCES tracks(id) ON DELETE CASCADE,
    doc_rev             INTEGER NOT NULL DEFAULT 0,
    summary             TEXT NOT NULL DEFAULT '',
    leading_comment     TEXT NOT NULL DEFAULT '',
    blocks_json         TEXT NOT NULL DEFAULT '[]',
    updated_at_ms       INTEGER NOT NULL
);

-- 4. Report 历史 Zstd 压缩快照（替代原 5,000 行自研 track_vcs）
CREATE TABLE IF NOT EXISTS report_revisions (
    track_id            TEXT NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
    doc_rev             INTEGER NOT NULL,
    author_session_id   TEXT,
    message             TEXT NOT NULL DEFAULT '',
    blocks_zstd         BLOB NOT NULL,
    created_at_ms       INTEGER NOT NULL,
    PRIMARY KEY (track_id, doc_rev)
);

-- 5. 任务声明与调度唯一真相表
CREATE TABLE IF NOT EXISTS tasks (
    id                  TEXT PRIMARY KEY,
    track_id            TEXT NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
    block_id            TEXT NOT NULL,
    task_key            TEXT NOT NULL,
    generation          INTEGER NOT NULL DEFAULT 1,
    kind                TEXT NOT NULL,
    provider            TEXT,
    access_mode         TEXT NOT NULL DEFAULT 'read_write',
    ready               INTEGER NOT NULL DEFAULT 0,
    depends_on_json     TEXT NOT NULL DEFAULT '[]',
    goal_or_cmd         TEXT NOT NULL,
    context_json        TEXT,
    acceptance          TEXT,
    gate_json           TEXT,
    status              TEXT NOT NULL DEFAULT 'pending',
    status_detail       TEXT,
    semantic_verdict    TEXT NOT NULL DEFAULT 'none',
    semantic_note       TEXT,
    latest_attempt_id   TEXT,
    created_at_ms       INTEGER NOT NULL,
    updated_at_ms       INTEGER NOT NULL,
    UNIQUE (track_id, task_key)
);
CREATE INDEX IF NOT EXISTS idx_tasks_track_status ON tasks(track_id, status);

-- 6. 任务执行 Attempt 轨迹（含 afterWorkerDoneCmd 采集的通用 meta_json 与 Per-Attempt 租约快照）
CREATE TABLE IF NOT EXISTS task_attempts (
    attempt_id           TEXT PRIMARY KEY,
    task_id              TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    track_id             TEXT NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
    session_id           TEXT,
    status               TEXT NOT NULL,
    worker_result_json   TEXT,
    meta_json            TEXT NOT NULL DEFAULT '{}',
    workspace_lease_json TEXT,
    gate_verdict_json    TEXT,
    started_at_ms        INTEGER NOT NULL,
    finished_at_ms       INTEGER
);
CREATE INDEX IF NOT EXISTS idx_attempts_task ON task_attempts(task_id, started_at_ms);
CREATE INDEX IF NOT EXISTS idx_attempts_track ON task_attempts(track_id);

-- 7. 大一统会话表（二元模型：Lead 由 tracks.lead_session_id 指针认定，其余均为 Subagent，彻底消除 Planner/Assistant/Worker 三态）
CREATE TABLE IF NOT EXISTS agent_sessions (
    id                  TEXT PRIMARY KEY,
    track_id            TEXT NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
    bound_task_id       TEXT,                 -- 非空表示该 Subagent 关联某 Task；空则为自由协作 Subagent
    bound_attempt_id    TEXT,                 -- 非空表示当前背负在途/历史 TaskAttempt（启用 task_done/fail 并锁 task 块改写）
    title               TEXT,
    provider            TEXT NOT NULL,
    model               TEXT,
    native_thread_id    TEXT,
    epoch               INTEGER NOT NULL DEFAULT 1,
    status              TEXT NOT NULL DEFAULT 'idle',
    context_usage_ratio REAL NOT NULL DEFAULT 0,
    archived_at_ms      INTEGER,
    created_at_ms       INTEGER NOT NULL,
    updated_at_ms       INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_sessions_track ON agent_sessions(track_id);

-- 8A. 会话轮次（队列、派发及执行结果）
CREATE TABLE IF NOT EXISTS session_turns (
    id                  TEXT PRIMARY KEY,
    session_id          TEXT NOT NULL REFERENCES agent_sessions(id) ON DELETE CASCADE,
    turn_index          INTEGER NOT NULL,
    epoch               INTEGER NOT NULL DEFAULT 1,
    trigger_kind        TEXT NOT NULL,        -- 'user' | 'wake' | 'system'
    input_json          TEXT,
    status              TEXT NOT NULL,        -- 'queued' | 'running' | 'completed' | 'failed' | 'interrupted' | 'unknown'
    usage_json          TEXT,
    error_text          TEXT,
    started_at_ms       INTEGER,
    completed_at_ms     INTEGER,
    accepted_at_ms      INTEGER,
    provider            TEXT,
    model               TEXT,
    provider_turn_ref   TEXT,
    UNIQUE (session_id, id)
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_turns_session_idx ON session_turns(session_id, turn_index);
CREATE INDEX IF NOT EXISTS idx_turns_session_epoch ON session_turns(session_id, epoch);

-- 8B. 已接收的会话输入及派发状态
CREATE TABLE IF NOT EXISTS session_inputs (
    id                  TEXT PRIMARY KEY,
    session_id          TEXT NOT NULL,
    turn_id             TEXT NOT NULL UNIQUE,
    author_id           TEXT NOT NULL CHECK (length(author_id) > 0),
    request_key         TEXT NOT NULL CHECK (length(request_key) BETWEEN 1 AND 256),
    request_hash        TEXT NOT NULL,
    content             TEXT NOT NULL,
    accepted_at_ms      INTEGER NOT NULL,
    delivery_state      TEXT NOT NULL CHECK (delivery_state IN ('accepted', 'dispatching', 'delivered', 'rejected', 'unknown')),
    dispatch_started_at_ms INTEGER,
    resolved_at_ms      INTEGER,
    FOREIGN KEY (session_id, turn_id) REFERENCES session_turns(session_id, id) ON DELETE CASCADE,
    UNIQUE (session_id, author_id, request_key),
    UNIQUE (id, session_id, turn_id)
);
CREATE INDEX IF NOT EXISTS idx_inputs_delivery ON session_inputs(delivery_state, accepted_at_ms, id);

-- 8C. 会话条目（流式 Delta 走内存 WS，>64KB 大输出剥离至外部冷存储）
CREATE TABLE IF NOT EXISTS session_items (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id          TEXT NOT NULL REFERENCES agent_sessions(id) ON DELETE CASCADE,
    turn_id             TEXT NOT NULL,
    kind                TEXT NOT NULL,        -- 'user' | 'thinking' | 'assistant_text' | 'tool_call' | 'tool_result'
    payload_json        TEXT NOT NULL,
    created_at_ms       INTEGER NOT NULL,
    accepted_input_id   TEXT UNIQUE,
    FOREIGN KEY (session_id, turn_id) REFERENCES session_turns(session_id, id) ON DELETE CASCADE,
    FOREIGN KEY (accepted_input_id, session_id, turn_id) REFERENCES session_inputs(id, session_id, turn_id),
    CHECK (accepted_input_id IS NULL OR kind = 'user')
);
CREATE INDEX IF NOT EXISTS idx_items_session ON session_items(session_id, id);
CREATE INDEX IF NOT EXISTS idx_items_turn ON session_items(turn_id);

-- 8D. 创建回执（资源删除后保留，重试仍返回 Gone）
CREATE TABLE IF NOT EXISTS creation_receipts (
    author_id           TEXT NOT NULL,
    operation           TEXT NOT NULL,
    scope_id            TEXT NOT NULL,
    request_key         TEXT NOT NULL CHECK (length(request_key) BETWEEN 1 AND 256),
    request_hash        TEXT NOT NULL,
    resource_id         TEXT NOT NULL,
    first_input_id      TEXT,
    created_at_ms       INTEGER NOT NULL,
    PRIMARY KEY (author_id, operation, scope_id, request_key)
);

-- 9A. 用户审批问答
CREATE TABLE IF NOT EXISTS asks (
    id                  TEXT PRIMARY KEY,
    track_id            TEXT NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
    status              TEXT NOT NULL DEFAULT 'open',
    questions_json      TEXT NOT NULL,
    answer_json         TEXT,
    created_at_ms       INTEGER NOT NULL,
    answered_at_ms      INTEGER
);
CREATE INDEX IF NOT EXISTS idx_asks_track ON asks(track_id, status);

-- 9B. 跨 Track 邮件（投递后自动唤醒目标 Track 的 lead_session_id）
CREATE TABLE IF NOT EXISTS mails (
    id                  TEXT PRIMARY KEY,
    from_track_id       TEXT NOT NULL,
    to_track_id         TEXT NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
    summary             TEXT NOT NULL,
    body                TEXT NOT NULL,
    reply_to_mail_id    TEXT,
    wake_pending        INTEGER NOT NULL DEFAULT 0 CHECK (wake_pending IN (0, 1)),
    read_at_ms          INTEGER,
    created_at_ms       INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_mails_to_track ON mails(to_track_id, created_at_ms DESC);
CREATE INDEX IF NOT EXISTS idx_mails_pending_wake ON mails(created_at_ms, id) WHERE wake_pending = 1;

-- 10A. 终端元数据
CREATE TABLE IF NOT EXISTS terminals (
    id                  TEXT PRIMARY KEY,
    track_id            TEXT NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
    attempt_id          TEXT,
    idempotency_key     TEXT,
    title               TEXT NOT NULL DEFAULT 'Terminal',
    program             TEXT,
    cwd                 TEXT NOT NULL,
    status              TEXT NOT NULL,
    exit_code           INTEGER,
    created_at_ms       INTEGER NOT NULL,
    exited_at_ms        INTEGER
);
CREATE INDEX IF NOT EXISTS idx_terminals_track ON terminals(track_id);

-- 10B. 增量事件流水（供 WS ?since= 断线重放，受滑窗上限约束）
CREATE TABLE IF NOT EXISTS events (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    area_id             TEXT,
    track_id            TEXT,
    kind                TEXT NOT NULL,
    payload_json        TEXT NOT NULL,
    created_at_ms       INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_events_track_id ON events(track_id, id);
CREATE INDEX IF NOT EXISTS idx_events_created ON events(created_at_ms);
