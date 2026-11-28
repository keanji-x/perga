// One editor for both Area creation and Area settings; a pure form, the shell owns
// the Dialog, mutations and directory transport.

import { Banner } from '@astryxdesign/core/Banner';
import { Button } from '@astryxdesign/core/Button';
import { DropdownMenu, DropdownMenuItem } from '@astryxdesign/core/DropdownMenu';
import { HStack } from '@astryxdesign/core/HStack';
import { Icon as AstryxIcon } from '@astryxdesign/core/Icon';
import { Text } from '@astryxdesign/core/Text';
import { TextInput } from '@astryxdesign/core/TextInput';
import { VStack } from '@astryxdesign/core/VStack';
import { VisuallyHidden } from '@astryxdesign/core/VisuallyHidden';
import { useRef, type FormEvent, type RefObject } from 'react';
import { flushSync } from 'react-dom';

import type { CreateExecutionTargetBody, ExecutionTargetDto } from '../../../../../core/domain/area.ts';
import type { ListDirectory } from '../../../ui/directory-browser/public.tsx';
import { Icon } from '../../../ui/icon/public.tsx';
import { useState } from '../../../ui/state/public.ts';
import { basenameOf, compactPathDescription, folderLabelParts } from '../default-pills/public.tsx';
import { DirectoryPicker } from '../directory-picker.tsx';
import styles from './area-editor.module.css';

// Astryx's public type does not expose the native `required` constraint, and
// `isRequired` would duplicate this dialog's only field label with "· Required".
const NATIVE_REQUIRED_INPUT_PROPS = Object.freeze({ required: true });

export type AreaEditorValues = Readonly<{
  name: string;
  defaultCwd: string | null;
  executionTargetId?: string;
}>;

export type AreaEditorPatch = Readonly<{
  name?: string;
  defaultCwd?: string | null;
  executionTargetId?: string;
}>;

export type AreaEditorFormProps = Readonly<{
  submitting: boolean;
  /** An unconfirmed creation must retry its original payload. */
  locked: boolean;
  cancelLabel: string;
  error: string | null;
  listDirectory: ListDirectory;
  listDirectoryForTarget?: (targetId: string) => ListDirectory;
  executionTargets?: readonly ExecutionTargetDto[];
  targetEditable?: boolean;
  onCreateExecutionTarget?: (body: CreateExecutionTargetBody) => Promise<ExecutionTargetDto>;
  nameInputRef: RefObject<HTMLInputElement | null>;
  submitLabel: string;
  onCancel: () => void;
  onSubmit: (values: AreaEditorValues) => void;
  initial: AreaEditorValues;
}>;

export function AreaEditorForm(props: AreaEditorFormProps) {
  const {
    initial, submitting, locked, cancelLabel, error,
    listDirectory, listDirectoryForTarget, executionTargets, targetEditable = false,
    onCreateExecutionTarget, nameInputRef, submitLabel, onCancel, onSubmit,
  } = props;
  const folderRef = useRef<HTMLButtonElement>(null);
  const [name, setName] = useState(initial.name);
  const [cwd, setCwd] = useState(initial.defaultCwd ?? '');
  const [targetId, setTargetId] = useState(initial.executionTargetId ?? 'local');
  const [browsing, setBrowsing] = useState(false);
  const [folderMenuOpen, setFolderMenuOpen] = useState(false);
  const normalizedName = name.trim();
  const targetLabel = targetId === 'local' ? undefined : executionTargets?.find((target) => target.id === targetId)?.label ?? targetId;
  const folderBound = cwd !== '';
  const { visibleLabel, accessibleName } = folderLabelParts({
    value: cwd,
    controlLabel: 'Default folder',
    modeSuffix: targetLabel,
  });
  const fieldsDisabled = submitting || locked;
  const folderButton = {
    ref: folderRef,
    label: accessibleName,
    variant: 'secondary' as const,
    size: 'sm' as const,
    icon: <Icon name="folder" size="sm" />,
    className: styles.folderPill,
    isDisabled: fieldsDisabled,
    children: <Text key={visibleLabel} type="inherit" color="inherit" maxLines={1} hasTruncateTooltip={!folderMenuOpen}>
      {visibleLabel}
    </Text>,
  };

  const openFolderPicker = () => {
    folderRef.current?.focus();
    setBrowsing(true);
  };

  const submit = (event: FormEvent<HTMLElement>) => {
    event.preventDefault();
    if (submitting || normalizedName === '') return;
    // Keep focus inside the modal when the request disables every field: on Enter
    // from Name the browser would otherwise leave the busy Dialog on <body>.
    event.currentTarget.querySelector<HTMLButtonElement>('button[type="submit"]')?.focus();
    onSubmit({
      name: normalizedName,
      defaultCwd: cwd === '' ? null : cwd,
      ...(targetId !== 'local' ? { executionTargetId: targetId } : {}),
    });
  };

  return (
    <VStack as="form" gap={3} className={styles.form} onSubmit={submit}>
      {error !== null && <Banner status="error" role="alert" title={error} data-nc-area-editor-error />}
      <TextInput
        {...NATIVE_REQUIRED_INPUT_PROPS}
        ref={nameInputRef}
        label="Name"
        isLabelHidden
        placeholder="Area name"
        value={name}
        onChange={setName}
        width="100%"
        isDisabled={fieldsDisabled}
      />
      <div className={styles.footer}>
        <DropdownMenu isMenuOpen={folderMenuOpen && !fieldsDisabled}
          onOpenChange={(open) => setFolderMenuOpen(open && !fieldsDisabled)} button={folderButton}>
          <DropdownMenuItem label="Default"
            endContent={folderBound ? undefined : <><AstryxIcon icon="check" size="sm" color="accent" /><VisuallyHidden>Selected</VisuallyHidden></>}
            onClick={folderBound ? () => {
              setCwd('');
              if (targetEditable) setTargetId('local');
            } : undefined} />
          <DropdownMenuItem label={folderBound ? 'Change folder…' : 'Choose folder…'}
            description={folderBound ? compactPathDescription(cwd) : undefined}
            hasCloseOnSelect={false} onClick={() => {
              flushSync(() => setFolderMenuOpen(false));
              queueMicrotask(openFolderPicker);
            }} />
        </DropdownMenu>
        <HStack className={styles.actions} gap={1}>
          <Button type="button" variant="ghost" label={cancelLabel} isDisabled={submitting} onClick={onCancel} />
          <Button
            type="submit"
            variant="primary"
            label={submitting ? 'Saving…' : submitLabel}
            isDisabled={normalizedName === ''}
            isLoading={submitting}
            tooltip={submitting
              ? 'Saving in progress'
              : normalizedName === '' ? 'Enter a name to save' : submitLabel}
          />
        </HStack>
      </div>
      <DirectoryPicker
        open={browsing}
        initialPath={cwd === '' ? null : cwd}
        listDirectory={listDirectory}
        listDirectoryForTarget={listDirectoryForTarget}
        targets={executionTargets}
        selectedTargetId={targetId}
        targetEditable={targetEditable}
        onCreateTarget={onCreateExecutionTarget}
        onCancel={() => setBrowsing(false)}
        onSelect={(path, pickedTargetId) => {
          setCwd(path);
          setName((current) => current.trim() === '' ? basenameOf(path) : current);
          if (targetEditable && pickedTargetId !== undefined) {
            setTargetId(pickedTargetId);
          }
          setBrowsing(false);
        }}
      />
    </VStack>
  );
}
