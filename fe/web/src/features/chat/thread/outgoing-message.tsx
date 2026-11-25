import { Badge } from '@astryxdesign/core/Badge';
import type { ReactNode } from 'react';
import type { MemoryMention } from '../../../../../core/domain/memory.ts';
import { sentMentionParts } from '../../../../../core/domain/mentions.ts';
import mobileStyles from './mobile-composer.module.css';
import styles from './thread.module.css';

/** The same readable outgoing text before and after a conversation exists.
 * Callers own delivery, grouping, attachments and identity; no row or clock is needed. */
export type MemoryMentionRenderer = (scope: MemoryMention['scope'], pill: ReactNode) => ReactNode;

export function OutgoingMessage({ text, mobile, speaker, queued = false, edited = false, replacement = false, renderMemoryMention }: Readonly<{
  text: string;
  speaker: 'you' | 'human' | 'unknown';
  mobile: boolean;
  queued?: boolean;
  edited?: boolean;
  replacement?: boolean;
  renderMemoryMention?: MemoryMentionRenderer;
}>) {
  return (
    <p
      className={`${styles.said} ${mobile ? mobileStyles.said : ''}`}
      data-nc-turn={speaker}
      {...(queued ? { 'data-nc-queued': '' } : {})}
      {...(edited ? { 'data-nc-editing': '' } : {})}
      {...(replacement ? { 'data-nc-replacement': '' } : {})}
    >{sentMentionParts(text).map((part, index) => {
      if (part.label === null) return part.text;
      const pill = <Badge label={part.label} />;
      return <span key={index} className={styles.mentionToken} data-nc-sent-mention="" title={part.text}>
        {part.memoryScope === undefined ? pill : renderMemoryMention?.(part.memoryScope, pill) ?? pill}
      </span>;
    })}</p>
  );
}
