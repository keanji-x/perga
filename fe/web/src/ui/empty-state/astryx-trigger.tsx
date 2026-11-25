import { isValidElement } from 'react';
import type { ChatComposerTrigger } from '@astryxdesign/core/Chat';
import { EmptyState, type EmptyStateSchema } from './public.tsx';

/** Bind our shared compact EmptyState to Astryx's trigger-menu empty slot. */
export function withEmptyTrigger(trigger: Omit<ChatComposerTrigger, 'emptySearchText'>, content: EmptyStateSchema): ChatComposerTrigger {
  return {
    ...trigger,
    emptySearchText: <EmptyState {...content} variant="compact" />,
  };
}

/** Ensure any trigger entering `ComposerInputField` carries `[data-nc-empty-state]`
 * on its empty slot so `triggerMenuKeyRoute` distinguishes settled empty menus from loading. */
export function withEmptyTriggerMarker(trigger: ChatComposerTrigger): ChatComposerTrigger {
  const empty = trigger.emptySearchText;
  if (empty === '') return trigger;
  if (isValidElement(empty) && (empty.type === EmptyState || (typeof empty.props === 'object' && empty.props !== null && 'data-nc-empty-state' in empty.props))) {
    return trigger;
  }
  return {
    ...trigger,
    emptySearchText: <span data-nc-empty-state="">{empty ?? 'No results'}</span>,
  };
}
