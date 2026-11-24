import { MessageActions } from './message-actions.tsx';
import { useFencedAction } from './status-notice.tsx';

/** Copy the stored input verbatim, including mention syntax and whitespace. */
export function InputCopy({ id, text, copyText }: Readonly<{ id: string; text: string; copyText: (text: string) => Promise<void> }>) {
  const copy = useFencedAction(JSON.stringify([id, text]));
  return <MessageActions label="Original message actions" actions={[{
    id: 'copy-input', kind: copy.feedback?.kind === 'done' ? 'copied' : 'copy',
    label: copy.feedback?.kind === 'done' ? 'Copied message' : copy.feedback?.kind === 'failed' ? 'Copy failed' : 'Copy message',
    disabled: copy.feedback?.kind === 'pending',
    run: () => { void copy.perform(() => copyText(text)); },
  }]} />;
}
