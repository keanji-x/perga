import { useCallback, useMemo, useRef } from 'react';
import { onlineManager } from '@tanstack/react-query';

import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { PlannerAttachment } from '../../../../core/api/generated/wire.ts';
import {
  type SentPlannerInput, type TranscriptEntry
} from '../../../../core/domain/conversation.ts';
import { KeyedSendFailure, retryUnknownSend, SEND_FAILURES, SEND_RETRIES } from '../../../../core/domain/conversation-delivery.ts';
import {
  outboxFacts,
  queueDisplayAccounting,
  settleSendOp, wasUnknown,
  withOutboxLiveReplies,
  type ConversationSendTarget,
  type QueueDisplayAccounting,
  type SendOp,
} from '../../../../core/domain/conversation-outbox.ts';
import { recoveryDelay } from '../../../../core/domain/recovery/access.ts';
import { ApiError, classifyFailure, NotSentError } from '../../../../core/domain/failure-class.ts';
import { admitTransport } from '../providers/recovery-mutation.ts';
import { mintIdempotencyKey } from '../providers/idempotency-key.ts';

import { useConversationCommands, useConversationSendOps } from './public.tsx';

/**
 * The open conversation's keyed sends (#2043): the press, the retries under one key, Try again, Edit
 * and Dismiss of a failed one, and its view of the thread. The ops live in the registry's outbox for
 * that conversation, so a send settles there whichever conversation is shown, and leaves it only when
 * a receipt arrives or the reader drops a failure.
 */
export function useConversationOutbox({
  cardId, transport, send, serverEntries, liveReplies, stalled,
  queuesInput, queueIfBusy, pressed, target,
}: {
  cardId: string;
  transport: ApiTransportPort;
  /** Mark the receipt before starting the reads that reconcile it. */
  send: (text: string, attachments: readonly string[], key: string, admitted: ApiTransportPort,
    queueIfBusy: boolean, target?: ConversationSendTarget) => Promise<SentPlannerInput>;
  target?: ConversationSendTarget;
  serverEntries: readonly TranscriptEntry[];
  liveReplies: readonly TranscriptEntry[];
  stalled: boolean;
  /** Whether the kernel queues a message posted now (`kernelQueuesInput`), read at the press. */
  queuesInput: boolean;
  queueIfBusy: boolean;
  /** A send of the conversation shown went out. */
  pressed: () => void;
}) {
  const registry = useConversationCommands();
  const { editOutbox, beginSend } = registry;
  const ops = useConversationSendOps(cardId === '' ? null : cardId);
  /** The card shown now; a send's own `cardId` is the one it was pressed in. */
  const shownCardId = useRef(cardId);
  shownCardId.current = cardId;
  const facts = useMemo(() => outboxFacts({ serverEntries, stalled, ops }), [serverEntries, stalled, ops]);
  const accountQueueDisplay = useCallback((entries: readonly TranscriptEntry[], suppressed: QueueDisplayAccounting['suppressed']) =>
    queueDisplayAccounting(entries, suppressed), []);
  const view = useMemo(() => withOutboxLiveReplies(facts, liveReplies), [facts, liveReplies]);

  /** Words and images back in the composer of the card they were sent from, whichever conversation is shown: the one
   * way a send gives its words back (#2068). */
  const refill = (sentTo: string, text: string, attachments: readonly PlannerAttachment[]) => {
    registry.refillComposer(sentTo, { text, attachments });
  };

  /**
   * A composer send's images leave the composer of the card they were sent from, whichever conversation is shown by
   * now: on delivery, and on Dismiss, since a send that may have been stored may have bound them.
   */
  const releaseComposerImages = (sentTo: string, attachments: readonly PlannerAttachment[]) => {
    const sentIds = new Set(attachments.map((attachment) => attachment.id));
    registry.editComposer(sentTo, (current) => current.attachments.some((image) => sentIds.has(image.id))
      ? { ...current, attachments: current.attachments.filter((image) => !sentIds.has(image.id)) } : current);
    registry.editUpload(sentTo, (current) => current.refusal === null ? current : { ...current, refusal: null });
  };

  /** One run of a begun op: its attempts under one key, settled into the outbox of the card it was pressed in. */
  const run = (op: SendOp & { phase: 'sending' }, admittedAtPress: ApiTransportPort): Promise<void> => {
    const sentTo = cardId;
    const { key, echo, fromComposer, target } = op;
    const attachments: readonly PlannerAttachment[] = echo.attachments ?? [];
    const settle = (next: SendOp | null) => editOutbox(sentTo, (current) => settleSendOp(current, key, next));
    /* An unknown answer is sent again under the same key, so the server queues the message at most once. Each
       retry is admitted again; one that cannot go out counts as an attempt. */
    const admitRetry = (): ApiTransportPort | null => { try { return admitTransport(transport); } catch { return null; } };
    const deliver = () => retryUnknownSend(
      (attempt) => {
        const admitted = attempt === 0 ? admittedAtPress : admitRetry();
        return admitted === null ? Promise.reject(new NotSentError())
          : send(echo.text, attachments.map((attachment) => attachment.id), key, admitted, queueIfBusy, target);
      },
      (error) => (error instanceof ApiError ? error.failure : null),
      (retry) => new Promise<void>((resolve) => { setTimeout(resolve, recoveryDelay(retry, Math.random())); }),
      op.unknown, (target?.queue ?? queueIfBusy) ? 0 : SEND_RETRIES,
      // The task admission check rejects a conflict before writing the message. After a lost
      // response the shared retry logic still keeps uncertainty until it receives a receipt.
      failure => target !== undefined && failure?.kind === 'http' && failure.status === 409 && failure.code === 'conflict'
        ? 'refused' : classifyFailure(failure, SEND_FAILURES),
    );
    const prerequisite = registry.permissionWriteOf(sentTo);
    const delivery = prerequisite === undefined ? deliver() : prerequisite.then(deliver,
      (error: unknown) => { throw new KeyedSendFailure(error, op.unknown ? 'unknown' : 'refused'); });
    return delivery.then(() => {
      if (fromComposer) releaseComposerImages(sentTo, attachments);
      settle(null);
    }, (error: unknown) => {
      const failed = error instanceof KeyedSendFailure ? error : new KeyedSendFailure(error, 'unknown');
      const message = failed.cause instanceof Error && failed.cause.message !== '' ? failed.cause.message : 'Could not send the message.';
      /* `message` is shown only for an op that was not sent: an unknown op's footer says no more than that it is unconfirmed. */
      settle({ key, target, echo, fromComposer, phase: 'failed', delivery: failed.delivery, message });
    });
  };

  /** Take the failed op under `key` out of the outbox if `may` lets it go; returns it, or `null` when it did not leave. */
  const discard = (key: string, may: (op: SendOp) => boolean): SendOp | null => {
    let taken: SendOp | null = null;
    editOutbox(cardId, (current) => {
      taken = current.find((op): op is SendOp => op.key === key && op.phase === 'failed' && may(op)) ?? null;
      return taken === null ? current : settleSendOp(current, key, null);
    });
    return taken;
  };

  return {
    view, accountQueueDisplay,
    /**
     * A press: a new op under a new key, settled when it is answered; `null` when the conversation cannot take it now
     * and nothing was sent, so its words stay where they were typed. `attachments` are ids already uploaded; naming one
     * here makes it permanent. `fromComposer`: they are the composer's own, so a delivery clears them (and the upload
     * refusal) there.
     */
    send: (conversationId: string, text: string, attachments: readonly PlannerAttachment[], fromComposer: boolean,
      idempotencyKey?: string): Promise<void> | null => {
      if (conversationId !== cardId || stalled || view.failed !== null) return null;
      /* Admitted at the press. Where the transport carries a recovery admission (the bundled build), a press that
         cannot leave the browser is refused here and sends nothing: the composer keeps its words, and the global
         recovery status already says why. The web build admits every press, and a send that cannot leave fails as a
         transport error — unknown, retried. */
      let admitted: ApiTransportPort;
      try { admitted = admitTransport(transport); } catch { return null; }
      const op = {
        key: idempotencyKey ?? mintIdempotencyKey(), target, fromComposer, phase: 'sending', unknown: false,
        echo: {
          id: `echo-${mintIdempotencyKey()}`, author: 'you', text, atMs: Date.now(),
          /* The echo carries the images: an image-only message has no text to match on, so the ids are the second criterion. */
          attachments,
          localSend: true,
          /* Read at the press from the last `GET /planner/run` snapshot, against the kernel's whitelist, and never
             recomputed from the live phase later. */
          queued: queuesInput,

          ...((target?.queue ?? queueIfBusy) ? { waitForIdentity: true } : {}),
        },
      } as const;
      if (!beginSend(cardId, op)) return null;
      if (shownCardId.current === cardId) pressed();
      return run(op, admitted);
    },
    /**
     * Try again resumes the failed op, it does not press again: the echo (images included — an image-only message
     * re-sent as `{ text: "" }` is refused, and its ids are still bound), the key and the delivery uncertainty
     * all carry over, so a first attempt that was stored after all is answered again and never queued twice.
     */
    retrySend: (key: string) => {
      const failed = view.failed;
      if (failed?.key !== key || stalled || !onlineManager.isOnline()) return false;
      let admitted: ApiTransportPort;
      /* Not admitted: the op keeps its standing and its footer unchanged; the global recovery status says why. */
      try { admitted = admitTransport(transport); } catch { return false; }
      const op = {
        key, target: failed.target, echo: failed.echo, fromComposer: failed.fromComposer, phase: 'sending',
        unknown: failed.delivery === 'unknown',
      } as const;
      if (!beginSend(cardId, op)) return false;
      pressed();
      void run(op, admitted);
      return true;
    },
    /**
     * Edit of the failed op: it leaves the outbox and its words and images go back to the composer. Only for an op that
     * was not stored: an unknown one may have been, and an edited message would be a second send under a new key.
     */
    discardFailedSend: (key: string) => {
      const taken = discard(key, (op) => !wasUnknown(op));
      if (taken !== null) refill(cardId, taken.echo.text, taken.echo.attachments ?? []);
    },
    /**
     * Dismiss of the failed op: it leaves the outbox and nothing of it comes back, since an unknown send may already be
     * delivered (a read then shows it). Its composer images leave the composer too, as a delivery takes them: they may
     * be bound to the stored message, and the next message must not send them again.
     */
    dismissFailedSend: (key: string) => {
      const dismissed = discard(key, () => true);
      if (dismissed?.fromComposer === true) releaseComposerImages(cardId, dismissed.echo.attachments ?? []);
    },
  };
}
