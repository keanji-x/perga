import type { CardHost,CardRegistry } from '../../systems/cards/public.ts';
/** The card runtime, created once at boot and injected. */
export type CardRuntime = Readonly<{ registry: CardRegistry; host: CardHost }>;
