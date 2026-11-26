import type { ComponentProps } from 'react';
import type { PermissionModeOptionDto } from '../../../../core/api/perga-v1.ts';
import { conversationName, type Conversation, type ModelSelection } from '../../../../core/domain/conversation.ts';
import type { AgentProvider } from '../../../../core/domain/presentation-types.ts';
import type { ChatDockRoleSlot } from '../../features/chat/dock/public.tsx';
import { MobileSessionSettingsControl } from '../../features/chat/thread/mobile-session-settings-sheet.tsx';
import type { ModelGroup } from '../../features/chat/thread/model-selection.ts';
import { PlannerAttachButton } from '../../features/planner/attachments.tsx';

export function mobileConversationSubtitle(
  open: Conversation | null,
  draftOpen: boolean,
  scopeTitle: string | undefined,
): string | undefined {
  if (open === null) return draftOpen ? 'New conversation' : undefined;
  if (open.isLead === true) return 'Lead';
  return scopeTitle !== undefined ? `Collaborator · ${scopeTitle}` : 'Collaborator';
}

export function mobileConversationRoleSlot(
  enabled: boolean,
  activeRow: Conversation | null,
  onOpen: () => void,
): ChatDockRoleSlot | undefined {
  if (!enabled) return undefined;
  const label = activeRow === null || activeRow.isLead === true ? 'Lead' : conversationName(activeRow);
  return {
    label,
    ariaLabel: `Switch conversation, current: ${label}`,
    onOpen,
  };
}

export function MobileConversationMetaBar({
  sessionKey,
  groups,
  provider,
  selection,
  onChangeModel,
  modelDisabled,
  appliesNextTurn,
  permission,
  attachments,
  attachmentsAvailable,
  attachDisabled,
}: Readonly<{
  sessionKey: string;
  groups: readonly ModelGroup<AgentProvider>[];
  provider: AgentProvider;
  selection: ModelSelection;
  onChangeModel: (next: ModelSelection, provider: AgentProvider) => void;
  modelDisabled: boolean;
  appliesNextTurn: boolean;
  permission?: Readonly<{
    mode: string;
    options: readonly PermissionModeOptionDto[];
    onChange: (mode: string) => void;
    disabled: boolean;
  }>;
  attachments: ComponentProps<typeof PlannerAttachButton>['attachments'];
  attachmentsAvailable: boolean;
  attachDisabled: boolean;
}>) {
  return (
    <>
      <MobileSessionSettingsControl
        key={`mobile-model-${sessionKey}`}
        groups={groups}
        provider={provider}
        selection={selection}
        onChangeModel={onChangeModel}
        modelDisabled={modelDisabled}
        appliesNextTurn={appliesNextTurn}
        permission={permission}
      />
      <PlannerAttachButton
        attachments={attachments}
        available={attachmentsAvailable}
        disabled={attachDisabled}
      />
    </>
  );
}
