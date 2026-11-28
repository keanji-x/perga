import { Button } from '@astryxdesign/core/Button';
import { Divider } from '@astryxdesign/core/Divider';
import { DropdownMenu, DropdownMenuItem } from '@astryxdesign/core/DropdownMenu';
import { Icon } from '../../../ui/icon/public.tsx';
import type { AgentSessionDto, SessionItemsPageDto } from '../../../../../core/api/perga-v1.ts';
import styles from './session-header.module.css';

type StatusTone = 'live' | 'warning' | 'error' | 'muted';

function SessionChip({ label, tone }: Readonly<{ label: string; tone: StatusTone }>) {
  return <span className={`${styles.chip} ${styles[tone]}`}>
    {(tone === 'live' || tone === 'warning' || tone === 'error') && <span className={styles.dot} aria-hidden="true" />}
    <span className={styles.chipText}>{label}</span>
  </span>;
}

export function MemorySaveButton({ pending = false, disabled = false, tooltip = 'Save to memory (/mem)', onSave }: Readonly<{
  pending?: boolean;
  disabled?: boolean;
  tooltip?: string;
  onSave: () => void;
}>) {
  return <Button isIconOnly label="Save to memory" tooltip={tooltip}
    icon={<Icon name="notebook-pen" size="sm" />} variant="ghost" size="sm" className={styles.menuButton}
    isDisabled={pending || disabled} onClick={onSave} />;
}

export function ConversationSessionHeader({ session, state, taskId, readonlyReason, workerPaused, mcpHealth, compactEnabled, resetEnabled, onCompact, onReset, onArchive }: Readonly<{
  session: AgentSessionDto | null;
  taskId: string | null;
  state: SessionItemsPageDto['state'] | null;
  readonlyReason: SessionItemsPageDto['readonlyReason'];
  workerPaused: boolean;
  mcpHealth: SessionItemsPageDto['mcpHealth'];
  compactEnabled: boolean;
  resetEnabled: boolean;
  onCompact: () => void;
  onReset: () => void;
  onArchive?: () => void;
}>) {
  const roleLabel = session === null ? null : taskId !== null ? 'Worker' : session.isLead ? 'Lead' : 'Assistant';
  // Live execution phases and background task liveness belong to the bottom turn status bar.
  // Keep the header quiet unless a session-level condition or readonly lock needs attention.
  const sessionNotice: Readonly<{ label: string; tone: StatusTone }> | null = state === 'unknown'
    ? { label: 'Status unknown', tone: 'error' }
    : state === 'stopping'
      ? { label: 'Stopping…', tone: 'warning' }
      : state === 'idle' && workerPaused
        ? { label: 'Paused', tone: 'warning' }
        : state === 'compacting'
          ? { label: 'Compacting…', tone: 'live' }
          : null;
  const readonlyLabel = readonlyReason === null ? null
    : readonlyReason === 'archived' ? 'Archived'
      : readonlyReason === 'track_closed' ? 'Track closed'
        : readonlyReason === 'waiting_task' ? 'Waiting on task'
          : readonlyReason === 'task_verifying' ? 'Verifying' : 'Ended';

  return <div className={styles.header} data-nc-session-header="">
    <div className={styles.signals}>
      {sessionNotice !== null && <SessionChip label={sessionNotice.label} tone={sessionNotice.tone} />}
      {mcpHealth != null && <SessionChip label="Tools disconnected" tone="error" />}
      {readonlyLabel !== null && <SessionChip label={readonlyLabel} tone="muted" />}
    </div>
    <DropdownMenu placement="below" alignment="end" button={{ label: 'Session actions', icon: <Icon name="more" size="sm" />, isIconOnly: true, variant: 'ghost', size: 'sm', className: styles.menuButton }}>
      {roleLabel !== null && <>
        <div className={styles.menuMeta} role="note">
          <span className={styles.menuRole}>{roleLabel}</span>
        </div>
        <Divider />
      </>}
      <DropdownMenuItem label="Compact context /compact" isDisabled={!compactEnabled} onClick={onCompact} />
      <DropdownMenuItem label="Reset context /reset" variant="destructive" isDisabled={!resetEnabled} onClick={onReset} />
      {onArchive !== undefined && <DropdownMenuItem label={readonlyReason === 'archived' ? 'Restore conversation' : 'Archive conversation'} onClick={onArchive} />}
    </DropdownMenu>
  </div>;
}
