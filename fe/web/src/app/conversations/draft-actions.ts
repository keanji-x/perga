import type { ManagedProviderAvailability } from '../../../../core/domain/agent-providers.ts';
import { managedProviderProblem } from '../../../../core/domain/agent-providers.ts';
import { onlineManager } from '@tanstack/react-query';
import { CONVERSATION_CREATE_TEXT, conversationCreateFailure, CONVERSATION_TEXT_MAX, FOLLOW_INSTALLATION_DEFAULT, type CreatedConversation, type SubmittedConversationInput } from '../../../../core/domain/conversation.ts';
import { ApiError, NotSentError } from '../../../../core/domain/failure-class.ts';
import { mintIdempotencyKey } from '../providers/idempotency-key.ts';
import type { ConversationDraft, ConversationDraftId, ConversationRegistry } from './public.tsx';
import type { ConversationCreationSource } from './contracts.ts';

/** The identity and dispatched body move together through rekey/markSent only. */
type DraftEdit = Partial<Pick<ConversationDraft, 'text' | 'creating' | 'error' | 'remedy'>>;

export type ConversationDraftActionInput = Readonly<{
  registry: Pick<ConversationRegistry, 'editDraft' | 'startDraft' | 'acceptDraft' | 'discardDraft'>;
  draft: ConversationDraft | null;
  creating: boolean;
  source: ConversationCreationSource;
  sourceScopeId: string;
  supportsSideConversation: boolean;
  supportsDraftModel: boolean;
  supportsInitialInput: boolean;
  supportsAssistantProviders: boolean;
  defaultProvider?: import('../../../../core/api/schemas.ts').AgentProvider;
  managedProviders: readonly ManagedProviderAvailability[] | undefined;
  recheckManagedProviders: () => Promise<readonly ManagedProviderAvailability[]>;
  openDraft: () => void;
  closeView: () => void;
  onGone: () => void;
  onAdopt: (from: ConversationDraftId, conversationId: string) => void;
}>;

/** A command assembly for this render's draft. Construction has no side effects;
 * dispatched attempts retain their original scope/key and settle through Registry. */
export function createConversationDraftActions({ registry, draft, creating, source, sourceScopeId,
  supportsSideConversation, supportsDraftModel, supportsInitialInput, supportsAssistantProviders, defaultProvider, managedProviders, recheckManagedProviders, openDraft, closeView, onGone, onAdopt,
}: ConversationDraftActionInput) {
  /* Every draft write goes through one of these three, each a whole-object update,
       and each a no-op when the draft it was computed from is no longer the one held. */
  const withDraft = (
    from: ConversationDraftId, next: (current: ConversationDraft) => ConversationDraft,
  ) => {
    registry.editDraft(from, next);
  };
  const amendDraft = (from: ConversationDraft, change: DraftEdit) => {
    withDraft(from, (current) => ({ ...current, ...change }));
  };
  /* The only way to change the key, and it always clears `submitted`: a key is the
       identity of an attempt and `submitted` is what that attempt sent. */
  const rekeyDraft = (from: ConversationDraft, key: string, change: DraftEdit = {}): ConversationDraft => {
    const next = { ...from, ...change, key, submitted: null };
    withDraft(from, (current) => ({ ...current, ...change, key, submitted: null }));
    return next;
  };
  /** Records that a POST is going out under this key with these words. Called
   *  before the request, so a failure finds the right baseline. */
  const markDraftSent = (from: ConversationDraft, submitted: SubmittedConversationInput) => {
    withDraft(from, (current) => ({ ...current, text: submitted.text, submitted }));
  };

  /* The `+` opens a draft scoped to one concrete Track. */
  const begin = (text: string | null): boolean => {
    /* A draft that was sent and failed is still open business: reopened with the
           same key, so the next attempt is a retry and not a second conversation. */
    if (draft !== null && draft.submitted !== null) {
      openDraft();
      return false;
    }
    /* The key is minted once, for the draft, not per send: a different key on the
           retry is a different derived card. */
    const readyProvider = defaultProvider !== undefined
      && (managedProviders === undefined || managedProviders.some((entry) => entry.availability.provider === defaultProvider && entry.availability.status === 'ready'))
      ? defaultProvider
      : managedProviders?.find((entry) => entry.availability.status === 'ready')?.availability.provider
        ?? defaultProvider
        ?? 'codex';
    registry.startDraft({
      scopeId: sourceScopeId,
      provider: readyProvider, model: FOLLOW_INSTALLATION_DEFAULT,
      key: mintIdempotencyKey(),
      text, autoSend: text !== null, submitted: null, creating: false, error: null, remedy: null,
    });
    openDraft();
    return true;
  };
  const start = () => { begin(null); };
  const startWithMessage = (text: string) => begin(text);

  /* `from` is not decoration: this runs after an `await`, and the reducer records
       the row only if `from` is still held. */
  const adopt = (from: ConversationDraft, result: CreatedConversation) => {
    if (from.submitted === null) {
      throw new Error('The first-message result does not match this conversation.');
    }
    if (registry.acceptDraft(from, result, from.submitted)) onAdopt(from, result.conversation.id);
  };

  /* A press that sent nothing is refused (#2131), unless an earlier attempt under this key, `submitted`, may have
     created the conversation: then it stays unconfirmed. Why nothing went out is the global indicator's to say. */
  const notSentText = (submitted: SubmittedConversationInput | null): string =>
    submitted === null ? CONVERSATION_CREATE_TEXT.refused : CONVERSATION_CREATE_TEXT.unknown;

  /* The draft's own send: it runs while there is no card, and its text lives in the
       registry's draft entry until the row it created is adopted. */
  const refuseOfflineDraft = (attempt: ConversationDraft, text: string, answers = managedProviders): boolean => {
    if (!supportsInitialInput) {
      amendDraft(attempt, { text, error: 'This server does not support first-message receipts. Update or reconnect before sending.', remedy: 'retry' });
      return true;
    }
    const providerProblem = !supportsAssistantProviders
      ? 'This server does not support Assistant provider selection. Update or reconnect before sending.'
      : attempt.submitted === null ? managedProviderProblem(answers, attempt.provider) : null;
    if (providerProblem !== null) {
      amendDraft(attempt, { text, error: providerProblem, remedy: 'retry' });
      return true;
    }
    if (attempt.side !== undefined && !supportsSideConversation) {
      amendDraft(attempt, { text, error: 'This server does not report side conversation support. Update or reconnect before retrying.', remedy: 'retry' });
      return true;
    }
    if ((attempt.model.model !== null || attempt.model.reasoning_effort !== null) && !supportsDraftModel) {
      amendDraft(attempt, { text, error: 'This server does not support choosing the first message’s model yet.', remedy: 'retry' });
      return true;
    }
    if (onlineManager.isOnline()) return false;
    amendDraft(attempt, { text, error: notSentText(attempt.submitted), remedy: 'retry' });
    return true;
  };

  /** Retry frozen submissions under their original key; only an explicit new conversation rekeys. */
  const createDraft = (from: ConversationDraft, text: string, recheck: ConversationDraft['remedy'], answers = managedProviders) => {
    const { create } = source;
    /* The draft this send is for, fixed here: a send that outlives its draft changes nothing. */
    let attempt = from;
    let previouslySubmitted = attempt.submitted;
    void (async () => {
      try {
        // Refresh display metadata, but a frozen dispatched request keeps its original
        // request body: neither probe failure nor new capabilities can prevent reconciliation/replay.
        if (attempt.submitted !== null) void recheckManagedProviders().catch(() => undefined);
        if (recheck === 'new-conversation') attempt = rekeyDraft(attempt, mintIdempotencyKey());
        if (refuseOfflineDraft(attempt, text, answers)) return;
        previouslySubmitted = attempt.submitted;
        const submitted = attempt.submitted ?? Object.freeze({ text, atMs: Date.now(), ...(attempt.attachments?.length ? { attachments: attempt.attachments } : {}) });
        markDraftSent(attempt, submitted);
        attempt = { ...attempt, text, submitted };
        const files = submitted.attachments?.flatMap(image => image.file === undefined ? [] : [image.file]);
        const answered = (result: CreatedConversation) => adopt(attempt, result);
        if (files?.length) await create(submitted.text, attempt.key, attempt.provider, attempt.model, attempt.side, answered, attempt.permissionMode, files);
        else await create(submitted.text, attempt.key, attempt.provider, attempt.model, attempt.side, answered, attempt.permissionMode);
      } catch (error: unknown) {
        if (error instanceof NotSentError) {
          // Marking a request optimistically must not invent dispatch when the
          // mutation's later guard refused it. Keep any earlier unknown send.
          registry.editDraft(attempt, (current) => ({ ...current, submitted: previouslySubmitted }));
          amendDraft(attempt, { error: notSentText(previouslySubmitted), remedy: 'retry' });
        } else {
          attempt = handleCreateFailure(error, attempt, previouslySubmitted);
        }
      } finally {
        amendDraft(attempt, { creating: false });
      }
    })();
  };

  const sendDraft = (text: string, answers = managedProviders) => {
    if (creating || draft === null) return false;
    /* The server refuses `text.trim().is_empty()` but counts `chars()` on the
           untrimmed text, in Unicode scalar values: so the blank check trims, the
           length check does not, and `Array.from` counts code points. */
    if (text.trim() === '' && !(draft.attachments?.length)) return false;
    if (Array.from(text).length > CONVERSATION_TEXT_MAX) {
      /* Shown back, but never recorded as sent: no request left the browser, so
         the key is untouched and the next press is not "the text changed". */
      amendDraft(draft, {
        text,
        error: `This message is too long — the limit is ${CONVERSATION_TEXT_MAX} characters.`,
        remedy: null,
      });
      return false;
    }
    if (draft.submitted !== null && draft.submitted.text !== text) {
      amendDraft(draft, { text, error: 'The previous submission is unconfirmed. Try again to recover it first.', remedy: 'retry' });
      return false;
    }
    if (refuseOfflineDraft(draft, text, answers)) return false;
    amendDraft(draft, { text, creating: true, error: null, remedy: null });
    createDraft(draft, text, null, answers);
    return true;
  };

  function handleCreateFailure(
    error: unknown,
    attempt: ConversationDraft,
    previouslySubmitted: SubmittedConversationInput | null,
  ): ConversationDraft {
    const failure = conversationCreateFailure(error instanceof ApiError ? error.failure : null);
    const message = failure.message;
    switch (failure.kind) {
      case 'gone':
        registry.discardDraft(attempt);
        onGone();
        return attempt;
      case 'exhausted':
        /* A spent key can never succeed again, so a new one is minted and takes
                   `submitted` with it: nothing was posted under this key. */
        return rekeyDraft(attempt, mintIdempotencyKey(), { error: message, remedy: 'retry' });
      case 'stale-payload':
        amendDraft(attempt, { error: message, remedy: 'new-conversation' });
        return attempt;
      case 'blocked':
        /* Nothing committed and the key is unspent, so both it and the words are kept. */
        if (attempt.attachments?.length && previouslySubmitted === null) {
          withDraft(attempt, current => ({ ...current, submitted: null, error: message, remedy: 'retry' }));
        } else amendDraft(attempt, { error: message, remedy: 'retry' });
        return attempt;
      case 'exists':
      case 'retry':
        // Replay the original POST to recover its authoritative session/input receipt.
        amendDraft(attempt, { error: message, remedy: 'retry' });
        return attempt;
    }
  }

  const sendAsNewConversation = () => {
    if (creating || draft === null || draft.text === null) return false;
    const text = draft.text;
    if (refuseOfflineDraft(draft, text)) return false;
    amendDraft(draft, { creating: true, error: null, remedy: null });
    /* An explicit new-conversation choice starts a fresh idempotent submission. */
    createDraft(draft, text, 'new-conversation');
    return true;
  };

  /* Retry means the same draft again: same key, same words. The boolean reports
     local takeover, not a dispatched/accepted POST or capability confirmation. */
  const retryDraft = () => {
    if (creating || draft === null || draft.text === null) return false;
    if (draft.submitted !== null) return sendDraft(draft.submitted.text);
    const text = draft.text;
    if (!onlineManager.isOnline()) return false;
    amendDraft(draft, { creating: true });
    void recheckManagedProviders().then(answers => {
      amendDraft(draft, { creating: false });
      sendDraft(text, answers);
    }).catch(() => {
      amendDraft(draft, { creating: false, error: 'Managed provider capabilities are unavailable. Reconnect before sending.', remedy: 'retry' });
    });
    return true;
  };

  /* A draft no request was ever made for has no identity worth keeping; one that
       was sent and failed keeps its key and words so the next attempt is a retry. */
  const closeDrawer = () => {
    closeView();
    if (draft !== null && draft.submitted === null) registry.discardDraft(draft);
  };

  return { start, startWithMessage, withDraft, sendDraft, retryDraft, sendAsNewConversation, closeDrawer };
}
