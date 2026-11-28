import { Banner } from '@astryxdesign/core/Banner';
import { Button } from '@astryxdesign/core/Button';
import { TextInput } from '@astryxdesign/core/TextInput';
import { useEffect, useRef, useSyncExternalStore, type FormEvent } from 'react';

import type { CreateExecutionTargetBody, ExecutionTargetDto } from '../../../../core/domain/area.ts';
import { Dialog, useDialogView, type DialogOrigin } from '../../ui/dialog/public.tsx';
import { DirectoryBrowser, type DirectoryMode, type ListDirectory } from '../../ui/directory-browser/public.tsx';
import { Icon } from '../../ui/icon/public.tsx';
import { useState } from '../../ui/state/public.ts';
import styles from './directory-picker.module.css';

export interface DirectoryPickerProps {
  open: boolean;
  origin?: DialogOrigin;
  initialPath: string | null;
  listDirectory: ListDirectory;
  listDirectoryForTarget?: (targetId: string) => ListDirectory;
  targets?: readonly ExecutionTargetDto[];
  selectedTargetId?: string;
  targetEditable?: boolean;
  onCreateTarget?: (body: CreateExecutionTargetBody) => Promise<ExecutionTargetDto>;
  mode?: DirectoryMode;
  title?: string;
  onCancel: () => void;
  onSelect: (path: string, targetId?: string) => void;
}

const DEFAULT_LOCAL_TARGET: ExecutionTargetDto = Object.freeze({
  id: 'local',
  label: 'Local',
  kind: 'local',
  isDefault: true,
  destination: null,
  removable: false,
});

interface TargetStore {
  subscribe: (listener: () => void) => () => void;
  getSnapshot: () => readonly ExecutionTargetDto[] | undefined;
  update: (targets: readonly ExecutionTargetDto[] | undefined) => void;
}

function DirectoryPickerContent({
  initialPath,
  listDirectory,
  listDirectoryForTarget,
  targetStore,
  selectedTargetId,
  targetEditable = false,
  onCreateTarget,
  mode,
  onCancel,
  onSelect,
}: Omit<DirectoryPickerProps, 'open' | 'origin' | 'title' | 'targets'> & { targetStore: TargetStore }) {
  const targets = useSyncExternalStore(targetStore.subscribe, targetStore.getSnapshot);
  const initialTargetId = selectedTargetId ?? 'local';
  const [localTargets, setLocalTargets] = useState<readonly ExecutionTargetDto[]>(() => {
    if (targets !== undefined && targets.length > 0) return targets;
    return targetEditable && onCreateTarget !== undefined ? [DEFAULT_LOCAL_TARGET] : [];
  });
  useEffect(() => {
    if (targets === undefined) return;
    setLocalTargets((current) => [...new Map([...current, ...targets].map((target) => [target.id, target])).values()]);
  }, [targets]);
  const [activeTargetId, setActiveTargetId] = useState(initialTargetId);
  const [browsedInitialPath, setBrowsedInitialPath] = useState<string | null>(initialPath);
  const [view, setView] = useState<'browse' | 'add-ssh'>('browse');
  const [sshTarget, setSshTarget] = useState('');
  const [sshLabel, setSshLabel] = useState('');
  const [sshIdentityFile, setSshIdentityFile] = useState('');
  const [adding, setAdding] = useState(false);
  const [addError, setAddError] = useState<string | null>(null);
  const sshTargetInputRef = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    if (view === 'add-ssh') {
      requestAnimationFrame(() => sshTargetInputRef.current?.focus());
    }
  }, [view]);

  const activeListDirectory = listDirectoryForTarget ? listDirectoryForTarget(activeTargetId) : listDirectory;
  const activeTarget = localTargets.find((candidate) => candidate.id === activeTargetId);
  const showInteractiveTargets = targetEditable && (localTargets.length > 1 || onCreateTarget !== undefined);
  const showReadonlyTarget = !targetEditable && activeTargetId !== 'local';

  const switchTarget = (nextId: string) => {
    if (nextId === activeTargetId) return;
    setActiveTargetId(nextId);
    setBrowsedInitialPath(null);
  };

  const submitNewSshTarget = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (adding || onCreateTarget === undefined) return;
    const trimmedTarget = sshTarget.trim();
    if (trimmedTarget === '') return;

    setAdding(true);
    setAddError(null);
    try {
      const created = await onCreateTarget({
        target: trimmedTarget,
        ...(sshLabel.trim() !== '' ? { label: sshLabel.trim() } : {}),
        ...(sshIdentityFile.trim() !== '' ? { identityFile: sshIdentityFile.trim() } : {}),
      });
      setLocalTargets((prev) => (prev.some((item) => item.id === created.id)
        ? prev.map((item) => (item.id === created.id ? created : item))
        : [...prev, created]));
      setActiveTargetId(created.id);
      setBrowsedInitialPath(null);
      setSshTarget('');
      setSshLabel('');
      setSshIdentityFile('');
      setView('browse');
    } catch (reason: unknown) {
      setAddError(reason instanceof Error && reason.message !== ''
        ? reason.message
        : 'Could not connect to SSH machine.');
    } finally {
      setAdding(false);
    }
  };

  if (view === 'add-ssh') {
    return (
      <form
        className={styles.sshForm}
        onSubmit={(event) => { void submitNewSshTarget(event); }}
        data-nc-add-ssh-target-form
      >
        <p className={styles.sshTitle}>Add SSH machine</p>
        {addError !== null && <Banner status="error" role="alert" title={addError} data-nc-add-ssh-target-error />}
        <TextInput
          ref={sshTargetInputRef}
          label="SSH target"
          placeholder="user@host or user@host:2222"
          value={sshTarget}
          onChange={setSshTarget}
          width="100%"
          isDisabled={adding}
        />
        <TextInput
          label="Display name (optional)"
          placeholder="e.g. jasper or gpu-box"
          value={sshLabel}
          onChange={setSshLabel}
          width="100%"
          isDisabled={adding}
        />
        <TextInput
          label="Identity file (optional)"
          placeholder="e.g. ~/.ssh/id_ed25519"
          value={sshIdentityFile}
          onChange={setSshIdentityFile}
          width="100%"
          isDisabled={adding}
        />
        <p className={styles.sshHint}>
          Uses passwordless SSH key auth from the Perga host and verifies connectivity before saving.
        </p>
        <div className={styles.sshActions}>
          <Button
            type="button"
            variant="ghost"
            label="Back"
            isDisabled={adding}
            onClick={() => { setAddError(null); setView('browse'); }}
          />
          <Button
            type="submit"
            variant="primary"
            label={adding ? 'Connecting…' : 'Connect & add'}
            isLoading={adding}
            isDisabled={adding || sshTarget.trim() === ''}
          />
        </div>
      </form>
    );
  }

  return (
    <div className={styles.picker}>
      {showInteractiveTargets && (
        <div className={styles.targetBar} role="group" aria-label="Execution target" data-nc-target-pills>
          {localTargets.map((target) => (
            <button
              key={target.id}
              type="button"
              className={styles.targetPill}
              aria-pressed={activeTargetId === target.id}
              title={target.destination ?? target.label}
              onClick={() => switchTarget(target.id)}
            >
              <span className={styles.targetPillLabel}>{target.label}</span>
            </button>
          ))}
          {onCreateTarget !== undefined && (
            <button
              type="button"
              className={styles.addTargetPill}
              aria-label="Add SSH machine"
              title="Add SSH machine"
              onClick={() => { setAddError(null); setView('add-ssh'); }}
            >
              <Icon name="plus" size="sm" />
              <span>SSH machine</span>
            </button>
          )}
        </div>
      )}
      {showReadonlyTarget && (
        <div className={styles.targetBar}>
          <span className={styles.targetBadge} title={activeTarget?.destination ?? activeTarget?.label ?? activeTargetId}>
            <span className={styles.targetPillLabel}>{activeTarget?.label ?? activeTargetId}</span>
          </span>
        </div>
      )}
      <DirectoryBrowser
        key={activeTargetId}
        listDirectory={activeListDirectory}
        initialPath={browsedInitialPath}
        mode={mode}
        onCancel={onCancel}
        onSelect={(path) => onSelect(path, targetEditable ? activeTargetId : selectedTargetId)}
      />
    </div>
  );
}

/**
 * The Area editor pushes the picker into its dialog. The New Track composer
 * hosts the same picker in its own dialog.
 */
export function DirectoryPicker({
  open,
  origin = { kind: 'surface-center' },
  initialPath,
  listDirectory,
  listDirectoryForTarget,
  targets,
  selectedTargetId,
  targetEditable = false,
  onCreateTarget,
  mode = 'directory',
  title = 'Choose a directory',
  onCancel,
  onSelect,
}: DirectoryPickerProps) {
  const dialog = useDialogView();
  const [targetStore] = useState<TargetStore>(() => {
    let snapshot = targets;
    const listeners = new Set<() => void>();
    return {
      subscribe: (listener) => { listeners.add(listener); return () => { listeners.delete(listener); }; },
      getSnapshot: () => snapshot,
      update: (next) => {
        if (snapshot === next) return;
        snapshot = next;
        listeners.forEach((listener) => listener());
      },
    };
  });
  useEffect(() => { targetStore.update(targets); }, [targetStore, targets]);

  const content = (
    <DirectoryPickerContent
      listDirectory={listDirectory}
      listDirectoryForTarget={listDirectoryForTarget}
      targetStore={targetStore}
      selectedTargetId={selectedTargetId}
      targetEditable={targetEditable}
      onCreateTarget={onCreateTarget}
      initialPath={initialPath}
      mode={mode}
      onCancel={onCancel}
      onSelect={onSelect}
    />
  );

  useEffect(() => {
    if (!dialog || !open) return;
    return dialog.pushView({
      title,
      wide: 'medium',
      onEscape: onCancel,
      body: content,
    });
  // eslint-disable-next-line react-hooks/exhaustive-deps -- capture the path and ports once when browsing opens; selection must not push a second view.
  }, [open, dialog]);

  if (dialog !== null) return null;

  return (
    <Dialog
      purpose="form"
      origin={origin}
      open={open}
      onClose={onCancel}
      title={title}
      wide="medium"
      placement="center"
    >
      {open && content}
    </Dialog>
  );
}
