import { Banner } from '@astryxdesign/core/Banner';
import { Button } from '@astryxdesign/core/Button';
import { TextInput } from '@astryxdesign/core/TextInput';
import { VStack } from '@astryxdesign/core/VStack';
import { useQueryClient } from '@tanstack/react-query';
import { useRef } from 'react';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { workerDispatchOperation, type WorkerDispatchOptions } from '../../../../core/domain/worker-session.ts';
import { Dialog } from '../../ui/dialog/public.tsx';
import { useState } from '../../ui/state/public.ts';
import { mintIdempotencyKey } from '../providers/idempotency-key.ts';
import { queryKeys, runOperation } from '../providers/queries.ts';
import { admitTransport } from '../providers/recovery-mutation.ts';

type OptionalDispatchFields = Readonly<{
  access: '' | NonNullable<WorkerDispatchOptions['access']>;
  acceptance: string;
  dependencies: string;
  gate: string;
  provider: string;
  model: string;
}>;

const EMPTY_FIELDS: OptionalDispatchFields = Object.freeze({ access: '', acceptance: '', dependencies: '', gate: '', provider: '', model: '' });

export function WorkerDispatchControl({ trackId, transport, unauthorized }: {
  trackId: string; transport: ApiTransportPort; unauthorized: UnauthorizedChannel;
}) {
  const client = useQueryClient();
  const [opened, setOpened] = useState(false);
  const [goal, setGoal] = useState('');
  const [fields, setFields] = useState<OptionalDispatchFields>(EMPTY_FIELDS);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [accepted, setAccepted] = useState(false);
  const input = useRef<HTMLInputElement | null>(null);
  const intent = useRef<{ payload: string; key: string } | null>(null);

  const dispatch = async () => {
    const normalized = goal.trim();
    if (busy || normalized === '') return;
    const dependsOn = fields.dependencies.split(',').map(value => value.trim()).filter(Boolean);
    const options: WorkerDispatchOptions = {
      ...(fields.access === '' ? {} : { access: fields.access }),
      ...(fields.acceptance.trim() === '' ? {} : { acceptance: fields.acceptance.trim() }),
      ...(dependsOn.length === 0 ? {} : { dependsOn }),
      ...(fields.gate.trim() === '' ? {} : { gate: fields.gate.trim() }),
      ...(fields.provider.trim() === '' ? {} : { provider: fields.provider.trim() }),
      ...(fields.model.trim() === '' ? {} : { model: fields.model.trim() }),
    };
    const payload = JSON.stringify({ goal: normalized, ...options });
    if (intent.current?.payload !== payload) intent.current = { payload, key: mintIdempotencyKey() };
    setBusy(true);
    setError(null);
    try {
      await runOperation(admitTransport(transport), workerDispatchOperation(trackId, intent.current.key, normalized, options), unauthorized);
      intent.current = null;
      setGoal('');
      setFields(EMPTY_FIELDS);
      setOpened(false);
      setAccepted(true);
      void Promise.all([client.invalidateQueries({ queryKey: queryKeys.trackReport(trackId) }),
        client.invalidateQueries({ queryKey: queryKeys.trackDetail(trackId) })]);
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
        <details>
          <summary>Dispatch options</summary>
          <VStack gap={2}>
            <label>Access
              <select value={fields.access} onChange={event => {
                const value = event.target.value;
                setFields({ ...fields, access: value === 'read_only' || value === 'read_write' ? value : '' });
              }}>
                <option value="">Template default</option>
                <option value="read_only">Read-only</option>
                <option value="read_write">Read and write</option>
              </select>
            </label>
            <TextInput label="Acceptance criteria" value={fields.acceptance} onChange={acceptance => setFields({ ...fields, acceptance })} />
            <TextInput label="Dependencies" description="Comma-separated task keys" value={fields.dependencies}
              onChange={dependencies => setFields({ ...fields, dependencies })} />
            <TextInput label="Validation command" description="Use the command configured by the Track template" value={fields.gate}
              onChange={gate => setFields({ ...fields, gate })} />
            <TextInput label="Provider" value={fields.provider} onChange={provider => setFields({ ...fields, provider })} />
            <TextInput label="Model" value={fields.model} onChange={model => setFields({ ...fields, model })} />
          </VStack>
        </details>
        <Button type="submit" label={busy ? 'Dispatching…' : 'Dispatch'} isDisabled={busy || goal.trim() === ''} />
      </VStack>
    </Dialog>
  </>;
}
