
import type { PanelItem } from '../../../../../core/domain/panel-item.ts';
import type { CardRegistry, RegisteredCard } from '../registry.js';

declare module '../registry.js' {
  interface CardEntry {
    /** Whether the card owns a visible surface. */
    readonly headless?: boolean;
  }
}

export type VisibleCardSlot = Readonly<{
  card: RegisteredCard;
  item: PanelItem;
  originalIndex: number;
}>;

export type UnknownCardSlot = Readonly<{
  item: PanelItem;
  originalIndex: number;
}>;

export type TrackCardPartition = Readonly<{
  visible: readonly VisibleCardSlot[];
  unknown: readonly UnknownCardSlot[];
}>;

export function partitionTrackCards(
  registry: CardRegistry,
  cards: readonly PanelItem[],
): TrackCardPartition {
  const visible: VisibleCardSlot[] = [];
  const unknown: UnknownCardSlot[] = [];
  cards.forEach((item, originalIndex) => {
    const card = registry.resolve(item);
    if (card === null) {
      if (item.kind !== 'track-report') unknown.push(Object.freeze({ item, originalIndex }));
      return;
    }
    if (registry.get(card.type)?.headless === true) return;
    visible.push(Object.freeze({ card, item, originalIndex }));
  });
  return Object.freeze({ visible: Object.freeze(visible), unknown: Object.freeze(unknown) });
}
