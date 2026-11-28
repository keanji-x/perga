import { useRef, type Ref } from 'react';
import { flushSync } from 'react-dom';
import { Divider } from '@astryxdesign/core/Divider';
import { DropdownMenu, DropdownMenuItem } from '@astryxdesign/core/DropdownMenu';
import { Icon as AstryxIcon } from '@astryxdesign/core/Icon';
import { Text } from '@astryxdesign/core/Text';
import { VisuallyHidden } from '@astryxdesign/core/VisuallyHidden';

import type { TrackWorkspaceOptions } from '../../../../../core/domain/track.ts';
import { Icon } from '../../../ui/icon/public.tsx';
import { useState } from '../../../ui/state/public.ts';
import { folderLabelParts } from '../default-pills/public.tsx';
import styles from './repo-pill.module.css';

export type RepoIsolationMode = 'worktree' | 'none';

function compactPathDescription(path: string, maxLength = 42): string {
  if (path.length <= maxLength) return path;
  const parts = path.split('/').filter(Boolean);
  if (parts.length >= 2) {
    const tail = parts.slice(-2).join('/');
    if (tail.length + 2 <= maxLength) return `…/${tail}`;
  }
  return `…${path.slice(-(maxLength - 1))}`;
}

const REPO_MODES = Object.freeze([
  Object.freeze({
    value: 'worktree',
    label: 'Git worktree',
    description: 'Default · isolated working copy per track',
  } as const),
  Object.freeze({
    value: 'none',
    label: 'Direct folder',
    description: 'Lead uses source folder · worker tasks still require a worktree',
  } as const),
] as const);

export type RepoPillProps = Readonly<{
  buttonId?: string;
  buttonRef?: Ref<HTMLButtonElement>;
  value: string;
  targetLabel?: string;
  isolation: RepoIsolationMode;
  workspaceOptions?: TrackWorkspaceOptions | null;
  workspaceOptionId?: string;
  onWorkspaceOptionChange?: (id: string) => void;
  isDisabled?: boolean;
  onIsolationChange: (mode: RepoIsolationMode) => void;
  onBrowse: () => void;
  onClear: () => void;
}>;

export function RepoPill({
  buttonId,
  buttonRef,
  value,
  targetLabel,
  isolation,
  workspaceOptions,
  workspaceOptionId,
  onWorkspaceOptionChange,
  isDisabled = false,
  onIsolationChange,
  onBrowse,
  onClear,
}: RepoPillProps) {
  const [open, setOpen] = useState(false);
  const triggerRef = useRef<HTMLButtonElement | null>(null);
  const managed = value === '';
  const modeLabel = workspaceOptions?.options.find((option) => option.id === workspaceOptionId)?.label
    ?? (workspaceOptions === undefined && isolation === 'none' ? 'Direct' : undefined);
  const suffixParts = [targetLabel, modeLabel].filter((part): part is string => part !== undefined && part !== '');
  const { visibleLabel, accessibleName } = folderLabelParts({
    value,
    emptyLabel: targetLabel ?? 'Managed',
    controlLabel: managed ? 'Workspace' : 'Repo',
    showLabel: true,
    modeSuffix: suffixParts.length > 0 ? suffixParts.join(' · ') : undefined,
  });

  const selectedMark = (
    <>
      <AstryxIcon icon="check" size="sm" color="accent" />
      <VisuallyHidden>Selected</VisuallyHidden>
    </>
  );
  const browseItem = (
    <DropdownMenuItem
      label={managed ? 'Choose folder…' : 'Change folder…'}
      description={managed ? 'Bind a local or SSH folder' : compactPathDescription(value)}
      hasCloseOnSelect={false}
      onClick={() => {
        flushSync(() => setOpen(false));
        // Let the menu's selection event finish before handing focus to the dialog.
        queueMicrotask(() => {
          const trigger = triggerRef.current;
          if (trigger === null) return;
          trigger.focus();
          onBrowse();
        });
      }}
    />
  );

  return (
    <DropdownMenu
      placement="above"
      isMenuOpen={open && !isDisabled}
      onOpenChange={(next) => setOpen(next && !isDisabled)}
      hasChevron={false}
      button={{
        id: buttonId,
        ref: (node: HTMLButtonElement | null) => {
          triggerRef.current = node;
          if (typeof buttonRef === 'function') buttonRef(node);
          else if (buttonRef !== undefined && buttonRef !== null) buttonRef.current = node;
        },
        label: accessibleName,
        children: (
          <Text key={visibleLabel} type="inherit" color="inherit" maxLines={1} hasTruncateTooltip={!open}>
            {visibleLabel}
          </Text>
        ),
        variant: 'secondary',
        size: 'sm',
        icon: <Icon name="folder" size="sm" />,
        isDisabled,
        className: styles.trigger,
      }}
    >
      {managed ? (
        <>
          <DropdownMenuItem label="Perga workspace" description="Using managed default workspace" endContent={selectedMark} />
          {browseItem}
        </>
      ) : (
        <>
          <div role="group" aria-label="Workspace mode">
            <div className={styles.groupHeading} aria-hidden="true">Workspace mode</div>
            {workspaceOptions === null && <DropdownMenuItem label="Workspace options unavailable" isDisabled />}
            {workspaceOptions && workspaceOptionId === undefined &&
              <DropdownMenuItem label="Choose an available workspace option" isDisabled />}
            {workspaceOptions?.options.map((option) => (
              <DropdownMenuItem key={option.id} label={option.label} description={option.description}
                onClick={() => onWorkspaceOptionChange?.(option.id)}
                endContent={option.id === workspaceOptionId ? selectedMark : undefined} />
            ))}
            {workspaceOptions === undefined && REPO_MODES.map((option) => (
              <DropdownMenuItem key={option.value} label={option.label} description={option.description}
                onClick={() => onIsolationChange(option.value)}
                endContent={option.value === isolation ? selectedMark : undefined} />
            ))}
          </div>
          <Divider />
          {browseItem}
          <DropdownMenuItem label="Use Perga workspace" description="Switch back to managed default workspace" onClick={onClear} />
        </>
      )}
    </DropdownMenu>
  );
}
