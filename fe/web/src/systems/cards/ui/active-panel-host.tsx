import type { CardHost } from '../host.js';
import { useState } from '../../../ui/state/public.ts';
import { CardCell, type CardHostItem } from './card-cell.tsx';

/** Mount on first selection. Hide/switch retains DOM, controllers and PTYs.
 * Removal from items, host replacement or route disposal releases resources.
 * Callers exclude dock-owned cards so each resource has exactly one mount. */
export function ActivePanelHost({ host, items, activeCardId, visible, onRemoveCard }: {
  host: CardHost;
  items: readonly CardHostItem[];
  activeCardId: string | null;
  visible: boolean;
  onRemoveCard?: (cardId: string) => void;
}) {
  const [visited, setVisited] = useState<readonly string[]>([]);
  const retained = visited.filter(id => items.some(item => item.card.id === id));
  if (visible && activeCardId !== null && !retained.includes(activeCardId)
    && items.some(item => item.card.id === activeCardId)) retained.push(activeCardId);
  if (retained.length !== visited.length || retained.some((id, index) => id !== visited[index])) setVisited(retained);

  return <div className="track-grid-wrap" data-nc-active-panel-host="">
    {items.filter(item => retained.includes(item.card.id)).map(item => {
      const active = visible && item.card.id === activeCardId;
      return <div key={item.card.id} className="track-card"
        hidden={!active} inert={!active} aria-hidden={!active || undefined}
        data-nc-card-cell="" data-nc-card-id={item.card.id}>
        <CardCell host={host} item={item} focused={active} visible={active}
          onRemove={onRemoveCard === undefined || item.deletable === false ? undefined : () => onRemoveCard(item.card.id)} />
      </div>;
    })}
  </div>;
}
