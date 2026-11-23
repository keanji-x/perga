import mobileStyles from './mobile-composer.module.css';
import { memo, useMemo } from 'react';
import { messageStatusLabel, type ConversationTurn } from '../../../../../core/domain/conversation.ts';
import { AttachmentGallery } from '../../../ui/image-attachments/public.tsx';
import { OutgoingMessage } from './outgoing-message.tsx';
import { Reply, type ReplyImageFiles, type ReplyLinkRenderer } from './reply.tsx';
import styles from './thread.module.css';

/** Only the declared visual inputs of one ordinary message. Transcript grouping,
 * navigation and delivery policy remain in their owners; fields capture values
 * rather than assuming a caller preserves a message object's identity. */
type MessageEntryProps = Readonly<{
  id: string;
  mobile: boolean;
  atMs: number;
  /** Stable for this thread viewport/timezone; only a rendered row formats it. */
  timestampFormatter: Intl.DateTimeFormat | null;
  entryKey: string;
  author: ConversationTurn['author'];
  human: ConversationTurn['human'];
  imported: boolean;
  text: string;
  deliveryError?: string | undefined;
  messageStatus?: ConversationTurn['messageStatus'];
  attachments: ConversationTurn['attachments'];
  opens: boolean;
  gapLabel: string | null;
  queued: boolean;
  edited: boolean;
  replacement: boolean;
  imageFiles: ReplyImageFiles | null;
  renderLink?: ReplyLinkRenderer;
}>;

export const MessageEntry = memo(function MessageEntry({ id, entryKey, author, human, imported, text, deliveryError, messageStatus, attachments,
  opens, gapLabel, queued, edited, replacement, imageFiles, renderLink, mobile, atMs, timestampFormatter,
}: MessageEntryProps) {
  const galleryImages = useMemo(
    () => (attachments ?? []).map((attachment) => ({ id: attachment.id, src: attachment.url })),
    [attachments],
  );
  const showOutgoingText = text !== '' || galleryImages.length === 0;
  return (
    <div
      className={[opens ? styles.exchange : '', mobile && author !== 'agent' ? mobileStyles.userTurn : ''].filter(Boolean).join(' ') || undefined}
      data-nc-entry={entryKey}
      {...(opens ? { 'data-nc-exchange': id } : {})}
    >
      {/* A time only where the conversation restarted. */}
      {gapLabel !== null && (
        <p className={styles.gap}>{gapLabel}</p>
      )}
      {author !== 'agent' ? (
        <>
          {showOutgoingText && (
            <OutgoingMessage speaker={author} text={text} mobile={mobile} queued={queued} edited={edited} replacement={replacement} />
          )}
          {/* Attribution follows the bubble so a real source handoff preserves its position. */}
          {imported && <p className={styles.queuedNote}>Imported history · Unknown author</p>}
          {!imported && author !== 'you' && <p className={styles.queuedNote}>{author === 'human' ? human?.displayName : 'Unknown author'}</p>}
          <AttachmentGallery images={galleryImages} editing={edited} />
          {queued && (
            /* `role="status"`: it appears in response to the reader's own press, answering "did that go anywhere?". */
            <p className={styles.queuedNote} data-nc-queued-note="" role="status">
              Queued · sends when this turn ends
            </p>
          )}
          {(deliveryError !== undefined || (messageStatus !== undefined && messageStatus !== 'completed')) && <p className={styles.queuedNote} role="status">
            {deliveryError ?? (messageStatus === undefined ? null : messageStatusLabel(messageStatus))}
          </p>}
          {replacement && <p className={styles.queuedNote}>Replaces the marked message above</p>}
        </>
      ) : (
        <div className={`${styles.reply} ${mobile ? mobileStyles.reply : ''}`} data-nc-turn="agent">
          <>{imported && <p className={styles.queuedNote}>Imported history</p>}</>
          <Reply text={text} imageFiles={imageFiles} renderLink={renderLink} />
          {timestampFormatter !== null && <time className={mobileStyles.timestamp} dateTime={new Date(atMs).toISOString()}>{timestampFormatter.format(atMs)}</time>}
        </div>
      )}
    </div>
  );
});
