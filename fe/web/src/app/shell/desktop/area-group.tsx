import type { DialogOrigin } from '../../../ui/dialog/public.tsx';
// Area-owned membership and actions, composed into the shared sidebar group.
import { DropdownMenuItem } from '@astryxdesign/core/DropdownMenu';

import type { Area } from '../../../../../core/domain/area.ts';
import { areaPinnedTracks, railAreaTracks, type Track } from '../../../../../core/domain/track.ts';
import { Icon } from '../../../ui/icon/public.tsx';
import { ListText } from '../../../ui/list-typography/public.tsx';
import { SidebarTrackGroup, type RowProps, type GroupManagement } from './sidebar-group.tsx';
import styles from './sidebar.module.css';
import { navigationRowPresentation, navigationRowClass as rowLayout } from '../../../ui/navigation-row/public.ts';
import { useUiPreferences } from '../../providers/ui-preferences.tsx';
import ownStyles from './area-group.module.css';

export function AreaGroup({
  readReady, area, targetLabel, areaTracks, activeTrackId, expanded, onToggle, showClosed, onSetShowClosed, disclosureRef, onEdit,
  onShare, onRequestDelete, onNewTrack, management, memoryEnabled = true, memoryActive = false, ...rowProps
}: RowProps & {
  readReady: boolean;
  management: GroupManagement;
  area: Area;
  targetLabel?: string | undefined;
  areaTracks: readonly Track[] | undefined;
  activeTrackId: string | null;
  expanded: boolean;
  onToggle: (expanded: boolean) => void;
  showClosed: boolean;
  onSetShowClosed: (showClosed: boolean) => void;
  disclosureRef: (element: HTMLButtonElement | null) => void;
  onShare?: (origin: DialogOrigin) => void;
  onEdit: (origin: DialogOrigin) => void;
  onRequestDelete: (areaId: string, origin: DialogOrigin) => void;
  onNewTrack: (areaId: string) => void;
  memoryEnabled?: boolean;
  memoryActive?: boolean;
}) {
  const preferences = useUiPreferences();
  const showMemory = memoryEnabled && preferences.areaShowsMemory(area.id);
  const memoryRow = navigationRowPresentation({ kind: 'entry', depth: 1 });
  const ordered = areaPinnedTracks(areaTracks ?? [], (track) => preferences.areaTrackPinned(area.id, track.id));
  const tracks = railAreaTracks(ordered, activeTrackId, rowProps.isUnread, showClosed);
  const count = tracks.length;
  const areaTitle = area.executionTargetId && area.executionTargetId !== 'local'
    ? `${area.name} · ${targetLabel ?? area.executionTargetId}`
    : area.name;
  return <SidebarTrackGroup
    emptyState={readReady ? { icon: <Icon name="file" />, title: 'No active tracks', description: 'Open tracks in this area appear here.' } : null}
    title={areaTitle}
    label={`area ${area.name}`}
    icon="folder"
    level="area"
    tracks={tracks}
    disclosurePreview={areaTracks === undefined ? undefined : <div className={ownStyles.preview}>
      <strong>{areaTitle}</strong>
      <span>{count} {count === 1 ? 'Track' : 'Tracks'}</span>
    </div>}
    activeTrackId={activeTrackId}
    expanded={expanded}
    onToggle={onToggle}
    disclosureRef={disclosureRef}
    management={management}
    navigation={showMemory && <div className={memoryRow.className}><button type="button" data-nc-role="row"
      className={`${styles.areaRow} ${styles.primaryNavRow} ${ownStyles.memoryRow} ${rowLayout('content')} ${memoryActive ? styles.primaryNavActive : ''}`}
      aria-current={memoryActive ? 'page' : undefined} onClick={() => rowProps.onGo({ name: 'area-memory', areaId: area.id })}>
      <span className={`${styles.primaryNavIcon} ${rowLayout('leading')}`}><Icon name="book-open" size="sm" /></span>
      <ListText tone={memoryRow.tone} emphasis={memoryActive ? 'selected' : 'medium'}>Memory</ListText>
    </button></div>}
    extraMenuItems={(origin) => <>
      {area.kind === 'user' && onShare !== undefined && <DropdownMenuItem label="Share area"
        onClick={() => onShare(origin)} />}
      <DropdownMenuItem label="Edit area" onClick={() => onEdit(origin)} />
      <DropdownMenuItem label={showClosed ? 'Hide closed' : 'Show closed'} onClick={() => onSetShowClosed(!showClosed)} />
      {memoryEnabled && <DropdownMenuItem label={showMemory ? 'Hide memory' : 'Show memory'}
        onClick={() => preferences.setAreaShowsMemory(area.id, !showMemory)} />}
      <DropdownMenuItem label="Delete area" variant="destructive" onClick={() => onRequestDelete(area.id, origin)} />
    </>}
    actions={
      <button type="button" data-nc-role="icon" className={`${styles.areaNew} ${rowLayout('outer')}`}
        aria-label={`New track in ${area.name}`} title="New track" onClick={() => onNewTrack(area.id)}>
        <Icon name="plus" size="sm" />
      </button>
    }
    {...rowProps}
  />;
}
