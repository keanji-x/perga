import { VisuallyHidden } from '@astryxdesign/core/VisuallyHidden';
import { providerPresentation } from '../../../../../core/domain/provider-presentation.ts';
import { Icon } from '../../../ui/icon/public.tsx';

/** Runtime identity remains in the projection; presentation alone chooses a known provider glyph. */
export function InventoryIcon({ kind }: { kind: string }) {
  return <Icon name={kind === 'terminal' ? 'terminal' : kind === 'agent' ? 'agent' : providerPresentation(kind).icon ?? 'file'} size="sm" />;
}
/** Preserve the exact projection text while exposing its clean display label to assistive technology. */
export function InventoryKind({ kind }: { kind: string }) {
  return <span role="img" aria-label={providerPresentation(kind).label}>
    <InventoryIcon kind={kind} /><VisuallyHidden aria-hidden="true">{kind}</VisuallyHidden>
  </span>;
}
