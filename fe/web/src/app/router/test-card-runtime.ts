// A booted card runtime for router tests: the real registry with the real built-ins.

import { createCardHost, createCardRegistry } from '../../systems/cards/public.js';
import { bootCards } from '../cards.ts';
import type { CardRuntime } from './card-runtime.ts';

export function bootTestCardRuntime(): CardRuntime {
  const registry = createCardRegistry();
  bootCards(registry);
  return { registry, host: createCardHost(registry) };
}
