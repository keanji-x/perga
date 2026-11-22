import { DropdownMenu, DropdownMenuItem } from '@astryxdesign/core/DropdownMenu';
import { Badge } from '@astryxdesign/core/Badge';
import { Icon } from '../../../ui/icon/public.tsx';
import type { AgentSessionDto, SessionItemsPageDto } from '../../../../../core/api/perga-v1.ts';
import styles from './session-header.module.css';

export function ConversationSessionHeader({ session, state, taskId, runtimeStatus, runtimeTasks, readonlyReason, workerPaused, compactEnabled, resetEnabled, onCompact, onReset, onArchive }: Readonly<{
  session: AgentSessionDto | null;
  taskId: string | null;
  state: SessionItemsPageDto['state'] | null;
  runtimeStatus?: SessionItemsPageDto['runtimeStatus'];
  runtimeTasks?: SessionItemsPageDto['runtimeTasks'] | readonly NonNullable<SessionItemsPageDto['runtimeTasks']>[number][];
  readonlyReason: SessionItemsPageDto['readonlyReason'];
  workerPaused: boolean;
  compactEnabled: boolean;
  resetEnabled: boolean;
  onCompact: () => void;
  onReset: () => void;
  onArchive?: () => void;
}>) {
  const phase = runtimeStatus?.kind === 'running' ? runtimeStatus.phase : null;
  const phaseLabel = phase === 'thinking' ? '思考中' : phase === 'responding' ? '回复中'
    : phase === 'waiting_model' ? '等待模型' : phase === 'compacting' ? '压缩中'
      : phase === 'tool' ? `工具：${runtimeStatus?.toolName ?? '执行中'}` : null;
  const backgroundTasks = runtimeTasks?.filter(task => !task.ambient &&
    (task.status === 'running' || task.status === 'pending' || task.status === 'paused')).length ?? 0;
  return <div className={styles.header} data-nc-session-header="">
    {session !== null && <span className={styles.epoch}>{taskId !== null ? 'Worker' : session.isLead ? 'Planner' : 'Assistant'} · Epoch {session.epoch}</span>}
    {state !== null && <Badge variant={state === 'unknown' ? 'error' : state === 'stopping' || workerPaused ? 'warning' : 'neutral'} label={state === 'idle' ? workerPaused ? '已暂停' : '空闲' : state === 'running' ? '运行中' : state === 'stopping' ? '停止中' : state === 'compacting' ? '压缩中' : '状态未知'} />}
    {readonlyReason !== null && <Badge label={readonlyReason === 'archived' ? '已归档' : readonlyReason === 'track_closed' ? 'Track 已关闭' : readonlyReason === 'waiting_task' ? '等待任务' : readonlyReason === 'task_verifying' ? '验证中' : '已结束'} />}
    {phaseLabel !== null && <Badge label={phaseLabel} />}
    {runtimeStatus?.kind === 'idle' && runtimeStatus.idleActivity != null && <Badge label="近期后台活动" />}
    {backgroundTasks > 0 && <Badge label={`运行时任务 ${backgroundTasks}`} />}
    <DropdownMenu placement="below" alignment="end" button={{ label: '会话操作', icon: <Icon name="more" size="sm" />, isIconOnly: true, variant: 'ghost', size: 'sm' }}>
      <DropdownMenuItem label="压缩上下文 /compact" isDisabled={!compactEnabled} onClick={onCompact} />
      <DropdownMenuItem label="重置上下文 /reset" isDisabled={!resetEnabled} onClick={onReset} />
      {onArchive !== undefined && <DropdownMenuItem label={readonlyReason === 'archived' ? '恢复对话' : '归档对话'} onClick={onArchive} />}
    </DropdownMenu>
  </div>;
}
