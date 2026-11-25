import { useCallback, useMemo } from 'react';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { buildMemoryDistillPrompt, memoryDistillOperation, parseMemoryDistillCommand } from '../../../../core/domain/memory.ts';
import { ChatFooterNotice, ChatFooterRemedy } from '../../features/chat/thread/public.tsx';
import type { CommandBinding } from '../../features/chat/thread/composer-command.ts';
import { MemorySaveButton } from '../../features/chat/thread/session-header.tsx';
import { useState } from '../../ui/state/public.ts';
import { mintIdempotencyKey } from '../providers/idempotency-key.ts';
import { runOperation } from '../providers/query-operation.ts';
import { useGo } from './navigation.ts';

type MemoryDistillSave =
  | { trackId: string; kind: 'pending' }
  | { trackId: string; kind: 'sent'; areaId: string | null }
  | { trackId: string; kind: 'failed'; key: string };

/** Owns `/mem` command dispatch, the header Save-to-Memory shortcut, and closed-Track distillation status. */
export function useMemorySaveControl({
  transport,
  unauthorized,
  trackId,
  eligible,
  hasUserArea,
  trackClosed,
  sendBlocked,
  draftOnly,
  openConversationId,
  submitMessage,
}: Readonly<{
  transport: ApiTransportPort;
  unauthorized: UnauthorizedChannel;
  trackId: string;
  eligible: boolean;
  hasUserArea: boolean;
  trackClosed: boolean;
  sendBlocked: boolean;
  draftOnly: boolean;
  openConversationId: string | null;
  submitMessage: (cardId: string, prompt: string, attachments: readonly never[], fromComposer: boolean) => Promise<void> | null;
}>): Readonly<{
  commandBinding: CommandBinding | undefined;
  headerButton: React.ReactNode;
  notice: React.ReactNode;
}> {
  const go = useGo();
  const [distillSave, setDistillSave] = useState<MemoryDistillSave | null>(null);
  const activeDistill = distillSave?.trackId === trackId ? distillSave : null;

  const sendMemoryPrompt = useCallback((argument: string, fromComposer: boolean) => {
    if (openConversationId === null) return false;
    const { scope, focus } = parseMemoryDistillCommand(argument);
    const prompt = buildMemoryDistillPrompt({ scope, focus, hasUserArea });
    return submitMessage(openConversationId, prompt, [], fromComposer) !== null;
  }, [openConversationId, hasUserArea, submitMessage]);

  const saveClosedToMemory = async (key = mintIdempotencyKey()) => {
    if (activeDistill?.kind === 'pending') return;
    setDistillSave({ trackId, kind: 'pending' });
    try {
      const { areaId } = await runOperation(transport, memoryDistillOperation(trackId, undefined, undefined, key), unauthorized);
      setDistillSave({ trackId, kind: 'sent', areaId });
    } catch {
      setDistillSave({ trackId, kind: 'failed', key });
    }
  };

  const commandUnavailable = trackClosed
    ? 'Use Save to memory in the conversation header on closed Tracks.'
    : sendBlocked || draftOnly ? 'Sending is unavailable in this conversation.' : undefined;

  const commandBinding = useMemo<CommandBinding | undefined>(() => !eligible ? undefined : {
    execute: (argument: string) => sendMemoryPrompt(argument, true),
    unavailableReason: commandUnavailable,
  }, [eligible, commandUnavailable, sendMemoryPrompt]);

  const headerButton = openConversationId === null || !eligible ? undefined : (
    <MemorySaveButton
      pending={trackClosed && activeDistill?.kind === 'pending'}
      disabled={!trackClosed && (sendBlocked || draftOnly)}
      tooltip={trackClosed ? 'Save to memory' : 'Save to memory (/mem)'}
      onSave={() => {
        if (trackClosed) void saveClosedToMemory();
        else sendMemoryPrompt('', false);
      }}
    />
  );

  const notice = activeDistill?.kind === 'sent' ? (
    <ChatFooterNotice tone="neutral">
      <span>{activeDistill.areaId === null ? 'Sent to Global Memory Planner' : 'Sent to Area Memory Planner'}</span>
      <ChatFooterRemedy onClick={() => go(activeDistill.areaId === null
        ? { name: 'memory' } : { name: 'area-memory', areaId: activeDistill.areaId })}>
        Open memory
      </ChatFooterRemedy>
      <ChatFooterRemedy onClick={() => setDistillSave(null)}>Dismiss</ChatFooterRemedy>
    </ChatFooterNotice>
  ) : activeDistill?.kind === 'failed' ? (
    <ChatFooterNotice tone="error">
      <span>Could not confirm the memory request</span>
      <ChatFooterRemedy onClick={() => { void saveClosedToMemory(activeDistill.key); }}>Retry</ChatFooterRemedy>
      <ChatFooterRemedy onClick={() => setDistillSave(null)}>Dismiss</ChatFooterRemedy>
    </ChatFooterNotice>
  ) : null;

  return { commandBinding, headerButton, notice };
}
