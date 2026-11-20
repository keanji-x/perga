import type { FormEvent } from 'react';
import { useState } from '../../../ui/state/public.ts';
import type { SetTrackScheduleRequest, TrackScheduleDto, TrackSchedulesSnapshot } from '../../../../../core/api/perga-v1.ts';
import styles from './protocol.module.css';

export type ScheduleActions = Readonly<{
  onSaveSchedule?: (request: SetTrackScheduleRequest) => Promise<void>;
  onCancelSchedule?: (id: string) => Promise<void>;
  onTriggerSchedule?: (id: string) => Promise<void>;
}>;

export function SchedulePanel({ schedules, recentRuns, onSaveSchedule, onCancelSchedule, onTriggerSchedule }: TrackSchedulesSnapshot & ScheduleActions) {
  const [kind, setKind] = useState('once');
  const [rearm, setRearm] = useState<TrackScheduleDto | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const perform = async (action: () => Promise<void>) => {
    if (busy) return;
    setBusy(true);
    setError('');
    try { await action(); } catch (cause) { setError(cause instanceof Error ? cause.message : 'Schedule update failed.'); }
    finally { setBusy(false); }
  };
  const save = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!onSaveSchedule) return;
    const form = event.currentTarget;
    const data = new FormData(form);
    const text = (key: string) => { const value = data.get(key); return typeof value === 'string' ? value : ''; };
    const runAtMs = Date.parse(text('runAt'));
    void perform(async () => {
      if (kind === 'once' && !Number.isFinite(runAtMs)) throw new Error('Choose a valid date and time.');
      await onSaveSchedule({ title: text('title'), prompt: text('prompt'),
        ...(rearm ? { id: rearm.id, ...(rearm.maxOccurrences === null ? {} : { maxOccurrences: rearm.maxOccurrences }) } : {}),
        ...(kind === 'cron' ? { cronExpr: text('cron'), timezone: text('timezone') } : { runAtMs }),
      });
      form.reset();
      setRearm(null);
    });
  };
  return <div className={styles.editor}>
    {schedules.length === 0 && <p>No schedules yet.</p>}
    <ul className={styles.scheduleList}>{schedules.map(schedule => <li key={schedule.id}>
      <strong>{schedule.title}</strong> <span>{schedule.state}</span>
      <p>{schedule.cronExpr ?? (schedule.runAtMs === null ? '' : new Date(schedule.runAtMs).toLocaleString())} · {schedule.timezone}</p>
      <p>{schedule.prompt}</p>
      {schedule.nextFireAtMs !== null && <p>Next: {new Date(schedule.nextFireAtMs).toLocaleString()}</p>}
      <div className={styles.actions}>{schedule.state === 'active' ? <>
        <button type="button" disabled={busy || !onTriggerSchedule} onClick={() => { void perform(() => onTriggerSchedule!(schedule.id)); }}>Run now</button>
        <button type="button" disabled={busy || !onCancelSchedule} onClick={() => { void perform(() => onCancelSchedule!(schedule.id)); }}>Cancel</button>
      </> : <button type="button" disabled={busy || !onSaveSchedule} onClick={() => { setRearm(schedule); setKind(schedule.scheduleKind); }}>Re-arm</button>}</div>
    </li>)}</ul>
    {onSaveSchedule && <form key={rearm?.id ?? 'new'} className={styles.form} onSubmit={save}>
      <h3>{rearm ? `Re-arm ${rearm.title}` : 'Add schedule'}</h3>
      <label>Title<input name="title" required maxLength={200} defaultValue={rearm?.title} disabled={busy} /></label>
      <label>Prompt<input name="prompt" required defaultValue={rearm?.prompt} disabled={busy} /></label>
      <label>Schedule type<select value={kind} onChange={event => setKind(event.target.value)} disabled={busy}>
        <option value="once">Once</option><option value="cron">Cron</option>
      </select></label>
      {kind === 'cron' ? <>
        <label>Cron expression<input name="cron" required defaultValue={rearm?.cronExpr ?? ''} disabled={busy} /></label>
        <label>Timezone<input name="timezone" required defaultValue={rearm?.timezone ?? 'UTC'} disabled={busy} /></label>
      </> : <label>Run at<input name="runAt" type="datetime-local" required disabled={busy} /></label>}
      <div className={styles.actions}><button type="submit" disabled={busy}>{busy ? 'Saving…' : rearm ? 'Save schedule' : 'Add schedule'}</button>
        {rearm && <button type="button" disabled={busy} onClick={() => setRearm(null)}>Stop editing</button>}</div>
    </form>}
    {error && <p role="alert">{error}</p>}
    <h3>Recent runs</h3>
    {recentRuns.length === 0 ? <p>No recent runs.</p> : <ul className={styles.scheduleList}>{recentRuns.map(run => <li key={run.wakeId}>
      <strong>{schedules.find(schedule => schedule.id === run.scheduleId)?.title ?? run.scheduleId ?? run.wakeId}</strong> · {run.state}
      <p>{run.reason} · {new Date(run.createdAtMs).toLocaleString()}</p>
      {run.turnId && <p>Turn: {run.turnId}</p>}
    </li>)}</ul>}
  </div>;
}
