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
import { compactPathDescription, folderLabelParts } from '../default-pills/public.tsx';
import styles from './repo-pill.module.css';

export type RepoIsolationMode = 'worktree' | 'none';

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
  const setTrigger = (node: HTMLButtonElement | null) => {
    triggerRef.current = node;
    if (typeof buttonRef === 'function') buttonRef(node);
    else if (buttonRef !== undefined && buttonRef !== null) buttonRef.current = node;
  };
  const triggerProps = {
    id: buttonId,
    ref: setTrigger,
    variant: 'secondary' as const,
    size: 'sm' as const,
    icon: <Icon name="folder" size="sm" />,
    isDisabled,
    className: styles.trigger,
  };
  const folderBound = value !== '';
  const modeLabel = workspaceOptions?.options.find((option) => option.id === workspaceOptionId)?.label
    ?? (workspaceOptions === undefined && isolation === 'none' ? 'Direct' : undefined);
  const { visibleLabel, accessibleName } = folderLabelParts({
    value,
    controlLabel: 'Repo',
    showLabel: true,
    modeSuffix: folderBound ? [targetLabel, modeLabel].filter(Boolean).join(' · ') : targetLabel,
  });

  const selectedMark = (
    <>
      <AstryxIcon icon="check" size="sm" color="accent" />
      <VisuallyHidden>Selected</VisuallyHidden>
    </>
  );
  const afterMenuSelection = (action: () => void) => {
    flushSync(() => setOpen(false));
    // Let the selection event finish before transferring focus.
    queueMicrotask(action);
  };

  return (
    <DropdownMenu
      placement="above"
      isMenuOpen={open && !isDisabled}
      onOpenChange={(next) => setOpen(next && !isDisabled)}
      button={{
        ...triggerProps,
        label: accessibleName,
        children: (
          <Text key={visibleLabel} type="inherit" color="inherit" maxLines={1} hasTruncateTooltip={!open}>
            {visibleLabel}
          </Text>
        ),
      }}
    >
      {folderBound && <>
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
      </>}
      <DropdownMenuItem label="Default" endContent={folderBound ? undefined : selectedMark}
        hasCloseOnSelect={!folderBound} onClick={folderBound ? () => afterMenuSelection(() => {
          flushSync(onClear);
          triggerRef.current?.focus();
        }) : undefined} />
      <DropdownMenuItem label={folderBound ? 'Change folder…' : 'Choose folder…'}
        description={folderBound ? compactPathDescription(value) : undefined} hasCloseOnSelect={false}
        onClick={() => afterMenuSelection(() => {
          const trigger = triggerRef.current;
          if (trigger === null) return;
          trigger.focus();
          onBrowse();
        })} />
    </DropdownMenu>
  );
}
