import type { RefCallback } from 'react';
import type { Area } from '../../../../../core/domain/area.ts';
import type { DialogOrigin } from '../../../ui/dialog/public.tsx';
import { MobileHeader } from '../../../ui/mobile-header/public.tsx';
import { useMobileShellBack } from '../../router/mobile-back.ts';
import { AreaSelector } from './area-selector.tsx';
import styles from './header.module.css';

export function MobileWorkspaceHeader({
  areas, activeArea, draftArea, onSelectArea, onCreateArea,
  actionsHostRef, titleHostRef, shareMode, obscured,
}: Readonly<{
  shareMode: boolean;
  obscured: boolean;
  areas: readonly Area[];
  activeArea: Area | undefined;
  draftArea?: Pick<Area, 'id' | 'name'>;
  onSelectArea: (areaId: string) => void;
  onCreateArea: (origin: DialogOrigin) => void;
  actionsHostRef: RefCallback<HTMLDivElement>;
  titleHostRef: RefCallback<HTMLDivElement>;
}>) {
  const back = useMobileShellBack(areas, shareMode, draftArea);
  return <div data-nc-workspace-header="" inert={obscured} aria-hidden={obscured || undefined}>
    <MobileHeader className={styles.rootHeader} title={activeArea?.name ?? draftArea?.name ?? 'Choose area'}
      titleContent={<div className={styles.titleContent}>
        <div ref={titleHostRef} className={styles.trackTitleHost} />
        <div className={styles.areaTitle}>
          {!shareMode && <AreaSelector areas={areas} activeArea={activeArea} draftArea={draftArea} onSelectArea={onSelectArea} onCreateArea={onCreateArea} />}
        </div>
      </div>}
      {...back}
      actions={<div ref={actionsHostRef} className={styles.tools} />}
    />
  </div>;
}
