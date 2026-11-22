import { type CommandBindings } from '../../features/chat/thread/composer-command.ts';
import { type TerminalWorkbench } from '../../features/track/workbench/public.tsx';
import type { ConversationCreationSource,PlannerConversationScope } from '../conversations/contracts.ts';

import { type ReactNode } from 'react';

import type { AgentProvider, AskAnswer } from '../../../../core/domain/presentation-types.ts';
import { type CardActivity } from '../../../../core/domain/activity.ts';
import { type PendingApproval } from '../../../../core/domain/approval.ts';
import { type OpenAsk } from '../../../../core/domain/ask.ts';
import {
type Conversation
} from '../../../../core/domain/conversation.ts';
import { type EmbeddedConversationPresentation } from './mobile-conversation-surface.tsx';

/** A route-owned server list whose rows open in this panel's drawer. */
export type ConversationPanelSource = ConversationCreationSource & Readonly<{
    /** The Track this draft belongs to. */
    scopeId: string;
    rows: readonly Conversation[];
    /** Default provider to seed new subagent drafts on this Track. */
    defaultProvider?: AgentProvider;
    /** Whether this surface runs against Perga v1 endpoints. */

    /** The kernel's per-card verdicts for the track these rows are on; required, because a caller that passes no overlay has a list on which nothing can ever be working. */
    cards: Readonly<Record<string, CardActivity>>;
    /** The Track these rows may be sent to. */
    rememberOn: string;
    /** Known on the Track route; Today resolves the open conversation's Track on demand. */
    workspaceRoot: string | null;
    scopeOf: (conversationId: string) => PlannerConversationScope | null;
    /** Live execution-scoped approvals for this Track, scoped to each owning sessionId by the pane. */
    pendingApprovals: readonly PendingApproval[];
    decideApproval: (approval: PendingApproval, optionId: string) => Promise<void>;
    /** The row and Area supplying report mention candidates; null without an Area context.
     * Candidate inserts are server-owned and the composer sends them verbatim.
     * asks/answerAsk serve durable open asks above the composer; role supplies the Area admin state. */
    planner: Readonly<{
      cardId: string; areaId: string; asks: readonly OpenAsk[];
      answerAsk: (askId: import('../../../../core/domain/activity.ts').AskId, answers: readonly AskAnswer[]) => Promise<void>;
      commands?: (stop: (() => void) | undefined) => CommandBindings;
    }> | null;
  }>;

/**
 * The conversation module shared by Today and Track. A draft is a third open state,
 * not a `Conversation`: the card is minted by the first message, so until one is
 * sent there is no card id and nothing to fetch.
 */
export type ConversationPanelOptions = Readonly<{
  defaultRowId: string | null;
  onPresent?: () => void;
  embedded?: EmbeddedConversationPresentation;
  presented?: boolean;
  onArchive?: (cardId: string, archived: boolean) => Promise<void>;
  terminal?: { workbench: TerminalWorkbench; render: () => ReactNode };
  showTrack?: boolean;
  resizable?: boolean;
  reportComposer?: Readonly<{ ready: boolean; returnLabel: string }>;
  workspace?: Readonly<{ waiting: boolean; notice?: ReactNode }>;
}>;
