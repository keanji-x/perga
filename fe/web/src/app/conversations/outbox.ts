import { useCallback, useEffect, useMemo, useRef } from 'react';
import { onlineManager } from '@tanstack/react-query';

import type { MultipartFile } from '../../../../core/api/multipart.ts';
import { type SentPlannerInput } from '../../../../core/domain/conversation-commands.ts';
import { type ConversationAttachment, type PendingQueueEntry, type TranscriptEntry } from '../../../../core/domain/conversation.ts';
import { KeyedSendFailure, retryUnknownSend, sendRetryDelay, sendFailureCode, sendFailureMessage, SEND_FAILURES, SEND_RETRIES } from '../../../../core/domain/conversation-delivery.ts';
import {
  outboxFacts,
  queueDisplayAccounting,
  settleSendOp, wasUnknown,
  withOutboxLiveReplies,
  type ConversationSendTarget,
  type QueueDisplayAccounting,
  type SendOp,
} from '../../../../core/domain/conversation-outbox.ts';
import { ApiError, classifyFailure, NotSentError } from '../../../../core/domain/failure-class.ts';
import { mintIdempotencyKey } from '../providers/idempotency-key.ts';

import { useConversationCommands, useConversationSendOps } from './public.tsx';

/**
 * The open conversation's keyed sends (#2043): the press, the retries under one key, Try again, Edit
 * and Dismiss of a failed one, and its view of the thread. The ops live in the registry's outbox for
 * that conversation, so a send settles there whichever conversation is shown, and leaves it only when
 * a matching history or queue item takes over its preview, or the reader drops a failure.
 */
export function useConversationOutbox({
  cardId, send, serverEntries, liveReplies, stalled,
  queuesInput, queueIfBusy, pressed, target, pendingQueue,
}: {
  cardId: string;
  /** Mark the receipt before starting the reads that reconcile it. */
  send: (text: string, attachments: readonly MultipartFile[], key: string,
    queueIfBusy: boolean, target?: ConversationSendTarget) => Promise<SentPlannerInput>;
  target?: ConversationSendTarget;
  serverEntries: readonly TranscriptEntry[];
  liveReplies: readonly TranscriptEntry[];
  pendingQueue?: readonly PendingQueueEntry[];
  stalled: boolean;
  /** Whether the kernel queues a message posted now (`kernelQueuesInput`), read at the press. */
  queuesInput: boolean;
  queueIfBusy: boolean;
  /** A send of the conversation shown went out. */
  pressed: () => void;
}) {
  const registry = useConversationCommands();
  const { editOutbox, beginSend, removeSend } = registry;
  const ops = useConversationSendOps(cardId === '' ? null : cardId);
  /** The card shown now; a send's own `cardId` is the one it was pressed in. */
  const shownCardId = useRef(cardId);
  shownCardId.current = cardId;
  const facts = useMemo(() => outboxFacts({ serverEntries, stalled, ops, queue: pendingQueue }), [serverEntries, stalled, ops, pendingQueue]);
  const accountQueueDisplay = useCallback((entries: readonly TranscriptEntry[], suppressed: QueueDisplayAccounting['suppressed']) =>
    queueDisplayAccounting(entries, suppressed), []);
  const view = useMemo(() => withOutboxLiveReplies(facts, liveReplies), [facts, liveReplies]);

  useEffect(() => {
    for (const op of facts.observed) removeSend(cardId, op.key);
  }, [cardId, facts.observed, removeSend]);

  /** One run of a begun op: its attempts under one key, settled into the outbox of the card it was pressed in. */
  const run = (op: SendOp & { phase: 'sending' }): Promise<void> => {
    const sentTo = cardId;
    const { key, echo, target } = op;
    const generation = registry.sendGenerationOf(sentTo);
    const current = () => registry.sendGenerationOf(sentTo) === generation && registry.outboxOf(sentTo).some(held => held.key === key);
    const requireCurrent = () => { if (!current()) throw new KeyedSendFailure(new NotSentError(), 'refused'); };
    const attachments: readonly ConversationAttachment[] = echo.attachments ?? [];
    const settle = (next: SendOp | null) => editOutbox(sentTo, (current) => settleSendOp(current, key, next));
    /* An unknown answer is sent again under the same key, so the server queues the message at most once. */
    const deliver = async () => {
      requireCurrent();
      const files = attachments.every(image => image.file !== undefined)
        ? attachments.map(image => image.file!)
        : await Promise.all(attachments.map(async image => image.file ?? readSavedImage(image))).catch(() => {
          throw new KeyedSendFailure(new Error('The saved image could not be read.'), op.unknown ? 'unknown' : 'refused');
        });
      return retryUnknownSend(
        () => {
          requireCurrent();
          return send(echo.text, files, key, queueIfBusy, target);
        },
        (error) => (error instanceof ApiError ? error.failure : null),
        (retry) => new Promise<void>((resolve) => { setTimeout(resolve, sendRetryDelay(retry, Math.random())); }),
        op.unknown, (target?.queue ?? queueIfBusy) ? 0 : SEND_RETRIES,
        // The session epoch and task admission checks reject a conflict before writing the message. After a lost
        // response the shared retry logic still keeps uncertainty until it receives a receipt.
        failure => target !== undefined && failure?.kind === 'http' && failure.status === 409 && failure.code === 'conflict'
          ? 'refused' : classifyFailure(failure, SEND_FAILURES),
      );
    };
    const prerequisite = registry.permissionWriteOf(sentTo);
    const delivery = prerequisite === undefined ? deliver() : prerequisite.then(deliver,
      (error: unknown) => { throw new KeyedSendFailure(error, op.unknown ? 'unknown' : 'refused'); });
    return delivery.then(answer => {
      if (!current()) return;
      if (answer.sent.turnId !== undefined) registry.rememberAcceptedTurn(sentTo, answer.sent.turnId);
      settle(attachments.length === 0 || answer.sent.entry_id === null ? null
        : { ...op, phase: 'accepted', messageId: answer.sent.entry_id, queued: answer.sent.queued === true });
    }, (error: unknown) => {
      if (!current()) return;
      const failed = error instanceof KeyedSendFailure ? error : new KeyedSendFailure(error, 'unknown');
      const message = sendFailureMessage(failed.cause);
      /* `message` is shown only for an op that was not sent: an unknown op's footer says no more than that it is unconfirmed. */
      settle({ ...op, phase: 'failed', delivery: failed.delivery, message, code: sendFailureCode(failed.cause) });
    });
  };

  return {
    view, accountQueueDisplay, unresolved: facts.unresolved,
    /** An admitted send moves its draft images into the outbox under one key. */
    send: (conversationId: string, text: string, attachments: readonly ConversationAttachment[], fromComposer: boolean,
      idempotencyKey?: string): Promise<void> | null => {
      if (conversationId !== cardId || stalled || view.failed !== null) return null;
      const op = {
        key: idempotencyKey ?? mintIdempotencyKey(), target, fromComposer, phase: 'sending', unknown: false,
        echo: {
          id: `echo-${mintIdempotencyKey()}`, author: 'you', text, atMs: Date.now(),
          attachments,
          localSend: true,
          /* Read at the press from the latest session snapshot and never
             recomputed from the live phase later. */
          queued: queuesInput,

          ...((target?.queue ?? queueIfBusy) ? { waitForIdentity: true } : {}),
        },
      } as const;
      if (!beginSend(cardId, op)) return null;
      if (shownCardId.current === cardId) pressed();
      return run(op);
    },
    /** Retry the same body under the original key, including image-only sends. */
    retrySend: (key: string) => {
      const failed = view.failed;
      if (failed?.key !== key || stalled || !onlineManager.isOnline()) return false;
      const op = {
        key, target: failed.target, echo: failed.echo, fromComposer: failed.fromComposer, phase: 'sending',
        unknown: failed.delivery === 'unknown',
      } as const;
      if (!beginSend(cardId, op)) return false;
      pressed();
      void run(op);
      return true;
    },
    discardFailedSend: (key: string) => {
      const failed = view.failed;
      if (failed?.key === key && !wasUnknown(failed)) {
        removeSend(cardId, key, { text: failed.echo.text, attachments: failed.echo.attachments ?? [] });
      }
    },
    dismissFailedSend: (key: string) => {
      if (view.failed?.key === key) removeSend(cardId, key);
    },
  };
}

async function readSavedImage(image: ConversationAttachment): Promise<File> {
  const response = await fetch(image.url, { credentials: 'same-origin', redirect: 'error' });
  if (!response.ok) throw new Error('The saved image is unavailable.');
  return new File([await response.blob()], image.id, { type: image.contentType });
}
