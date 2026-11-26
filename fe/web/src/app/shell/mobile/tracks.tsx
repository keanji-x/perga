// Native 2-level mobile Workspace transition shell:
// Level 1 (`areas`): WorkspaceLevelOne (AREAS + LIBRARY + sticky Account bar).
// Level 2 (`tracks`): AreaLevelTwo (full-screen Area detail with OPEN TRACKS and collapsible CLOSED).

import { useLayoutEffect, useRef } from 'react';
import { useEdgePresence } from '../../../ui/motion/edge-presence.ts';
import { readSurfaceFrame } from '../../../ui/motion/geometry.ts';
import { useState } from '../../../ui/state/public.ts';
import { AreaLevelTwo } from './area-level-two.tsx';
import styles from './navigation.module.css';
import type { MobileTracksProps } from './tracks-types.ts';
import { WorkspaceLevelOne } from './workspace-level-one.tsx';

/** Stable page shells preserve the Workspace Level 1 scroll position across Level 2 push/pop transitions. */
export function MobileTracks(props: MobileTracksProps) {
  const tracksFrameRef = useRef<HTMLDivElement | null>(null);
  const tracksPageRef = useRef<HTMLDivElement | null>(null);
  const { view, areaId } = props;
  const [tracksPresented, setTracksPresented] = useState(view === 'tracks');
  if (view === 'tracks' && !tracksPresented) setTracksPresented(true);
  useEdgePresence({
    frame: { ref: tracksFrameRef },
    part: { element: tracksPageRef, readFrame: () => readSurfaceFrame(tracksFrameRef.current) },
    edge: 'inline-end',
    open: view === 'tracks',
    present: tracksPresented,
    enabled: true,
    onExited: () => { setTracksPresented(false); },
  });
  useLayoutEffect(() => {
    if (view === 'tracks' && tracksPageRef.current !== null) tracksPageRef.current.scrollTop = 0;
  }, [areaId, view]);

  return (
    <div ref={tracksFrameRef} className={styles.pages}>
      <div
        data-nc-workspace-page="areas"
        className={`${styles.pageFrame} ${view === 'tracks' ? styles.coveredPage : ''}`}
        inert={view !== 'areas'}
        aria-hidden={view !== 'areas' || undefined}
      >
        <WorkspaceLevelOne {...props} />
      </div>
      <div
        ref={tracksPageRef}
        data-nc-workspace-page="tracks"
        className={`${styles.pageFrame} ${view === 'tracks' ? '' : tracksPresented ? styles.coveredPage : styles.inactivePage}`}
        inert={view !== 'tracks'}
        aria-hidden={view !== 'tracks' || undefined}
      >
        <AreaLevelTwo {...props} />
      </div>
    </div>
  );
}
