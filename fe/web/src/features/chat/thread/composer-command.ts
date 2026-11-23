import { parseComposerCommand } from '../../../../../core/domain/composer-command.ts';
import { PLANNER_SWITCH_ARGUMENTS } from '../../../../../core/domain/planner-switch.ts';
import type { IconName } from '../../../ui/icon/public.tsx';
import { TERMINAL_ARGUMENTS_MESSAGE, TERMINAL_DESKTOP_MESSAGE } from '../../../../../core/domain/terminal-command.ts';

/** One registry drives discovery, selection and typed dispatch. Unavailable entries remain recognized. */
export type ComposerCommand = Readonly<{
  id: string;
  name: string;
  icon: IconName;
  hint: string;
  arguments: 'none' | 'optional';
  /** Discovery visibility is separate from typed-command execution policy. */
  hidden?: boolean;
  unavailableReason?: string;
  argumentError?: string;
  execute: (argument: string) => void | boolean | Promise<void | boolean>;
}>;
export type CommandBinding = Pick<ComposerCommand, 'execute' | 'unavailableReason' | 'hidden'>;

// The sole display order, shared by every surface. Bindings supply policy and effects, never ordering.
function commandDefinitions(): readonly Omit<ComposerCommand, 'execute'>[] {
  return [
    { id: 'track-stop', name: 'stop', icon: 'status-exited', hint: 'Stop the current response', arguments: 'none' },
    { id: 'reconnect-conversation', name: 'reconnect', icon: 'chat', hint: 'Reconnect session and keep context', arguments: 'none' },
    { id: 'reset-conversation', name: 'reset', icon: 'chat', hint: 'Reset context while keeping chat history', arguments: 'none' },
    { id: 'new-conversation', name: 'new', icon: 'plus', hint: 'Start a new conversation and keep this one', arguments: 'none' },
    { id: 'side-conversation', name: 'side', icon: 'chat', hint: 'Discuss a snapshot in a side conversation', arguments: 'optional' },
    { id: 'planner-switch', name: 'planner', icon: 'chat', hint: 'Promote this Assistant to Planner', arguments: 'none', argumentError: PLANNER_SWITCH_ARGUMENTS },
    { id: 'compact-conversation', name: 'compact', icon: 'compact', hint: 'Compact conversation context', arguments: 'none' },
    { id: 'terminal-workbench', name: 'terminal', icon: 'terminal', hint: 'Open the track terminal workbench', arguments: 'none', argumentError: TERMINAL_ARGUMENTS_MESSAGE },
    { id: 'track-close', name: 'close', icon: 'status-done', hint: 'Close this Track', arguments: 'none' },
    { id: 'track-open', name: 'open', icon: 'status-running', hint: 'Reopen this track', arguments: 'none' },
  ];
}

export type CommandName = 'stop' | 'reconnect' | 'reset' | 'new' | 'side' | 'planner' | 'compact' | 'terminal' | 'close' | 'open';
export type CommandBindings = Readonly<Partial<Record<CommandName, CommandBinding>>>;

export function composerCommands(bindings: CommandBindings): readonly ComposerCommand[] {
  return commandDefinitions().flatMap(command => {
    const binding = bindings[command.name as CommandName];
    return binding === undefined ? command.name === 'planner'
      ? [{ ...command, hidden: true, unavailableReason: 'Planner switching is not available in this conversation.', execute: () => false }] : []
      : [{ ...command, ...binding }];
  });
}

/** Existing conversation actions, assembled by the owner; no Track policy lives here. */
export function conversationCommandBindings(options: Readonly<{
  newConversation?: () => void;
  side?: (question: string) => void | boolean;
  sideHidden?: boolean;
  sideUnavailableReason?: string;
  compact?: () => void;
  terminal?: () => void;
  compactViewport?: boolean;
}>): CommandBindings {
  return {
    new: options.newConversation === undefined ? undefined : { execute: options.newConversation },
    side: options.side === undefined ? undefined : { execute: options.side, hidden: options.sideHidden, unavailableReason: options.sideUnavailableReason },
    compact: options.compact === undefined ? undefined : { execute: options.compact },
    terminal: options.terminal === undefined && !options.compactViewport ? undefined : { execute: () => options.terminal?.(),
      unavailableReason: options.compactViewport ? TERMINAL_DESKTOP_MESSAGE : undefined },
  };
}

export function conversationCommands(options: Parameters<typeof conversationCommandBindings>[0]): readonly ComposerCommand[] {
  return composerCommands(conversationCommandBindings(options));
}

export function matchesCommandName(name: string, typed: string, match: 'prefix' | 'exact'): boolean {
  const query = name === 'side' ? typed.toLowerCase() : typed;
  return match === 'prefix' ? name.startsWith(query) : name === query;
}

/** All chat send surfaces use the same recognition and binding policy. */
export function dispatchComposerCommand(commands: readonly ComposerCommand[], text: string,
  run: (command: ComposerCommand, argument: string) => void): boolean {
  const parsed = parseComposerCommand(text);
  const command = parsed === null ? undefined : commands.find(candidate => matchesCommandName(candidate.name, parsed.name, 'exact'));
  if (command === undefined || parsed === null) return false;
  run(command, parsed.argument);
  return true;
}

export async function executeComposerCommand(command: ComposerCommand, argument: string): Promise<void> {
  const error = command.unavailableReason
    ?? (command.arguments === 'none' && argument !== '' ? command.argumentError ?? `Use /${command.name} without arguments.` : undefined);
  if (error !== undefined) throw new Error(error);
  if (await command.execute(argument) === false) throw new Error(`/${command.name} was not accepted. Try again when it is available.`);
}
