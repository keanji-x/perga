import type { CardEntry,CardRegistry,RegisteredCard } from '../registry.js';
import { FILE_VIEWER_CARD_ENTRY } from './file-viewer.js';
import { TERMINAL_CARD_ENTRY } from './terminal.js';
import { TRACK_REPORT_CARD_ENTRY } from './track-report.js';

export const BUILTIN_CARD_ORDER = Object.freeze([
  'terminal',
  'track-report',
  'file-viewer',
] as const);

export type BuiltinCardType = (typeof BUILTIN_CARD_ORDER)[number];

class BuiltinRegistrar {
  readonly #register: (target: CardRegistry) => void;

  private constructor(register: (target: CardRegistry) => void) {
    this.#register = register;
  }

  static of<Card extends RegisteredCard>(
    entry: CardEntry<Card> & { readonly headless: boolean },
  ): BuiltinRegistrar {
    return new BuiltinRegistrar((target) => { target.register(entry); });
  }

  run(target: CardRegistry): void {
    this.#register(target);
  }
}

type BuiltinRegistrarMap = Partial<Record<BuiltinCardType, BuiltinRegistrar>>;

type AssertTrue<Condition extends true> = Condition;

type BuiltinRegistrarSlot = NonNullable<BuiltinRegistrarMap[BuiltinCardType]>;

type SlotRejectsStructuralRegistrars =
  ((target: CardRegistry) => void) extends BuiltinRegistrarSlot
    ? false
    : { run(target: CardRegistry): void } extends BuiltinRegistrarSlot
      ? false
      : true;

export type StructuralRegistrarIsNotAssignable = AssertTrue<SlotRejectsStructuralRegistrars>;

export function registerAvailableBuiltinCards(registry: CardRegistry): void {
  const registrars: BuiltinRegistrarMap = {
    terminal: BuiltinRegistrar.of(TERMINAL_CARD_ENTRY),
    'track-report': BuiltinRegistrar.of(TRACK_REPORT_CARD_ENTRY),
    'file-viewer': BuiltinRegistrar.of(FILE_VIEWER_CARD_ENTRY),
  };
  for (const type of BUILTIN_CARD_ORDER) {
    registrars[type]?.run(registry);
  }
}
