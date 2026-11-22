// One editor for both Area creation and Area settings; a pure form, the shell owns
// the Dialog, mutations and directory transport.

import { Banner } from '@astryxdesign/core/Banner';
import { Button } from '@astryxdesign/core/Button';
import { HStack } from '@astryxdesign/core/HStack';
import { TextInput } from '@astryxdesign/core/TextInput';
import { VStack } from '@astryxdesign/core/VStack';
import { useId, type FormEvent, type RefObject } from 'react';

import type { CreateExecutionTargetBody, ExecutionTargetDto } from '../../../../../core/domain/area.ts';
import type { ListDirectory } from '../../../ui/directory-browser/public.tsx';
import { useState } from '../../../ui/state/public.ts';
import { FolderPill } from '../default-pills/public.tsx';
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
  const baseId = useId();
  const folderId = `${baseId}-default-folder`;
  const [name, setName] = useState(initial.name);
  const [cwd, setCwd] = useState(initial.defaultCwd ?? '');
  const [targetId, setTargetId] = useState(initial.executionTargetId ?? 'local');
  const [browsing, setBrowsing] = useState(false);
  const normalizedName = name.trim();
  const resolvedTargetLabel = executionTargets?.find((target) => target.id === targetId)?.label ?? targetId;
  const activeListDirectory = listDirectoryForTarget ? listDirectoryForTarget(targetId) : listDirectory;

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
      {error !== null && <Banner status="error" title={error} data-nc-area-editor-error />}
      <TextInput
        {...NATIVE_REQUIRED_INPUT_PROPS}
        ref={nameInputRef}
        label="Name"
        value={name}
        onChange={setName}
        width="100%"
        isDisabled={submitting || locked}
      />
      <HStack gap={1} align="center" className={styles.controlRow}>
        <HStack gap={1} align="center" className={styles.pills}>
          <FolderPill
            buttonId={folderId}
            value={cwd}
            emptyLabel={targetId !== 'local' ? resolvedTargetLabel : undefined}
            modeSuffix={targetId !== 'local' ? resolvedTargetLabel : undefined}
            controlLabel="Default folder"
            clearLabel="Use a new Neige workspace"
            isDisabled={submitting || locked}
            onBrowse={() => setBrowsing(true)}
            onClear={() => {
              setCwd('');
              if (targetEditable) setTargetId('local');
            }}
          />
        </HStack>
        <HStack gap={1} justify="end" className={styles.actions}>
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
      </HStack>
      <DirectoryPicker
        open={browsing}
        initialPath={cwd === '' ? null : cwd}
        listDirectory={activeListDirectory}
        listDirectoryForTarget={listDirectoryForTarget}
        targets={executionTargets}
        selectedTargetId={targetId}
        targetEditable={targetEditable}
        onCreateTarget={onCreateExecutionTarget}
        onCancel={() => setBrowsing(false)}
        onSelect={(path, pickedTargetId) => {
          setCwd(path);
          if (targetEditable && pickedTargetId !== undefined) {
            setTargetId(pickedTargetId);
          }
          setBrowsing(false);
        }}
      />
    </VStack>
  );
}
