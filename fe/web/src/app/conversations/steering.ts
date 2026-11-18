import type { ApiTransportPort } from '../../../../core/api/types.ts';
import { useEffect, useMemo } from 'react';
import type { TranscriptEntry } from '../../../../core/domain/conversation.ts';
import { ApiError } from '../../../../core/domain/failure-class.ts';
import { useConversationCommands, useConversationSteeringMessages } from './public.tsx';
import { mintIdempotencyKey } from '../providers/idempotency-key.ts';
import { admitTransport } from '../providers/recovery-mutation.ts';

export function useSteeringMessages(cardId: string, transport: ApiTransportPort, entries: readonly TranscriptEntry[],
  send: (turnId: string, text: string, admitted: ApiTransportPort) => Promise<{
    inputId: string; delivery: 'accepted' | 'queued' | 'unconfirmed';
  }>) {
  const { editSteeringMessages } = useConversationCommands();
  const messages = useConversationSteeringMessages(cardId);
  const visible = useMemo(() => {
    if (!messages.some(message => message.queuedInputId !== undefined)) return messages;
    const ids = new Set(entries.map(entry => entry.id));
    return messages.filter(message => message.queuedInputId === undefined || !ids.has(`human:${message.queuedInputId}`));
  }, [entries, messages]);
  useEffect(() => {
    if (visible.length !== messages.length) {
      const retired = new Set(messages.filter(message => !visible.includes(message)).map(message => message.id));
      editSteeringMessages(cardId, current => current.filter(message => !retired.has(message.id)));
    }
  }, [cardId, messages, visible, editSteeringMessages]);
  return {
    messages: visible,
    send: (turnId: string, text: string): Promise<void> | null => {
      let admitted: ApiTransportPort;
      try { admitted = admitTransport(transport); } catch { return null; }
      const id = `steering-${mintIdempotencyKey()}`;
      editSteeringMessages(cardId, current => [...current, { id, text, status: 'Sending supplement…' }]);
      const settle = (status: string, queuedInputId?: string) => editSteeringMessages(cardId, current => current.map(message =>
        message.id === id ? { ...message, status, ...(queuedInputId === undefined ? {} : { queuedInputId }) } : message));
      return send(turnId, text, admitted).then(result => {
        settle(result.delivery === 'accepted' ? 'Delivered to current turn'
          : result.delivery === 'queued' ? 'Queued for a follow-up turn' : 'Delivery not confirmed', result.delivery === 'queued' ? result.inputId : undefined);
      }, (error: unknown) => {
        const refused = error instanceof ApiError && (error.failure.kind === 'unauthorized'
          || (error.failure.kind === 'http' && [400, 401, 403, 404, 409, 422].includes(error.failure.status)));
        settle(refused ? error.message : 'Delivery not confirmed');
      });
    },
  };
}
