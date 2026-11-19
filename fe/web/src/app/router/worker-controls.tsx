import { Banner } from '@astryxdesign/core/Banner';
import { Button } from '@astryxdesign/core/Button';
import { TextInput } from '@astryxdesign/core/TextInput';
import { VStack } from '@astryxdesign/core/VStack';
import { useQueryClient } from '@tanstack/react-query';
import { useRef } from 'react';
import type { WorkerActivity, WorkerSettings } from '../../../../core/api/perga-v1.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { workerDispatchOperation, workerToolActivity, type WorkerDispatchOptions, type WorkerMessageMode } from '../../../../core/domain/worker-session.ts';
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

export function WorkerSettingsControl({ worker, update }: {
  worker: WorkerActivity; update: (settings: WorkerSettings & { attemptId: string }, approvalKey?: string) => Promise<unknown>;
}) {
  const [draft, setDraft] = useState<WorkerSettings | null>(null);
  const [approvalKey, setApprovalKey] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const available = worker.phase === 'running' || worker.phase === 'paused';
  const configuration = worker.providerConfiguration;
  const modelFor = (model: string | null | undefined) => configuration?.models.find(option => option.id === (model ?? configuration.defaultModel));
  const effortsFor = (model: string | null | undefined) => configuration == null ? worker.reasoningEfforts
    : configuration.sessionReasoningEfforts ?? modelFor(model)?.reasoningEfforts ?? [];
  const efforts = effortsFor(draft?.model);
  const defaultEffort = draft?.model == null || draft.model === configuration?.defaultModel || configuration?.models.length === 0
    ? configuration?.defaultReasoningEffort : modelFor(draft.model)?.defaultReasoningEffort;
  const restoringApproval = worker.nextSettings.approvalMode === 'deny' && draft?.approvalMode === 'ask';
  const approvalAvailable = worker.approvalAuthorizationConfigured === true && globalThis.location?.protocol === 'https:';
  const proofReady = /^[a-fA-F0-9]{64}$/.test(approvalKey);
  return <>
    <Button label="Worker settings" variant="ghost" isDisabled={!available} onClick={() => {
      setError(null); setSaved(false); setApprovalKey(''); setDraft(worker.nextSettings);
    }} />
    {saved && <span role="status">Settings saved for the next turn.</span>}
    <Dialog purpose="form" origin={{ kind: 'surface-center' }} open={draft !== null} onClose={() => {
      if (!busy) { setApprovalKey(''); setDraft(null); }
    }} title="Worker settings">
      {draft !== null && <VStack as="form" gap={2} onSubmit={event => {
        event.preventDefault();
        if (busy || (restoringApproval && (!approvalAvailable || !proofReady))) return;
        setBusy(true); setError(null);
        const proof = restoringApproval ? approvalKey : undefined;
        setApprovalKey('');
        void update({ ...draft, model: draft.model?.trim() || null, attemptId: worker.attemptId }, proof).then(() => {
          setDraft(null); setSaved(true);
        }, (failure: unknown) => setError(failure instanceof Error ? failure.message : 'Could not save Worker settings.'))
          .finally(() => setBusy(false));
      }}>
        {worker.activeSettings != null && <section aria-label="Current turn settings">
          <h3>Current turn</h3>
          <dl>
            <dt>Model</dt><dd>{worker.activeSettings.model ?? 'Provider default'}</dd>
            <dt>Reasoning effort</dt><dd>{worker.activeSettings.reasoningEffort ?? 'Provider default'}</dd>
            <dt>Approvals</dt><dd>{worker.activeSettings.approvalMode === 'deny' ? 'Deny requests' : 'Ask a person'}</dd>
          </dl>
        </section>}
        <p>Changes apply from the next turn.</p>
        {error !== null && <Banner status="error" title={error} />}
        {worker.canChangeModel && configuration == null &&
          <TextInput label="Model" value={draft.model ?? ''} onChange={model => setDraft({ ...draft, model })} />}
        {configuration != null && configuration.models.length > 0 && <label>Model
          <select value={draft.model ?? ''} onChange={event => {
            const model = event.target.value || null;
            const reasoningEffort = draft.reasoningEffort != null && effortsFor(model).includes(draft.reasoningEffort)
              ? draft.reasoningEffort : null;
            setDraft({ ...draft, model, reasoningEffort });
          }}>
            <option value="" disabled={configuration.defaultModel == null && draft.model != null}>
              {configuration.defaultModel == null ? 'Current provider model' : `Provider default (${configuration.defaultModel})`}
            </option>
            {draft.model != null && modelFor(draft.model) === undefined && <option value={draft.model}>{draft.model} (current)</option>}
            {configuration.models.map(model => <option key={model.id} value={model.id}>{model.name}</option>)}
          </select>
        </label>}
        {(efforts.length > 0 || draft.reasoningEffort != null) && <label>Reasoning effort
          <select value={draft.reasoningEffort ?? ''} onChange={event => setDraft({ ...draft, reasoningEffort: event.target.value || null })}>
            <option value="" disabled={worker.nextSettings.reasoningEffort != null && defaultEffort == null}>
              {defaultEffort == null ? 'Provider default' : `Provider default (${defaultEffort})`}
            </option>
            {draft.reasoningEffort != null && !efforts.includes(draft.reasoningEffort) &&
              <option value={draft.reasoningEffort}>{draft.reasoningEffort} (current)</option>}
            {efforts.map(effort => <option key={effort} value={effort}>{effort}</option>)}
          </select>
        </label>}
        <label>Approvals
          <select value={draft.approvalMode} onChange={event => setDraft({ ...draft, approvalMode: event.target.value === 'deny' ? 'deny' : 'ask' })}>
            <option value="ask" disabled={worker.nextSettings.approvalMode === 'deny' && !approvalAvailable}>Ask a person</option>
            <option value="deny">Deny requests</option>
          </select>
        </label>
        {worker.nextSettings.approvalMode === 'deny' && !approvalAvailable &&
          <p>Restoring approval requests requires owner authorization over HTTPS.</p>}
        {restoringApproval && approvalAvailable && <TextInput label="Owner authorization key" type="password" value={approvalKey} onChange={setApprovalKey} />}
        <Button type="submit" label={busy ? 'Saving…' : 'Save settings'}
          isDisabled={busy || !available || (restoringApproval && (!approvalAvailable || !proofReady))} />
      </VStack>}
    </Dialog>
  </>;
}

export function WorkerDeliveryControl({ mode, select, nativeSteer }: {
  mode: WorkerMessageMode; select: (mode: WorkerMessageMode) => void; nativeSteer: boolean;
}) {
  return <label>Delivery
    <select value={mode} onChange={event => select(event.target.value === 'steer' ? 'steer' : 'queue')}>
      {nativeSteer && <option value="steer">Current turn (Steer)</option>}
      <option value="queue">Next turn (Queue)</option>
    </select>
  </label>;
}

export function WorkerActivityControl({ worker }: { worker: WorkerActivity }) {
  const [open, setOpen] = useState(false);
  const tools = open ? workerToolActivity(worker.events) : [];
  return <>
    <Button label="Worker activity" variant="ghost" onClick={() => setOpen(true)} />
    <Dialog purpose="info" origin={{ kind: 'surface-center' }} open={open} onClose={() => setOpen(false)} title="Worker activity">
      <VStack gap={2}>
        {worker.messages.length > 0 && <section>
          <h3>Message delivery</h3>
          <ul>{worker.messages.map(message => <li key={message.id}>{message.content} <strong>{message.delivery}</strong></li>)}</ul>
        </section>}
        {tools.length > 0 && <section>
          <h3>Current turn tools</h3>
          {tools.map(tool => <details key={tool.sequence}>
            <summary>{tool.title} · {tool.status}</summary>
            {tool.detail !== undefined && <pre style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{tool.detail}</pre>}
          </details>)}
        </section>}
        {worker.messages.length === 0 && tools.length === 0 && <p>No recent Worker activity.</p>}
      </VStack>
    </Dialog>
  </>;
}
