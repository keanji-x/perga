// Live execution-scoped approvals (provider tool requests and policy-guard pauses)
// rendered above the owning session's composer. Distinct from durable Planner asks (`asks.tsx`).

import { useRef } from 'react';
import { Button } from '@astryxdesign/core/Button';
import { ChatComposerDrawer } from '@astryxdesign/core/Chat';

import {
  approvalDecisionFailureText, type PendingApproval,
} from '../../../../core/domain/approval.ts';
import { OperationFeedback, useOperationFeedback } from '../../ui/operation-feedback/public.tsx';
import { useState } from '../../ui/state/public.ts';
import styles from './asks.module.css';

export type DecideApproval = (approval: PendingApproval, optionId: string) => Promise<void>;
export type ApprovalDrawerProps = Readonly<{
  approvals: readonly PendingApproval[];
  onDecide: DecideApproval;
}>;

export function ApprovalDrawer({ approvals, onDecide }: ApprovalDrawerProps) {
  const [settled, setSettled] = useState<ReadonlySet<string>>(() => new Set());
  const [selected, setSelected] = useState<string | null>(null);
  const [queueOpen, setQueueOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const open = approvals.filter((approval) => !settled.has(approval.id));
  const current = open.find((approval) => approval.id === selected) ?? open[0];
  if (current === undefined) return null;

  return <ChatComposerDrawer count={open.length} label="Approval" className={styles.drawer}>
    <div className={styles.asks} data-nc-asks="">
      <ApprovalForm
        key={current.id}
        approval={current}
        onDecide={onDecide}
        onBusy={setBusy}
        onSettled={() => {
          setSettled((previous) => new Set(previous).add(current.id));
          setSelected(null);
          setQueueOpen(false);
        }}
      />
      {open.length > 1 && <div className={styles.queue}>
        <Button
          label={`${open.length - 1} more ${open.length === 2 ? 'approval' : 'approvals'}`}
          variant="secondary"
          size="sm"
          className={styles.queueToggle}
          isDisabled={busy}
          aria-expanded={queueOpen}
          onClick={() => setQueueOpen((value) => !value)}
        />
        {queueOpen && <div className={styles.queueList} role="group" aria-label="Other pending approvals">
          {open.filter((approval) => approval.id !== current.id).map((approval) => (
            <Button
              key={approval.id}
              label={approval.title}
              variant="secondary"
              size="sm"
              className={styles.queueItem}
              isDisabled={busy}
              onClick={() => { setSelected(approval.id); setQueueOpen(false); }}
            />
          ))}
        </div>}
      </div>}
    </div>
  </ChatComposerDrawer>;
}

function ApprovalForm({ approval, onDecide, onBusy, onSettled }: Readonly<{
  approval: PendingApproval;
  onDecide: DecideApproval;
  onBusy: (busy: boolean) => void;
  onSettled: () => void;
}>) {
  const feedback = useOperationFeedback();
  const [busy, setBusy] = useState(false);
  const lock = useRef(false);

  const choose = async (optionId: string) => {
    if (lock.current) return;
    lock.current = true;
    setBusy(true);
    onBusy(true);
    try {
      const success = await feedback.run(onDecide(approval, optionId), approvalDecisionFailureText);
      if (success) onSettled();
    } finally {
      lock.current = false;
      setBusy(false);
      onBusy(false);
    }
  };

  return <section role="group" aria-label="Approval required" className={styles.ask} data-nc-ask={approval.id}>
    <p className={styles.paused} data-nc-ask-paused="">Turn paused, waiting for your approval</p>
    <h3 className={styles.title}>{approval.title}</h3>
    <div className={styles.questions} data-nc-ask-questions="">
      <div className={styles.optionList} data-nc-ask-options="">
        {approval.options.map((option) => (
          <Button
            key={option.id}
            label={option.label}
            variant="secondary"
            className={styles.option}
            isDisabled={busy}
            onClick={() => { void choose(option.id); }}
          >
            <span className={styles.optionContent}>
              <span className={styles.optionLabel}>{option.label}</span>
              <span className={styles.optionHint} aria-hidden="true">↵</span>
            </span>
          </Button>
        ))}
      </div>
    </div>
    {busy && <span className={styles.meta} role="status">Sending decision…</span>}
    <OperationFeedback feedback={feedback} />
  </section>;
}
