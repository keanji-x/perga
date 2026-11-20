// @vitest-environment jsdom
// The conversation registry's composer refill and outbox bookkeeping, driven through the provider itself.

import { act, cleanup, render } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';

import { StrictMode, useEffect, useLayoutEffect } from 'react';

import { ConversationProvider, useConversationCommands, useConversationComposerText, useConversationUpload, type ConversationRegistry } from './public.tsx';

const CONVERSATION = 'conversation-a';


function mountRegistry(): () => ConversationRegistry {
  let registry!: ConversationRegistry;
  function Probe() {
    const current = useConversationCommands();
    useEffect(() => { registry = current; });
    return null;
  }
  render(<ConversationProvider><Probe /></ConversationProvider>);
  return () => registry;
}

afterEach(cleanup);

it('does not execute an unrelated card upload reader when composer text changes', () => {
  let registry!: ConversationRegistry;
  let renders = 0;
  function Commands() {
    registry = useConversationCommands();
    return null;
  }
  function Upload() {
    useConversationUpload('another-card');
    renders += 1;
    return null;
  }
  render(<ConversationProvider><Commands /><Upload /></ConversationProvider>);
  const before = renders;
  act(() => { registry.editComposer(CONVERSATION, current => ({ ...current, text: 'First words' })); });
  expect(renders).toBe(before);
});

it('observes a write between render and subscription without publishing from render', () => {
  let text = '';
  function Reader() { text = useConversationComposerText(CONVERSATION); return <span>{text}</span>; }
  function Parent() {
    const registry = useConversationCommands();
    useLayoutEffect(() => { registry.editComposer(CONVERSATION, current => ({ ...current, text: 'Landed before subscribing' })); }, [registry]);
    return <Reader />;
  }
  render(<ConversationProvider><Parent /></ConversationProvider>);
  expect(text).toBe('Landed before subscribing');
});

it('keeps StrictMode subscriptions live, retains facts across last unsubscribe and isolates replacement Providers', () => {
  let commands!: ConversationRegistry; let text = ''; let renders = 0;
  function Reader() {
    commands = useConversationCommands();
    text = useConversationComposerText(CONVERSATION); renders += 1;
    return null;
  }
  const tree = (key: string, shown = true) => <StrictMode><ConversationProvider key={key}>{shown && <Reader />}</ConversationProvider></StrictMode>;
  const view = render(tree('first'));
  const original = commands;
  act(() => { original.editComposer(CONVERSATION, current => ({ ...current, text: 'Retained words' })); });
  expect(text).toBe('Retained words'); expect(commands).toBe(original);
  view.rerender(tree('first', false)); view.rerender(tree('first'));
  expect(text).toBe('Retained words'); expect(commands).toBe(original);
  view.rerender(tree('replacement'));
  expect(text).toBe(''); expect(commands).not.toBe(original);
  const before = renders;
  act(() => { original.editComposer(CONVERSATION, current => ({ ...current, text: 'Late old completion' })); });
  expect(text).toBe(''); expect(renders).toBe(before);
});

it('switches card subscriptions without notifying the previous identity and cleans up exactly', () => {
  let registry!: ConversationRegistry; let text = ''; let renders = 0;
  function Reader({ id }: { id: string }) {
    registry = useConversationCommands(); text = useConversationComposerText(id); renders += 1;
    return null;
  }
  const view = render(<ConversationProvider><Reader id={CONVERSATION} /></ConversationProvider>);
  act(() => { registry.editComposer(CONVERSATION, current => ({ ...current, text: 'A' })); });
  view.rerender(<ConversationProvider><Reader id="another" /></ConversationProvider>);
  expect(text).toBe('');
  const before = renders;
  act(() => { registry.editComposer(CONVERSATION, current => ({ ...current, text: 'A changed' })); });
  expect(renders).toBe(before);
  act(() => { registry.editComposer('another', current => ({ ...current, text: 'B' })); });
  expect(text).toBe('B');
  view.unmount();
  act(() => { registry.editComposer('another', current => ({ ...current, text: 'B changed' })); });
  expect(text).toBe('B');
});

/* #2068 item 30: an Edit merges the turn into whatever the composer holds, so the images it could not take are the
   merge's, not the turn's alone; they are said, never dropped silently. */
it('ignores a late acceptance after a valid rekey without touching a newer draft', () => {
  const registry = mountRegistry();
  const submitted = Object.freeze({ text: 'old words', atMs: 3 });
  const from = { scopeId: 'track', key: 'old-key' };
  act(() => { registry().startDraft({ ...from, provider: 'codex', model: { model: null, reasoning_effort: null }, text: 'new words',
    submitted, creating: false, error: null, remedy: null }); });
  act(() => { registry().editDraft(from, current => ({ ...current, key: 'new-key', submitted: null })); });
  act(() => { expect(registry().acceptDraft(from, {
    conversation: { id: CONVERSATION, trackId: 'track', kind: 'agent', isLead: false, provider: 'codex', title: null, state: 'idle', updatedAt: 1 },
    sessionInput: { inputId: 'old-entry', sessionId: CONVERSATION, turnId: 'turn', deliveryState: 'accepted', turnStatus: 'queued', statusUrl: '/status' },
  }, submitted)).toBe(false); });
  expect(registry().draftOf(from.scopeId)).toMatchObject({ key: 'new-key', text: 'new words', submitted: null });
  expect(registry().outboxOf(CONVERSATION)).toEqual([]);
});
