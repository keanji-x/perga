import { MessageActions } from './message-actions.tsx';
import { useFencedAction } from './status-notice.tsx';

/** Copy the stored input verbatim, including mention syntax and whitespace. */
export function InputCopy({ id, text, copyText }: Readonly<{ id: string; text: string; copyText: (text: string) => Promise<void> }>) {
  const copy = useFencedAction(JSON.stringify([id, text]));
  return <MessageActions label="原始消息操作" actions={[{
    id: 'copy-input', kind: copy.feedback?.kind === 'done' ? 'copied' : 'copy',
    label: copy.feedback?.kind === 'done' ? '已复制原文' : copy.feedback?.kind === 'failed' ? '复制失败' : '复制原文',
    disabled: copy.feedback?.kind === 'pending',
    run: () => { void copy.perform(() => copyText(text)); },
  }]} />;
}
