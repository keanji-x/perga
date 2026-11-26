import { conversationCommands, dispatchComposerCommand, executeComposerCommand, type ComposerCommand } from '../thread/composer-command.ts';
import type { Dispatch, SetStateAction } from 'react';
import { useState } from '../../../ui/state/public.ts';
import { Icon } from '../../../ui/icon/public.tsx';
import styles from './dock.module.css';

export type ChatDockRoleSlot = Readonly<{
  label: string;
  ariaLabel?: string;
  onOpen: () => void;
}>;

/** Full-width single-capsule Track bottom dock with fixed 76px/66px left role switcher slot. */
export function ChatDock({
  text,
  onChange,
  onSend,
  disabled,
  onBeginInput,
  roleSlot,
  commands = conversationCommands({ compactViewport: true }),
}: Readonly<{
  commands?: readonly ComposerCommand[];
  text: string;
  onChange: Dispatch<SetStateAction<string>>;
  onSend: (text: string) => void;
  onBeginInput: () => void;
  disabled: boolean;
  roleSlot?: ChatDockRoleSlot;
}>) {
  const [commandError, setCommandError] = useState<string | null>(null);
  return (
    <form
      className={styles.bar}
      aria-label="Quick chat"
      data-nc-chat-dock=""
      tabIndex={-1}
      onSubmit={(event) => {
        event.preventDefault();
        if (disabled || text.trim() === '') return;
        setCommandError(null);
        if (dispatchComposerCommand(commands, text, (command, argument) => {
          void executeComposerCommand(command, argument).then(() => { onChange(current => current === text ? '' : current); }, (reason: unknown) => {
            setCommandError(reason instanceof Error ? reason.message : 'Command failed.');
          });
        })) return;
        onSend(text);
      }}
    >
      {commandError && <p role="alert">{commandError}</p>}
      {roleSlot !== undefined && (
        <button
          type="button"
          className={styles.roleSlot}
          aria-label={roleSlot.ariaLabel ?? `Switch conversation, current: ${roleSlot.label}`}
          onClick={roleSlot.onOpen}
        >
          <span className={styles.roleLabel}>{roleSlot.label}</span>
          <span className={styles.roleChevron} aria-hidden="true">
            <Icon name="chevron-down" size="sm" />
          </span>
        </button>
      )}
      <textarea
        className={styles.input}
        aria-label="Chat message"
        name="chat-message"
        onFocus={onBeginInput}
        placeholder="Write a message…"
        rows={1}
        value={text}
        onChange={(event) => onChange(event.target.value)}
      />
      <button
        type="submit"
        className={styles.send}
        aria-label="Send chat message"
        disabled={disabled || text.trim() === ''}
      >
        <Icon name="arrow-up" size="sm" />
      </button>
    </form>
  );
}
