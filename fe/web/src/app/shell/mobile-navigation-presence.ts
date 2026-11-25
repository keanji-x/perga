import type { RefObject } from 'react';
import { useEdgePresence } from '../../ui/motion/edge-presence.ts';
import { readSurfaceFrame } from '../../ui/motion/geometry.ts';

/** Retain exit paint through Motion; interaction ends at the close commit. */
export function useMobileNavigationPresence(
  host: RefObject<HTMLDivElement | null>,
  frame: RefObject<HTMLDivElement | null>,
  onClosed: () => void,
  open: boolean,
  present: boolean,
): void {
  useEdgePresence({
    frame: { ref: frame },
    part: { element: host, readFrame: () => readSurfaceFrame(frame.current) },
    edge: 'inline-end',
    open,
    present,
    enabled: true,
    onExited: onClosed,
  });
}
