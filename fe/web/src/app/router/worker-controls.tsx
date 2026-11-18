import { Banner } from '@astryxdesign/core/Banner';
import { Button } from '@astryxdesign/core/Button';
import { TextInput } from '@astryxdesign/core/TextInput';
import { VStack } from '@astryxdesign/core/VStack';
import { useQueryClient } from '@tanstack/react-query';
import { useRef } from 'react';
import type { WorkerActivity, WorkerSettings } from '../../../../core/api/perga-v1.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { workerDispatchOperation } from '../../../../core/domain/worker-session.ts';
import { Dialog } from '../../ui/dialog/public.tsx';
import { useState } from '../../ui/state/public.ts';
import { mintIdempotencyKey } from '../providers/idempotency-key.ts';
import { queryKeys, runOperation } from '../providers/queries.ts';
import { admitTransport } from '../providers/recovery-mutation.ts';

export function WorkerDispatchControl({ trackId, transport, unauthorized }: {
  trackId: string; transport: ApiTransportPort; unauthorized: UnauthorizedChannel;
}) {
  const client = useQueryClient();
  const [opened, setOpened] = useState(false);
  const [goal, setGoal] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [accepted, setAccepted] = useState(false);
  const input = useRef<HTMLInputElement | null>(null);
  const intent = useRef<{ goal: string; key: string } | null>(null);

  const dispatch = async () => {
    const normalized = goal.trim();
    if (busy || normalized === '') return;
    if (intent.current?.goal !== normalized) intent.current = { goal: normalized, key: mintIdempotencyKey() };
    setBusy(true);
    setError(null);
    try {
      await runOperation(admitTransport(transport), workerDispatchOperation(trackId, intent.current.key, normalized), unauthorized);
      intent.current = null;
      setGoal('');
      setOpened(false);
      setAccepted(true);
      void Promise.all([client.invalidateQueries({ queryKey: queryKeys.trackReport(trackId) }),
        client.invalidateQueries({ queryKey: queryKeys.trackDetail(trackId) }),
        client.invalidateQueries({ queryKey: queryKeys.trackConversations(trackId) })]);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Could not dispatch the Worker.');
    } finally { setBusy(false); }
  };

  return <>
    <Button label="Dispatch Worker" variant="ghost" onClick={() => { setAccepted(false); setOpened(true); }} />
    {accepted && <span role="status">Worker task queued.</span>}
    <Dialog purpose="form" origin={{ kind: 'surface-center' }} open={opened} onClose={() => { if (!busy) setOpened(false); }}
      title="Dispatch Worker" initialFocusRef={input}>
      <VStack as="form" gap={2} onSubmit={event => { event.preventDefault(); void dispatch(); }}>
        {error !== null && <Banner status="error" title={error} />}
        <TextInput ref={input} label="Goal" value={goal} onChange={setGoal} isRequired />
        <Button type="submit" label={busy ? 'Dispatching…' : 'Dispatch'} isDisabled={busy || goal.trim() === ''} />
      </VStack>
    </Dialog>
  </>;
}

export function WorkerSettingsControl({ worker, update }: {
  worker: WorkerActivity; update: (settings: WorkerSettings & { attemptId: string }) => Promise<unknown>;
}) {
  const [draft, setDraft] = useState<WorkerSettings | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const available = worker.phase === 'running' || worker.phase === 'paused';
  return <>
    <Button label="Worker settings" variant="ghost" isDisabled={!available} onClick={() => {
      setError(null); setSaved(false); setDraft(worker.nextSettings);
    }} />
    {saved && <span role="status">Settings saved for the next turn.</span>}
    <Dialog purpose="form" origin={{ kind: 'surface-center' }} open={draft !== null} onClose={() => { if (!busy) setDraft(null); }} title="Worker settings">
      {draft !== null && <VStack as="form" gap={2} onSubmit={event => {
        event.preventDefault();
        if (busy) return;
        setBusy(true); setError(null);
        void update({ ...draft, model: draft.model?.trim() || null, attemptId: worker.attemptId }).then(() => {
          setDraft(null); setSaved(true);
        }, (failure: unknown) => setError(failure instanceof Error ? failure.message : 'Could not save Worker settings.'))
          .finally(() => setBusy(false));
      }}>
        <p>Changes apply from the next turn.</p>
        {error !== null && <Banner status="error" title={error} />}
        {worker.canChangeModel && <TextInput label="Model" value={draft.model ?? ''} onChange={model => setDraft({ ...draft, model })} />}
        {worker.reasoningEfforts.length > 0 && <label>Reasoning effort
          <select value={draft.reasoningEffort ?? ''} onChange={event => setDraft({ ...draft, reasoningEffort: event.target.value || null })}>
            <option value="" disabled={worker.nextSettings.reasoningEffort != null}>Provider default</option>
            {worker.reasoningEfforts.map(effort => <option key={effort} value={effort}>{effort}</option>)}
          </select>
        </label>}
        <label>Approvals
          <select value={draft.approvalMode} onChange={event => setDraft({ ...draft, approvalMode: event.target.value === 'deny' ? 'deny' : 'ask' })}>
            <option value="ask" disabled={worker.nextSettings.approvalMode === 'deny'}>Ask a person</option>
            <option value="deny">Deny requests</option>
          </select>
        </label>
        <Button type="submit" label={busy ? 'Saving…' : 'Save settings'} isDisabled={busy || !available} />
      </VStack>}
    </Dialog>
  </>;
}
