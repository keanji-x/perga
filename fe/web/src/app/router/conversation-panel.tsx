import { priorityConversation } from '../../../../core/domain/activity.ts';
import { sideConversationSnapshot } from '../../../../core/domain/side-conversation.ts';
import { useConversationPaneTarget } from '../conversations/pane-lifecycle.ts';
import { queryKeys } from '../providers/query-keys.ts';
import { TerminalCompanion } from './terminal-companion.tsx';

import { useQuery } from '@tanstack/react-query';
import { useEffect,useMemo,useRef } from 'react';

import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import {
FOLLOW_INSTALLATION_DEFAULT
} from '../../../../core/domain/conversation.ts';
import { useState } from '../../ui/state/public.ts';
import { useCompactViewport } from '../../ui/viewport/public.ts';
import {
useConversationCommands,
useConversationDraft
} from '../conversations/public.tsx';
import { mintIdempotencyKey } from '../providers/idempotency-key.ts';
import { runOperation } from '../providers/query-operation.ts';
import { useReadReceipts } from '../providers/read-receipts.tsx';
import { serverVersionOperation } from '../providers/server-queries.ts';

import { useConversationPane } from './conversation-pane.tsx';
import type { ConversationPanelOptions,ConversationPanelSource } from './conversation-panel-contract.ts';
/** Two independent cards share the existing creation/recovery path and registry. */
export function useConversationPanel(
  transport: ApiTransportPort, unauthorized: UnauthorizedChannel, source: ConversationPanelSource,
  options: ConversationPanelOptions,
) {
  const registry = useConversationCommands();
  const compact = useCompactViewport();
  const [sideError, setSideError] = useState<string | null>(null);
  const savedTarget = useConversationPaneTarget(source.scopeId, source.rows);
  // The declared default owns resources even when its surface is absent. Keep
  // that fallback out of preferences: only an explicit selection opens a Sheet.
  const declaredDefault = options.defaultRowId === null ? null : { kind: 'row' as const, id: options.defaultRowId };
  const logicalTarget = savedTarget[0] ?? declaredDefault;
  const surfaceTarget = savedTarget[0] ?? (options.embedded?.active ? logicalTarget : null);
  const mainTarget = [surfaceTarget, savedTarget[1]] as const;
  const parentId = logicalTarget?.kind === 'row' ? logicalTarget.id : null;
  const lastMainRow = useRef<string | null>(null);
  const selectedRowId = savedTarget[0]?.kind === 'row' ? savedTarget[0].id : null;
  useEffect(() => { if (selectedRowId !== null) lastMainRow.current = selectedRowId; }, [selectedRowId]);
  const receipts = useReadReceipts();
  const preferredConversation = priorityConversation(source.rows, source.cards,
    row => receipts.isUnread('conversation', row.id, row.lastTurnCompletedAt ?? null));
  const dockOwnerId = compact && options.reportComposer !== undefined
    ? selectedRowId ?? source.rows.find(row => row.id === lastMainRow.current)?.id
      ?? preferredConversation?.id ?? source.planner?.cardId ?? source.rows[0]?.id ?? null
    : parentId;
  const mainReportComposer = options.reportComposer === undefined ? undefined : { ...options.reportComposer, conversationId: dockOwnerId };
  const capabilities = useQuery({ queryKey: queryKeys.serverVersion(),
    queryFn: () => runOperation(transport, serverVersionOperation(), unauthorized), enabled: parentId !== null && !compact, retry: false });
  const sideSlot = `${source.scopeId}:side:${parentId ?? ''}`;
  const sideTarget = useConversationPaneTarget(sideSlot, source.rows);
  const sideDraft = useConversationDraft(sideSlot);
  const childId = sideTarget[0]?.kind === 'row' ? sideTarget[0].id : null;
  const child = source.rows.find((row) => row.id === childId);
  const sideOwnerMatches = parentId !== null && (sideTarget[0]?.kind === 'draft'
    ? sideDraft?.side?.source_card_id === parentId : child?.sourceCardId === parentId);
  // Selection defines the side parent, but the compact dock can still read a
  // different resource (including with no main selection or a main draft).
  // Every pane's list writes must yield to all of these resource owners.
  const ownedCardIds = useMemo(() => [parentId, dockOwnerId, sideOwnerMatches ? childId : null]
    .filter((id): id is string => id !== null), [parentId, dockOwnerId, sideOwnerMatches, childId]);
  // The companion is a sibling of the main surface. Its presentation follows
  // its own desktop/parent/terminal host, independently of the main subview.
  const companionVisible = !compact;
  const terminalVisible = companionVisible && options.terminal?.workbench.open === true;
  const sideAvailable = sideOwnerMatches && !compact;
  const sidePresented = sideAvailable && !terminalVisible;
  const side = useConversationPane(transport, unauthorized, source, sideTarget,
    { ...options, embedded: undefined, reportComposer: undefined, ownedCardIds, onTerminal: options.terminal?.workbench.reveal, inline: true, slotId: sideSlot, enabled: sideAvailable, presented: sidePresented });
  const main = useConversationPane(transport, unauthorized, source, mainTarget,
    { ...options, reportComposer: mainReportComposer, ownedCardIds, sideError, stacked: true, showSideCommand: !compact, retainCompanion: options.terminal !== undefined, onTerminal: options.terminal?.workbench.reveal,
      companionVisible: terminalVisible || (sidePresented && side.isOpen),
      companion: options.terminal !== undefined
        ? (group) => <TerminalCompanion workbench={options.terminal!.workbench} visible={companionVisible}
            sideOpen={sideAvailable && side.isOpen} side={side.drawerFor(group)} terminal={options.terminal!.render()} />
        : sideAvailable && side.isOpen ? side.drawerFor : undefined,
      onSide: (parent, entries, question) => {
        if (compact) { setSideError('Side conversations are available on desktop.'); return false; }
        if (capabilities.data?.conversationSide !== true) {
          setSideError('Side conversations require a server that reports support. Update or reconnect, then try again.');
          return false;
        }
        setSideError(null);
        options.terminal?.workbench.suspend();
        if (sideDraft !== null && sideDraft.submitted !== null) {
          sideTarget[1]({ kind: 'draft' });
          if (question !== '') { setSideError('This side draft still has an unsettled delivery. Continue or retry it before starting another.'); return false; }
          return;
        }
        if (question === '') {
          const saved = source.rows.find((row) => row.sourceCardId === parent.id);
          if (saved !== undefined) { sideTarget[1]({ kind: 'row', id: saved.id }); return; }
        }
        registry.startDraft({ scopeId: sideSlot, key: mintIdempotencyKey(),
          provider: parent.provider, side: sideConversationSnapshot(parent.id, entries), model: FOLLOW_INSTALLATION_DEFAULT,
          text: question === '' ? null : question, autoSend: question !== '',
          submitted: null, creating: false, error: null, remedy: null });
        sideTarget[1]({ kind: 'draft' });
      },
    });
  return main;
}
