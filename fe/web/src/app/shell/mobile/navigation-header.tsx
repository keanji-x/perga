import type { DialogOrigin } from '../../../ui/dialog/public.tsx';
import { floatingControlClassName } from '../../../ui/floating-control/public.ts';
import { useRef, type ReactNode } from 'react';
import { MobileHeader } from '../../../ui/mobile-header/public.tsx';
import { List, ListItem } from '@astryxdesign/core/List';
import { MobileListGroup } from '../../../ui/mobile-list/public.tsx';
import { Icon as AstryxIcon } from '@astryxdesign/core/Icon';
import { IconButton } from '@astryxdesign/core/IconButton';
import { Menu } from '../../../ui/menu/public.tsx';
import menuStyles from './header.module.css';
import type { Area } from '../../../../../core/domain/area.ts';
import { Icon } from '../../../ui/icon/public.tsx';
import styles from './navigation.module.css';

export type MobileNavigationActions = Readonly<{
  onBack: () => void;
  onNewTrack: (areaId: string) => void;
  onEditArea: (area: Area, origin: DialogOrigin) => void;
  onOpenSettings: () => void;
  onCreateArea: (origin: DialogOrigin) => void;
}>;

/** Keep navigation in the title bar and workspace actions in one stable group. */
export function MobileNavigationHeader({
  title, area, creationScope, backLabel, filters, showSettings = true, shareMode = false, memoryVisibility, onBack, onNewTrack, onEditArea, onOpenSettings, onCreateArea,
}: Readonly<{ title: string; filters?: ReactNode; showSettings?: boolean; shareMode?: boolean; area: Area | undefined; creationScope: 'area' | 'track' | 'both'; backLabel: string;
  memoryVisibility?: Readonly<{ shown: boolean; toggle: () => void }>;
}> & MobileNavigationActions) {
  const headerRef = useRef<HTMLSpanElement | null>(null);
  const rowRef = useRef<HTMLLIElement | null>(null);
  return <>
    <MobileHeader title={title} backLabel={backLabel} onBack={onBack}
      actions={<span ref={headerRef} style={{ display: 'inline-flex' }}>{shareMode || area === undefined ? undefined : <Menu wrapClassName={menuStyles.toolsMenuWrap} menuClassName={menuStyles.toolsMenu} itemClassName={menuStyles.menuItem}
        items={[{ label: `Edit area ${area.name}`,
          onSelect: () => { onEditArea(area, { kind: 'anchor', anchor: { ref: headerRef }, unavailable: 'surface-center' }); } },
          ...(memoryVisibility === undefined ? [] : [{ label: memoryVisibility.shown ? 'Hide memory' : 'Show memory', onSelect: memoryVisibility.toggle }])]}
        trigger={(props) => <IconButton {...props} label="Area actions" icon={<AstryxIcon icon="moreHorizontal" size="md" color="inherit" />}
          variant="ghost" size="lg" className={`${styles.iconButton} ${floatingControlClassName}`} />} />}</span>} />
    {filters}
    {!shareMode && <div className={styles.actionRows}><MobileListGroup label="Workspace actions">
      <List density="balanced" className={styles.actionList}>
        {showSettings && <ListItem label="Settings" className={styles.actionRow}
          startContent={<AstryxIcon icon="wrench" size="md" color="secondary" />}
          onClick={onOpenSettings} />}
        {creationScope !== 'area' && <ListItem label="New track" className={styles.actionRow}
          startContent={<Icon name="plus" />} isDisabled={area === undefined}
          onClick={() => { if (area !== undefined) onNewTrack(area.id); }} />}
        {creationScope !== 'track' && <ListItem ref={rowRef} label="New area" className={styles.actionRow}
          startContent={<Icon name="plus" />} onClick={() => onCreateArea({ kind: 'anchor', anchor: { ref: rowRef }, unavailable: 'surface-center' })} />}
      </List>
    </MobileListGroup></div>}
  </>;
}
