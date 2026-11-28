import type { DialogOrigin } from '../../../ui/dialog/public.tsx';
// The new-track page: one thing to say, with compact preferences in its footer. Presentational
// plus local form state; the caller owns `POST /api/tracks`, `submitting`, `error` and
// puts the sentence on the create as `first_message`, verbatim.

import { useEffect, useMemo, useRef, useId, type ReactNode } from 'react';
import { Banner } from '@astryxdesign/core/Banner';
import { Button } from '@astryxdesign/core/Button';
import { ChatComposer, type ChatComposerTrigger } from '@astryxdesign/core/Chat';
import { DropdownMenu, DropdownMenuItem } from '@astryxdesign/core/DropdownMenu';
import { Icon } from '@astryxdesign/core/Icon';
import { Text } from '@astryxdesign/core/Text';
import { Icon as PergaIcon } from '../../../ui/icon/public.tsx';
import { VStack } from '@astryxdesign/core/VStack';
import { ComposerInputField } from '../../../ui/composer-attachments/public.tsx';

import type { CreateExecutionTargetBody, ExecutionTargetDto } from '../../../../../core/domain/area.ts';
import { isBlankForKernel } from '../../../../../core/domain/track.ts';
import { DirectoryPicker } from '../directory-picker.tsx';
import type { ListDirectory } from '../../../ui/directory-browser/public.tsx';
import { PageHeader } from '../../../ui/page-header/public.tsx';
import { useState } from '../../../ui/state/public.ts';
import { triggerMenuKeyRoute } from '../../../ui/trigger-menu-keys/public.ts';
import { CenteredComposerIntroduction } from '../../../ui/drawer/centered-composer-introduction.tsx';
import styles from './new-track.module.css';
import { centeredComposerPageClassName, centeredComposerColumnClassName, centeredComposerClassName, centeredComposerSendClassName } from '../../../ui/drawer/centered-composer.ts';
import { ComposerPreferences } from './composer-preferences.tsx';
import type { RepoIsolationMode } from './repo-pill.tsx';

export type { RepoIsolationMode };

export type NewTrackDraft = Readonly<{
  /**
   * What the user typed: the track's intent, not its title. Verbatim, whitespace
   * included, and never blank by the kernel's rule (`isBlankForKernel`).
   */
  message: string;
  /** Absolute path, or the key is absent: an empty string would take the attached branch with a path that cannot work. */
  cwd?: string;
  executionTargetId?: string;
  isolation?: RepoIsolationMode;
  workspaceOptionId?: string;
}>;

type AreaIdentity = Readonly<{ id: string; name: string }>;

export type NewTrackFormProps = Readonly<{
  area: AreaIdentity;
  areas?: readonly AreaIdentity[];
  onSelectArea?: (areaId: string) => void;
  workspaceOptions?: Readonly<{
    cwd: string;
    targetId?: string;
    result: import('../../../../../core/domain/track.ts').TrackWorkspaceOptions | null;
  }>;
  /** App-composed model controls beside the send button. */
  modelControls?: ReactNode;
  /** App-composed image attachment button beside footer preferences. */
  attachButton?: ReactNode;
  /** App-composed floating image attachment strip rendered directly above the composer. */
  drawer?: ReactNode;
  /** Whether a blank text message is still submittable (for image-only track creation). */
  allowEmptyText?: boolean;
  /** Files dropped or pasted into the field; the caller owns validation and previews. */
  onFiles?: (files: File[]) => void;
  /** The `@` menu over this Area's reports, composed by the app: the sentence is the new track's Planner's first message. Kept stable by the caller. */
  mentionTrigger?: ChatComposerTrigger;
  submitting: boolean;
  error: string | null;
  /** Caller-owned read feedback; the draft remains editable while a read is pending. */
  readNotice?: ReactNode;
  /** In-memory route draft, including the unfinished message. */
  initialDraft?: NewTrackFormState;
  onDraftChange?: (draft: NewTrackFormState) => void;
  /** Keep the text available when its parent Area cannot accept a write. */
  submitBlocked?: boolean;
  /** An uncertain creation retries the original request instead of new edits. */
  locked?: boolean;
  /** One caller-owned recovery beside the create error; the form only renders the action and preserves its fields. */
  errorAction?: Readonly<{
    label: string;
    isApplicable?: (draft: NewTrackDraft) => boolean;
    onClick: (draft: NewTrackDraft) => void;
  }>;
  initialCwd: string | null;
  initialExecutionTargetId?: string;
  executionTargets?: readonly ExecutionTargetDto[];
  /** The folder picker's read port, created at the composition layer. Required: a call site that forgot it would render a picker that silently lists nothing. */
  listDirectory: ListDirectory;
  listDirectoryForTarget?: (targetId: string) => ListDirectory;
  onCreateExecutionTarget?: (body: CreateExecutionTargetBody) => Promise<ExecutionTargetDto>;
  onSubmit: (draft: NewTrackDraft) => void;
}>;

/** The composer field's accessible name; hidden, not absent, since an unnamed textbox is unusable by screen reader. */
const TASK_LABEL = 'What this track should do';

export type NewTrackFormState = Readonly<{
  message: string;
  cwd: string;
  executionTargetId?: string;
  isolation?: RepoIsolationMode;
  workspaceOptionId?: string;
  workspaceOptionCwd?: string;
}>;

export function NewTrackForm({
  area, areas, onSelectArea, modelControls, attachButton, drawer, allowEmptyText = false, onFiles,
  submitting, error, readNotice, initialCwd,
  initialExecutionTargetId, executionTargets, listDirectory, listDirectoryForTarget, onCreateExecutionTarget, onSubmit,
  errorAction, initialDraft, onDraftChange, submitBlocked = false, locked = false, mentionTrigger,
  workspaceOptions: workspaceOptionsState,
}: NewTrackFormProps) {
  const fieldId = useId();
  const [message, setMessage] = useState(initialDraft?.message ?? '');
  const [cwd, setCwd] = useState(initialDraft?.cwd ?? initialCwd ?? '');
  const [executionTargetId, setExecutionTargetId] = useState(
    initialDraft?.executionTargetId ?? initialExecutionTargetId ?? 'local',
  );
  const [isolation, setIsolation] = useState<RepoIsolationMode>(initialDraft?.isolation ?? 'worktree');
  const [workspaceChoice, setWorkspaceChoice] = useState(initialDraft?.workspaceOptionId === undefined ? null
    : { id: initialDraft.workspaceOptionId, cwd: initialDraft.workspaceOptionCwd ?? initialDraft.cwd });
  const [browsing, setBrowsing] = useState<DialogOrigin | null>(null);
  const composerHostRef = useRef<HTMLDivElement | null>(null);
  /* One array per trigger: `useTriggerMenu` drops an open menu when the list changes identity. */
  const triggers = useMemo(() => mentionTrigger === undefined ? undefined : [mentionTrigger], [mentionTrigger]);
  const placeholder = triggers === undefined ? 'What should this track do?' : 'What should this track do? Type @ for templates or tracks…';
  const folderId = `${fieldId}-folder`;
  const hasTargetPicker = executionTargets !== undefined || onCreateExecutionTarget !== undefined;
  const resolvedTargetLabel = executionTargetId !== 'local'
    ? (executionTargets?.find((candidate) => candidate.id === executionTargetId)?.label ?? executionTargetId)
    : undefined;
  const workspaceOptions = workspaceOptionsState === undefined ? undefined
    : workspaceOptionsState.cwd === cwd
      && (workspaceOptionsState.targetId ?? 'local') === executionTargetId
      ? workspaceOptionsState.result : null;
  const selectedWorkspaceOption = workspaceChoice === null ? workspaceOptions?.defaultId ?? undefined
    : workspaceChoice.cwd === cwd ? workspaceOptions?.options.find((option) => option.id === workspaceChoice.id)?.id : undefined;

  useEffect(() => {
    const workspace = workspaceChoice === null ? {} : { workspaceOptionId: workspaceChoice.id, workspaceOptionCwd: workspaceChoice.cwd };
    const targetField = hasTargetPicker || initialExecutionTargetId !== undefined ? { executionTargetId } : {};
    onDraftChange?.({ message, cwd, ...targetField, isolation, ...workspace });
  }, [message, cwd, executionTargetId, hasTargetPicker, initialExecutionTargetId, isolation, workspaceChoice, onDraftChange]);

  /* The caret starts in the field. Found by query, not ref: `ChatComposerInput`
   * forwards its DOM `ref` to the wrapper, which is not focusable. Mount-only, so
   * a later render does not yank the caret from an opened chip. */
  useEffect(() => {
    const field = composerHostRef.current?.querySelector<HTMLElement>('[contenteditable="true"]');
    field?.focus();
  }, []);

  const workspaceSelectionValid = workspaceOptions === undefined || selectedWorkspaceOption !== undefined || locked;
  const valid = (!isBlankForKernel(message) || allowEmptyText || locked) && workspaceSelectionValid;

  function draftFor(text: string, forAction = false): NewTrackDraft | null {
    /* Blank refuses the submit unless inline attachments carry the turn or a locked create is retrying; it does not rewrite it. `text` goes to the caller
           exactly as typed, and the kernel forwards it untrimmed. */
    if ((isBlankForKernel(text) && !allowEmptyText && !locked) || !workspaceSelectionValid || submitting || (submitBlocked && !forAction)) return null;
    /* Spread, not `cwd: cwd || undefined`: the caller keys managed-vs-attached on
           whether the key is there. A path travels byte-for-byte; leading/trailing
           spaces are legal POSIX path characters. */
    return {
      message: text,
      ...(cwd === '' ? {} : { cwd, isolation }),
      ...(executionTargetId !== (initialExecutionTargetId ?? 'local') || executionTargetId !== 'local'
        ? { executionTargetId }
        : {}),
      ...(selectedWorkspaceOption === undefined ? {} : { workspaceOptionId: selectedWorkspaceOption }),
    };
  }

  function submit(text: string): void {
    const draft = draftFor(text);
    if (draft !== null) onSubmit(draft);
  }

  const actionDraft = draftFor(message, true);
  const shownErrorAction = errorAction !== undefined
    && actionDraft !== null
    && (errorAction.isApplicable?.(actionDraft) ?? true)
    ? errorAction
    : undefined;

  const areaControl = areas !== undefined && areas.length > 1 && onSelectArea !== undefined ? (
    <div className={styles.areaSwitcher}>
      <DropdownMenu button={{
        label: `Area: ${area.name}`,
        children: <Text type="inherit" color="inherit" maxLines={1}>{area.name}</Text>,
        variant: 'ghost',
        size: 'sm',
        icon: <PergaIcon name="folder" size="sm" />,
      }}>
        {areas.map(({ id, name }) => (
          <DropdownMenuItem key={id} label={name}
            endContent={id === area.id ? <Icon icon="check" size="sm" /> : undefined}
            onClick={() => { if (id !== area.id) onSelectArea(id); }} />
        ))}
      </DropdownMenu>
    </div>
  ) : (
    <span className={styles.areaLabel}>
      <PergaIcon name="folder" size="sm" /><span>{area.name}</span>
    </span>
  );

  const stage = (
    <div className={`${centeredComposerPageClassName} ${styles.stage}`}>
      <VStack gap={2} className={centeredComposerColumnClassName}>
        <CenteredComposerIntroduction title="What would you like to work on?" />

        <div
          ref={composerHostRef}
          className={`${styles.composer} ${centeredComposerClassName}`}
          data-nc-new-track-message
          /* Enter is ours: astryx's `ChatComposer.handleSubmit` clears the controlled value unconditionally after calling us, so a refused
             submit would lose the sentence. Only when the field itself is the target; Enter while composing is accepting an IME candidate, not sending. */
          onKeyDownCapture={(event) => {
            if (event.key !== 'Enter' && event.key !== 'Tab') return;
            const target = event.target as HTMLElement | null;
            const field = target?.closest?.('[contenteditable="true"]') ?? null;
            if (field === null) return;
            const route = triggerMenuKeyRoute(event.nativeEvent, field);
            if (route === 'composing' || route === 'swallow') {
              if (route === 'swallow') event.preventDefault();
              event.stopPropagation();
              return;
            }
            if (route === 'menu' || event.key !== 'Enter' || event.shiftKey) return;
            if (target !== field) {
              /* A control inside the editable: Enter belongs to it, so this neither submits
                               nor `preventDefault`s, but it must stop propagation or the editable
                               hands the event to astryx's own Enter handling, which submits. */
              event.stopPropagation();
              return;
            }
            event.stopPropagation();
            event.preventDefault();
            submit(message);
          }}
        >
          <ChatComposer
            density="spacious"
            value={message}
            onChange={setMessage}
            placeholder={placeholder}
            isDisabled={submitting}
            onSubmit={submit}
            {...(drawer === undefined ? {} : { drawer })}
            input={(
              <ComposerInputField
                className={styles.editor}
                inputClassName={styles.input}
                label={TASK_LABEL}
                placeholder={placeholder}
                disabled={submitting || locked}
                onFiles={onFiles}
                /* The `@` source delays its own requests; Astryx's delay would let its per-keystroke probe search fire. */
                triggers={triggers}
                debounceMs={triggers === undefined ? undefined : 0}
              />
            )}
            footerActions={<>
              {attachButton}
              <ComposerPreferences browsing={browsing !== null}
                folder={{ buttonId: folderId, value: cwd, targetLabel: resolvedTargetLabel, isolation,
                  workspaceOptions, workspaceOptionId: selectedWorkspaceOption,
                  onWorkspaceOptionChange: (id) => setWorkspaceChoice({ id, cwd }),
                  onIsolationChange: setIsolation,
                  onBrowse: (origin) => setBrowsing(origin),
                  onClear: () => {
                    setCwd('');
                    if (hasTargetPicker) setExecutionTargetId('local');
                    setIsolation('worktree');
                    setWorkspaceChoice(null);
                  },
                  isDisabled: submitting || locked }}
              />
            </>}
            /* Model and Create stay together at the trailing edge. */
            sendActions={modelControls === undefined ? undefined : <span className={styles.modelControls}>{modelControls}</span>}
            sendButton={(
              <Button
                type="button"
                className={centeredComposerSendClassName}
                variant="primary"
                isIconOnly
                icon={<Icon icon="arrowUp" size="sm" />}
                label={submitting ? 'Creating…' : 'Create track'}
                isDisabled={submitting || submitBlocked || !valid}
                onClick={() => submit(message)}
              />
            )}
          />
        </div>

        {readNotice}
        {error !== null && (
          <Banner
            status="error"
            role="alert"
            title={error}
            endContent={shownErrorAction === undefined
              ? submitBlocked && message !== ''
                ? <Button label="Select draft" variant="ghost" onClick={() => {
                  /* The field itself, not its role: the `@` menu makes it a combobox. */
                  const field = composerHostRef.current?.querySelector<HTMLElement>('[contenteditable]');
                  if (field === null || field === undefined) return;
                  field.focus();
                  const range = document.createRange();
                  range.selectNodeContents(field);
                  const selection = window.getSelection();
                  selection?.removeAllRanges();
                  selection?.addRange(range);
                }} />
                : undefined
              : (
                <Button
                  label={shownErrorAction.label}
                  variant="ghost"
                  onClick={() => {
                    if (actionDraft !== null) shownErrorAction.onClick(actionDraft);
                  }}
                />
              )}
            data-nc-new-track-error
          />
        )}

      </VStack>
    </div>
  );

  return (
    <div className={styles.page}>
      <PageHeader className={styles.header} title={areaControl} />
      {stage}

      {/* The picker as a real modal: shared `DirectoryPicker` owns the Dialog,
                focus trap, Escape and click-outside. */}
      <DirectoryPicker
        open={browsing !== null}
        origin={browsing ?? { kind: 'surface-center' }}
        initialPath={cwd === '' ? null : cwd}
        listDirectory={listDirectory}
        listDirectoryForTarget={listDirectoryForTarget}
        targets={executionTargets}
        selectedTargetId={executionTargetId}
        targetEditable={hasTargetPicker}
        onCreateTarget={onCreateExecutionTarget}
        onCancel={() => setBrowsing(null)}
        onSelect={(path, pickedTargetId) => {
          setCwd(path);
          if (hasTargetPicker && pickedTargetId !== undefined) {
            setExecutionTargetId(pickedTargetId);
          }
          setWorkspaceChoice(null);
          setBrowsing(null);
        }}
      />
    </div>
  );
}
