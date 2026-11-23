import { MESSAGES } from '../../../../../core/domain/messages.ts';
import { MessageActions } from './message-actions.tsx';
import { useFencedAction } from './status-notice.tsx';

/** Copy the stored input verbatim, including mention syntax and whitespace. */
export function InputCopy({ id, text, copyText }: Readonly<{ id: string; text: string; copyText: (text: string) => Promise<void> }>) {
  const copy = useFencedAction(JSON.stringify([id, text]));
  return <MessageActions label={MESSAGES.chat.inputActionsLabel} actions={[{
    id: 'copy-input', kind: copy.feedback?.kind === 'done' ? 'copied' : 'copy',
    label: copy.feedback?.kind === 'done' ? MESSAGES.chat.copiedInput : copy.feedback?.kind === 'failed' ? MESSAGES.chat.copyFailed : MESSAGES.chat.copyInput,
    disabled: copy.feedback?.kind === 'pending',
    run: () => { void copy.perform(() => copyText(text)); },
  }]} />;
}
