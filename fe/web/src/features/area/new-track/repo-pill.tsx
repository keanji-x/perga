import { useRef } from 'react';
import { flushSync } from 'react-dom';
import { Divider } from '@astryxdesign/core/Divider';
import { DropdownMenu, DropdownMenuItem } from '@astryxdesign/core/DropdownMenu';
import { Icon as AstryxIcon } from '@astryxdesign/core/Icon';
import { Text } from '@astryxdesign/core/Text';
import { VisuallyHidden } from '@astryxdesign/core/VisuallyHidden';

import type { TrackWorkspaceOptions } from '../../../../../core/domain/track.ts';
import { MESSAGES } from '../../../../../core/domain/messages.ts';
import { Icon } from '../../../ui/icon/public.tsx';
import { useState } from '../../../ui/state/public.ts';
import { FolderClearButton, folderLabelParts } from '../default-pills/public.tsx';
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

function repoModes() {
  return [
    {
      value: 'worktree' as const,
      label: MESSAGES.repo.worktreeLabel,
      description: MESSAGES.repo.worktreeDescription,
    },
    {
      value: 'none' as const,
      label: MESSAGES.repo.directLabel,
      description: MESSAGES.repo.directDescription,
    },
  ] as const;
}

export type RepoPillProps = Readonly<{
  buttonId?: string;
  value: string;
  targetLabel?: string;
  isolation: RepoIsolationMode;
  clearLabel: string;
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
  value,
  targetLabel,
  isolation,
  clearLabel,
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
  const modeLabel = workspaceOptions?.options.find((option) => option.id === workspaceOptionId)?.label
    ?? (workspaceOptions === undefined && isolation === 'none' ? MESSAGES.repo.directShortLabel : undefined);
  const suffixParts = [targetLabel, modeLabel].filter((part): part is string => part !== undefined && part !== '');
  const { visibleLabel, accessibleName } = folderLabelParts({
    value,
    emptyLabel: targetLabel !== undefined && targetLabel !== '' ? targetLabel : undefined,
    controlLabel: 'Repo',
    showLabel: true,
    modeSuffix: suffixParts.length > 0 ? suffixParts.join(' · ') : undefined,
  });

  return (
    <>
      <DropdownMenu
        placement="above"
        isMenuOpen={open && !isDisabled}
        onOpenChange={(next) => setOpen(next && !isDisabled)}
        hasChevron={false}
        button={{
          id: buttonId,
          ref: triggerRef,
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
        <div role="group" aria-label={MESSAGES.repo.modeHeading}>
          <div className={styles.groupHeading} aria-hidden="true">{MESSAGES.repo.modeHeading}</div>
          {workspaceOptions === null && <DropdownMenuItem label="Workspace options unavailable" isDisabled />}
          {workspaceOptions && workspaceOptionId === undefined &&
            <DropdownMenuItem label="Choose an available workspace option" isDisabled />}
          {workspaceOptions?.options.map((option) => (
            <DropdownMenuItem key={option.id} label={option.label} description={option.description}
              onClick={() => onWorkspaceOptionChange?.(option.id)}
              endContent={option.id === workspaceOptionId ? (
                <>
                  <AstryxIcon icon="check" size="sm" color="accent" />
                  <VisuallyHidden>Selected</VisuallyHidden>
                </>
              ) : undefined}
            />
          ))}
          {workspaceOptions === undefined && repoModes().map((option) => {
            const managedOnly = value === '';
            const selected = managedOnly ? option.value === 'worktree' : isolation === option.value;
            const disabled = managedOnly && option.value === 'none';
            return (
              <DropdownMenuItem
                key={option.value}
                label={option.label}
                description={disabled ? MESSAGES.repo.directRequiresFolder : option.description}
                isDisabled={disabled}
                onClick={() => {
                  if (!disabled) onIsolationChange(option.value);
                }}
                endContent={selected ? (
                  <>
                    <AstryxIcon icon="check" size="sm" color="accent" />
                    <VisuallyHidden>Selected</VisuallyHidden>
                  </>
                ) : undefined}
              />
            );
          })}
        </div>
        <Divider />
        <DropdownMenuItem
          label={MESSAGES.repo.othersLabel}
          hasCloseOnSelect={false}
          description={value !== '' ? compactPathDescription(value) : MESSAGES.repo.browseFolderHint}
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
        <DropdownMenuItem
          label={MESSAGES.repo.managedWorkspaceLabel}
          description={value === '' ? MESSAGES.repo.managedWorkspaceActive : MESSAGES.repo.managedWorkspaceSwitch}
          onClick={() => {
            if (value !== '') onClear();
          }}
          endContent={value === '' ? (
            <>
              <AstryxIcon icon="check" size="sm" color="accent" />
              <VisuallyHidden>Selected</VisuallyHidden>
            </>
          ) : undefined}
        />
      </DropdownMenu>
      {value !== '' && <FolderClearButton label={clearLabel} isDisabled={isDisabled} onClear={onClear} />}
    </>
  );
}
