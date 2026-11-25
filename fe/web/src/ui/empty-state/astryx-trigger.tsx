import type { ChatComposerTrigger } from '@astryxdesign/core/Chat';
import { EmptyState, type EmptyStateSchema } from './public.tsx';

/** Bind our shared compact EmptyState to Astryx's trigger-menu empty slot. */
export function withEmptyTrigger(trigger: Omit<ChatComposerTrigger, 'emptySearchText'>, content: EmptyStateSchema): ChatComposerTrigger {
  return {
    ...trigger,
    emptySearchText: <EmptyState {...content} variant="compact" />,
  };
}
