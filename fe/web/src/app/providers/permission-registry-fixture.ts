import { useState } from '../../ui/state/public.ts';
import { createConversationOwner } from '../conversations/registry-owner.ts';

/** A test's tab owner, retained while its mutation hook rerenders. */
export function useTestConversationRegistry() {
  const [owner] = useState(() => createConversationOwner());
  return owner.commands;
}
