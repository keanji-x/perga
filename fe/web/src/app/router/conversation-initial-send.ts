import { useEffect, useRef } from 'react';
import type { Conversation } from '../../../../core/domain/conversation.ts';
import { usePendingTrackInitialSend, type useConversationCommands } from '../conversations/public.tsx';
import type { useConversationStore } from '../conversations/store.ts';

/** Redeem the persisted first Track message once, then focus only its ready composer. */
export function useConversationInitialSend({ primaryPlanner, scopeId, plannerCardId, open, store, registry, focusComposer }: {
  primaryPlanner: boolean;
  scopeId: string;
  plannerCardId: string | undefined;
  open: Conversation | null;
  store: ReturnType<typeof useConversationStore>;
  registry: ReturnType<typeof useConversationCommands>;
  focusComposer: (id: string) => void;
}) {
  const pendingTrackInitialSend = usePendingTrackInitialSend(
    primaryPlanner ? scopeId : null,
  );
  const dispatchedInitialSendRef = useRef<string | null>(null);
  const pendingInitialFocusRef = useRef<string | null>(null);
  useEffect(() => {
    if (pendingTrackInitialSend === undefined || open === null || open.id !== plannerCardId) return;
    if (dispatchedInitialSendRef.current === scopeId) return;
    const sent = store.send(open.id, pendingTrackInitialSend.text, pendingTrackInitialSend.attachments ?? [], false, pendingTrackInitialSend.key);
    if (sent === null) return;
    dispatchedInitialSendRef.current = scopeId;
    pendingInitialFocusRef.current = open.id;
    void sent.then(() => {
      registry.consumeTrackInitialSend(scopeId);
    });
  }, [pendingTrackInitialSend, open, plannerCardId, scopeId, store, registry]);
  useEffect(() => {
    if (open === null || pendingInitialFocusRef.current !== open.id) return;
    if (!store.sendBlocked && store.historyReady) {
      pendingInitialFocusRef.current = null;
      focusComposer(open.id);
    }
  }, [open, store.sendBlocked, store.historyReady, focusComposer]);

}
