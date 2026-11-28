import { AreaEditorForm } from '../../features/area/editor/public.tsx';
import { Dialog } from '../../ui/dialog/public.tsx';
import type { ShellController } from './use-shell-controller.ts';

/** Shared create/edit Area dialog driven by the headless shell controller. */
export function AreaEditorDialog({ editor }: Readonly<{ editor: ShellController['areaEditor'] }>) {
  const { target, pending, error, createRequest } = editor;
  return (
    <Dialog
      purpose="form"
      origin={target?.origin ?? { kind: 'surface-center' }}
      open={target !== null}
      title={target?.kind === 'edit' ? `Edit ${target.area.name}` : 'New area'}
      onClose={editor.onClose}
      hideClose={pending}
      initialFocusRef={editor.nameInputRef}
    >
      {target !== null && (
        <AreaEditorForm
          key={target.kind === 'edit' ? target.area.id : 'new-area'}
          initial={target.kind === 'edit'
            ? {
              name: target.area.name,
              defaultCwd: target.area.defaultCwd,
              executionTargetId: target.area.executionTargetId,
            }
            : {
              name: createRequest?.body.name ?? '',
              defaultCwd: createRequest?.body.default_cwd ?? null,
              executionTargetId: createRequest?.body.execution_target_id ?? 'local',
            }}
          submitting={pending}
          locked={target.kind === 'create' && createRequest !== null}
          error={error}
          listDirectory={editor.listDirectory}
          listDirectoryForTarget={editor.listDirectoryForTarget}
          executionTargets={editor.executionTargets}
          targetEditable
          onCreateExecutionTarget={editor.onCreateExecutionTarget}
          nameInputRef={editor.nameInputRef}
          submitLabel={target.kind === 'edit' ? 'Save'
            : createRequest === null ? 'Create' : 'Try again'}
          cancelLabel={!pending && target.kind === 'create' && createRequest !== null ? 'Discard draft' : 'Cancel'}
          onCancel={editor.onCancel}
          onSubmit={editor.onSubmit}
        />
      )}
    </Dialog>
  );
}
