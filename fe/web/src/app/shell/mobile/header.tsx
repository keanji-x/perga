import type { DialogOrigin } from '../../../ui/dialog/public.tsx';
import { Icon } from '../../../ui/icon/public.tsx';
import { type Ref, type RefCallback } from 'react';
import type { Area } from '../../../../../core/domain/area.ts';
import { MobileHeader } from '../../../ui/mobile-header/public.tsx';
import { AreaSelector } from './area-selector.tsx';
import styles from './header.module.css';

export function MobileWorkspaceHeader({
  areas, activeArea, draftArea, navigationOpen, onOpenNavigation, onSelectArea, onCreateArea,
  actionsHostRef, titleHostRef, onBack, backLabel = 'Tracks', historyTriggerRef, shareMode = false,
}: Readonly<{
  shareMode?: boolean;
  onBack?: () => void;
  backLabel?: string;
  historyTriggerRef: Ref<HTMLButtonElement>;
  areas: readonly Area[];
  activeArea: Area | undefined;
  draftArea?: Pick<Area, 'id' | 'name'>;
  navigationOpen: boolean;
  onOpenNavigation: () => void;
  onSelectArea: (areaId: string) => void;
  onCreateArea: (origin: DialogOrigin) => void;
  actionsHostRef: RefCallback<HTMLDivElement>;
  titleHostRef: RefCallback<HTMLDivElement>;
}>) {
  return <div data-nc-workspace-header="" inert={navigationOpen} aria-hidden={navigationOpen || undefined}>
    <MobileHeader className={styles.rootHeader} title={activeArea?.name ?? draftArea?.name ?? 'Choose area'}
      titleContent={<div className={styles.titleContent}>
        <div ref={titleHostRef} className={styles.trackTitleHost} />
        <div className={styles.areaTitle}>
          {!shareMode && <AreaSelector areas={areas} activeArea={activeArea} draftArea={draftArea} onSelectArea={onSelectArea} onCreateArea={onCreateArea} />}
        </div>
      </div>}
      onBack={onBack} backLabel={backLabel} backRef={onBack !== undefined ? historyTriggerRef : undefined}
      leading={onBack === undefined ? <button ref={historyTriggerRef} type="button" className={styles.iconButton} aria-label="Open workspace"
        aria-expanded={navigationOpen} aria-controls="mobile-workspace-navigation" onClick={onOpenNavigation}>
        <Icon name="menu" />
      </button> : undefined}
      actions={<div ref={actionsHostRef} className={styles.tools} />}
    />
  </div>;
}
