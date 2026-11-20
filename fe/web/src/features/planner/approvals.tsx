// Live execution-scoped approvals (provider tool requests and policy-guard pauses)
// rendered above the owning session's composer. Distinct from durable Planner asks (`asks.tsx`).

import { useRef } from 'react';
import { Button } from '@astryxdesign/core/Button';
import { PendingInputDrawer } from './pending-input-drawer.tsx';

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
  return <PendingInputDrawer items={approvals} idOf={approval => approval.id} titleOf={approval => approval.title}
    label="Approval" noun="approval" queueLabel="Other pending approvals">
    {(current, onBusy, onSettled) => <ApprovalForm key={current.id} approval={current}
      onDecide={onDecide} onBusy={onBusy} onSettled={onSettled} />}
  </PendingInputDrawer>;
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
