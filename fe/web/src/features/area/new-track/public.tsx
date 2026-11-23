import type { DialogOrigin } from '../../../ui/dialog/public.tsx';
// The new-track page: one thing to say, with compact preferences in its footer. Presentational
// plus local form state; the caller owns `POST /api/tracks`, `submitting`, `error` and
// puts the sentence on the create as `first_message`, verbatim.

import { useEffect, useMemo, useRef, useId, type ReactNode } from 'react';
import { Banner } from '@astryxdesign/core/Banner';
import { Button } from '@astryxdesign/core/Button';
import { ChatComposer, ChatComposerInput, type ChatComposerInputHandle, type ChatComposerTrigger } from '@astryxdesign/core/Chat';
import { HStack } from '@astryxdesign/core/HStack';
import { Icon } from '@astryxdesign/core/Icon';
import { VStack } from '@astryxdesign/core/VStack';
import {
  dropzoneActiveClassName,
  handleComposerPasteCapture,
  useDropzoneHighlight,
} from '../../../ui/image-attachments/public.tsx';

import type { CreateExecutionTargetBody, ExecutionTargetDto } from '../../../../../core/domain/area.ts';
import { isBlankForKernel, type TrackTemplate } from '../../../../../core/domain/track.ts';
import { extractTemplateMentions } from '../../../../../core/domain/mentions.ts';
import {
  compilePergaTemplateInputs,
  parsePergaInputsSchema,
  type PergaTemplateRawValues,
} from '../../../../../core/domain/template-input.ts';
import { DirectoryPicker } from '../directory-picker.tsx';
import type { ListDirectory } from '../../../ui/directory-browser/public.tsx';
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
  templateId?: string;
  templateInput?: Readonly<Record<string, unknown>>;
}>;

export type NewTrackFormProps = Readonly<{
  workspaceOptions?: Readonly<{
    cwd: string;
    targetId?: string;
    templateId?: string;
    result: import('../../../../../core/domain/track.ts').TrackWorkspaceOptions | null;
  }>;
  /** App-composed model controls beside the send button. */
  modelControls?: ReactNode;
  /** Optional attachment drawer rendered inside the composer above the input. */
  drawer?: ReactNode;
  /** Optional image attach control placed beside footer preferences. */
  attachControl?: ReactNode;
  /** Files pasted or dropped into the new-track composer. */
  onFiles?: (files: File[]) => void;
  /** Allow submitting with blank text when at least one ready image attachment is present. */
  allowEmptyText?: boolean;
  /** Block submission while image uploads are in flight or failed. */
  attachmentSendBlocked?: boolean;
  /** Available Perga templates for explicit selection and typed `inputsSchema` entry. */
  templates?: readonly TrackTemplate[];
  /** Area's default template ID when starting a fresh draft. */
  defaultTemplateId?: string | null;
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

const TASK_PLACEHOLDER = 'What should this track do?';

/** The way back to the managed default, which exists nowhere else. */
const FOLDER_CLEAR_LABEL = 'Use a Perga workspace instead';

export type NewTrackFormState = Readonly<{
  message: string;
  cwd: string;
  executionTargetId?: string;
  isolation?: RepoIsolationMode;
  workspaceOptionId?: string;
  workspaceOptionCwd?: string;
  activeTemplateId?: string;
  templateId?: string | null;
  templateInputs?: PergaTemplateRawValues;
}>;

export function NewTrackForm({
  modelControls, drawer, attachControl, onFiles, allowEmptyText = false, attachmentSendBlocked = false,
  templates, defaultTemplateId, submitting, error, readNotice, initialCwd,
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
  const [templateId, setTemplateId] = useState<string | null>(
    initialDraft?.templateId === undefined ? defaultTemplateId ?? null : initialDraft.templateId);
  const [templateInputs, setTemplateInputs] = useState<PergaTemplateRawValues>(initialDraft?.templateInputs ?? {});
  const [browsing, setBrowsing] = useState<DialogOrigin | null>(null);
  const composerHostRef = useRef<HTMLDivElement | null>(null);
  const inputHandleRef = useRef<ChatComposerInputHandle | null>(null);
  const { isDragOver, dropzoneProps } = useDropzoneHighlight({
    enabled: !submitting && !locked && onFiles !== undefined,
    onDropFiles: (files) => { onFiles?.(files); },
  });
  /* One array per trigger: `useTriggerMenu` drops an open menu when the list changes identity. */
  const triggers = useMemo(() => mentionTrigger === undefined ? undefined : [mentionTrigger], [mentionTrigger]);
  const folderId = `${fieldId}-folder`;
  const templateSelectId = `${fieldId}-template`;
  const hasTemplates = templates !== undefined;
  const hasTemplatePicker = templates !== undefined && templates.length > 0;

  const activeTemplate = useMemo(() => {
    if (!hasTemplatePicker) return undefined;
    if (templateId !== null && templateId !== '') {
      return templates.find((candidate) => candidate.id === templateId && candidate.availability.status === 'ready');
    }
    const mentioned = extractTemplateMentions(message);
    if (mentioned.length === 1) {
      return templates.find((candidate) => candidate.id === mentioned[0] && candidate.availability.status === 'ready');
    }
    return undefined;
  }, [hasTemplatePicker, message, templateId, templates]);

  const templateFields = useMemo(
    () => parsePergaInputsSchema(activeTemplate?.input_schema),
    [activeTemplate?.input_schema],
  );
  const compiledTemplateInputs = useMemo(
    () => compilePergaTemplateInputs(templateFields, templateInputs),
    [templateFields, templateInputs],
  );

  const activeTemplateId = activeTemplate?.id;
  const hasTargetPicker = executionTargets !== undefined || onCreateExecutionTarget !== undefined;
  const resolvedTargetLabel = executionTargetId !== 'local'
    ? (executionTargets?.find((candidate) => candidate.id === executionTargetId)?.label ?? executionTargetId)
    : undefined;
  const workspaceOptions = workspaceOptionsState === undefined ? undefined
    : workspaceOptionsState.cwd === cwd
      && (workspaceOptionsState.targetId ?? 'local') === executionTargetId
      && workspaceOptionsState.templateId === activeTemplateId
      ? workspaceOptionsState.result : null;
  const selectedWorkspaceOption = workspaceChoice === null ? workspaceOptions?.defaultId ?? undefined
    : workspaceChoice.cwd === cwd ? workspaceOptions?.options.find((option) => option.id === workspaceChoice.id)?.id : undefined;

  useEffect(() => {
    const workspace = workspaceChoice === null ? {} : { workspaceOptionId: workspaceChoice.id, workspaceOptionCwd: workspaceChoice.cwd };
    const targetField = hasTargetPicker || initialExecutionTargetId !== undefined ? { executionTargetId } : {};
    if (hasTemplates) {
      onDraftChange?.({ message, cwd, ...targetField, isolation, templateId, templateInputs, activeTemplateId, ...workspace });
    } else {
      onDraftChange?.({ message, cwd, ...targetField, isolation, ...workspace });
    }
  }, [message, cwd, executionTargetId, hasTargetPicker, initialExecutionTargetId, isolation, templateId, templateInputs, activeTemplateId, workspaceChoice, hasTemplates, onDraftChange]);

  /* The caret starts in the field. Found by query, not ref: `ChatComposerInput`
   * forwards its DOM `ref` to the wrapper, which is not focusable. Mount-only, so
   * a later render does not yank the caret from an opened chip. */
  useEffect(() => {
    const field = composerHostRef.current?.querySelector<HTMLElement>('[contenteditable="true"]');
    field?.focus();
  }, []);

  const templateInputsValid = locked || activeTemplate === undefined || compiledTemplateInputs.valid;
  const workspaceSelectionValid = workspaceOptions === undefined || selectedWorkspaceOption !== undefined || locked;
  const hasMessageOrAttachments = !isBlankForKernel(message) || allowEmptyText;
  const valid = hasMessageOrAttachments && templateInputsValid && workspaceSelectionValid && !attachmentSendBlocked;

  function draftFor(text: string, forAction = false): NewTrackDraft | null {
    /* Blank refuses the submit unless ready image attachments are present; `text` goes to the caller
           exactly as typed, and the kernel forwards it untrimmed. */
    if ((isBlankForKernel(text) && !allowEmptyText) || attachmentSendBlocked || !templateInputsValid || !workspaceSelectionValid || submitting || (submitBlocked && !forAction)) return null;
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
      ...(activeTemplate === undefined
        ? {}
        : {
            templateId: activeTemplate.id,
            ...(Object.keys(compiledTemplateInputs.input).length > 0
              ? { templateInput: compiledTemplateInputs.input }
              : {}),
          }),
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

  return (
    <div className={centeredComposerPageClassName}>
      <VStack gap={2} className={centeredComposerColumnClassName}>

        <CenteredComposerIntroduction title="What would you like to work on?">
          Describe your request and optionally choose a Repo for your repository.
          An Area groups a project&apos;s work; a Track keeps one request, its conversation and report.
        </CenteredComposerIntroduction>

        {hasTemplatePicker && (
          <div className={styles.templatePanel} data-nc-new-track-templates>
            <div className={styles.templateHeader}>
              <label className={styles.templateLabel} htmlFor={templateSelectId}>Template</label>
              <select
                id={templateSelectId}
                className={styles.templateSelect}
                value={activeTemplate?.id ?? ''}
                disabled={submitting || locked}
                onChange={(event) => {
                  const nextId = event.target.value;
                  setTemplateId(nextId === '' ? null : nextId);
                  setTemplateInputs({});
                }}
              >
                <option value="">Blank track (no template)</option>
                {templates.filter((candidate) => candidate.availability.status === 'ready').map((candidate) => (
                  <option key={candidate.id} value={candidate.id}>{candidate.title}</option>
                ))}
              </select>
            </div>
            {activeTemplate?.description != null && activeTemplate.description !== '' && (
              <p className={styles.templateDescription}>{activeTemplate.description}</p>
            )}
            {templateFields.length > 0 && (
              <div className={styles.templateFields}>
                {templateFields.map((field) => {
                  const inputId = `${fieldId}-tpl-${field.key}`;
                  const errorText = compiledTemplateInputs.errors[field.key];
                  if (field.type === 'boolean') {
                    const checked = Object.hasOwn(templateInputs, field.key)
                      ? templateInputs[field.key] === true || templateInputs[field.key] === 'true'
                      : field.defaultValue;
                    return (
                      <div key={field.key} className={styles.fieldGroup}>
                        <label className={styles.checkboxRow} htmlFor={inputId}>
                          <input
                            id={inputId}
                            type="checkbox"
                            checked={checked}
                            disabled={submitting || locked}
                            onChange={(event) => {
                              const next = event.target.checked;
                              setTemplateInputs((prev) => ({ ...prev, [field.key]: next }));
                            }}
                          />
                          <span>{field.label}</span>
                        </label>
                      </div>
                    );
                  }
                  if (field.type === 'enum') {
                    const currentVal = Object.hasOwn(templateInputs, field.key)
                      ? String(templateInputs[field.key] ?? '')
                      : (field.defaultValue ?? (field.optional ? '' : (field.values[0] ?? '')));
                    return (
                      <div key={field.key} className={styles.fieldGroup}>
                        <label className={styles.templateLabel} htmlFor={inputId}>
                          {field.label}{!field.optional ? ' *' : ''}
                        </label>
                        <select
                          id={inputId}
                          className={styles.fieldInput}
                          value={currentVal}
                          disabled={submitting || locked}
                          onChange={(event) => {
                            const next = event.target.value;
                            setTemplateInputs((prev) => ({ ...prev, [field.key]: next }));
                          }}
                        >
                          {field.optional && <option value="">—</option>}
                          {field.values.map((option) => (
                            <option key={option} value={option}>{option}</option>
                          ))}
                        </select>
                        {errorText !== undefined && <span className={styles.fieldError}>{errorText}</span>}
                      </div>
                    );
                  }
                  const currentVal = Object.hasOwn(templateInputs, field.key)
                    ? String(templateInputs[field.key] ?? '')
                    : (field.defaultValue !== undefined ? String(field.defaultValue) : '');
                  return (
                    <div key={field.key} className={styles.fieldGroup}>
                      <label className={styles.templateLabel} htmlFor={inputId}>
                        {field.label}{!field.optional ? ' *' : ''}
                      </label>
                      <input
                        id={inputId}
                        type={field.type === 'number' ? 'number' : 'text'}
                        className={styles.fieldInput}
                        value={currentVal}
                        disabled={submitting || locked}
                        onChange={(event) => {
                          const next = event.target.value;
                          setTemplateInputs((prev) => ({ ...prev, [field.key]: next }));
                        }}
                      />
                      {errorText !== undefined && <span className={styles.fieldError}>{errorText}</span>}
                    </div>
                  );
                })}
              </div>
            )}
          </div>
        )}

        <div
          ref={composerHostRef}
          className={[styles.composer, centeredComposerClassName, isDragOver ? dropzoneActiveClassName : ''].filter(Boolean).join(' ')}
          data-nc-new-track-message
          data-nc-dropzone-active={isDragOver ? '' : undefined}
          {...dropzoneProps}
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
            placeholder={TASK_PLACEHOLDER}
            isDisabled={submitting}
            onSubmit={submit}
            {...(drawer === undefined ? {} : { drawer })}
            input={<div
              className={styles.editor}
              onPasteCapture={(event) => {
                handleComposerPasteCapture(event, {
                  inputDisabled: submitting || locked,
                  disabled: onFiles === undefined,
                  onImages: onFiles,
                  onUnsupportedFiles: onFiles,
                  insertText: (plainText) => {
                    if (inputHandleRef.current !== null) inputHandleRef.current.insertText(plainText);
                    else setMessage((current) => `${current}${plainText}`);
                  },
                });
              }}
            >
              <ChatComposerInput
                handleRef={inputHandleRef}
                className={styles.input}
                label={TASK_LABEL}
                placeholder={TASK_PLACEHOLDER}
                isDisabled={submitting || locked}
                /* The `@` source delays its own requests; Astryx's delay would let its per-keystroke probe search fire. */
                {...(triggers === undefined ? {} : { triggers, debounceMs: 0 })}
              />
            </div>}
            footerActions={(() => {
              const preferences = (
                <ComposerPreferences browsing={browsing !== null}
                  folder={{ buttonId: folderId, value: cwd, targetLabel: resolvedTargetLabel, isolation, clearLabel: FOLDER_CLEAR_LABEL,
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
              );
              return attachControl === undefined ? preferences : (
                <HStack gap={1} align="center">{attachControl}{preferences}</HStack>
              );
            })()}
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
