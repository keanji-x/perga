import { DropdownMenu, DropdownMenuItem } from '@astryxdesign/core/DropdownMenu';
import { Badge } from '@astryxdesign/core/Badge';
import { Icon } from '../../../ui/icon/public.tsx';
import type { AgentSessionDto, SessionItemsPageDto } from '../../../../../core/api/perga-v1.ts';
import styles from './session-header.module.css';

export function ConversationSessionHeader({ session, state, taskId, readonlyReason, workerPaused, compactEnabled, resetEnabled, onCompact, onReset, onArchive }: Readonly<{
  session: AgentSessionDto | null;
  taskId: string | null;
  state: SessionItemsPageDto['state'] | null;
  readonlyReason: SessionItemsPageDto['readonlyReason'];
  workerPaused: boolean;
  compactEnabled: boolean;
  resetEnabled: boolean;
  onCompact: () => void;
  onReset: () => void;
  onArchive?: () => void;
}>) {
  return <div className={styles.header} data-nc-session-header="">
    {session !== null && <span className={styles.epoch}>{taskId !== null ? 'Worker' : session.isLead ? 'Planner' : 'Assistant'} · Epoch {session.epoch}</span>}
    {state !== null && <Badge variant={state === 'unknown' ? 'error' : state === 'stopping' || workerPaused ? 'warning' : 'neutral'} label={state === 'idle' ? workerPaused ? '已暂停' : '空闲' : state === 'running' ? '运行中' : state === 'stopping' ? '停止中' : state === 'compacting' ? '压缩中' : '状态未知'} />}
    {readonlyReason !== null && <Badge label={readonlyReason === 'archived' ? '已归档' : readonlyReason === 'track_closed' ? 'Track 已关闭' : readonlyReason === 'waiting_task' ? '等待任务' : '已结束'} />}
    <DropdownMenu placement="below" alignment="end" button={{ label: '会话操作', icon: <Icon name="more" size="sm" />, isIconOnly: true, variant: 'ghost', size: 'sm' }}>
      <DropdownMenuItem label="压缩上下文 /compact" isDisabled={!compactEnabled} onClick={onCompact} />
      <DropdownMenuItem label="重置上下文 /reset" isDisabled={!resetEnabled} onClick={onReset} />
      {onArchive !== undefined && <DropdownMenuItem label={readonlyReason === 'archived' ? '恢复对话' : '归档对话'} onClick={onArchive} />}
    </DropdownMenu>
  </div>;
}
