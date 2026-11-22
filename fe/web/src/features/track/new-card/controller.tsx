import { Button } from '@astryxdesign/core/Button';
import { useEffect, useMemo, useRef } from 'react';
import { cardAddMenuEntries, type CardRegistry, type CardAddMenuEntry } from '../../../systems/cards/public.ts';
import { Dialog, type DialogOrigin } from '../../../ui/dialog/public.tsx';
import { OperationFeedback, useOperationFeedback } from '../../../ui/operation-feedback/public.tsx';
import { useState } from '../../../ui/state/public.ts';
import { cardCreateEnded, cardCreateFailureText, keyedCardBodyOf, sameCardDraft, sendCardCreate,
 type CardCreatePort, type CardDraft, type KeyedCardBody } from './create.ts';
import { AddCardMenu, NewCardForm, type NewCardValues } from './public.tsx';

type KeyedRequest<Draft, Body> = Readonly<{ key: string; draft: Draft; body: Body }>;
/** App supplies the shared intent holder; this feature owns its creation policy and UI lifecycle. */
export type CardCreateIntent = Readonly<{
 held: KeyedRequest<CardDraft, KeyedCardBody> | null;
 request: (draft: CardDraft, build: () => KeyedCardBody) => KeyedRequest<CardDraft, KeyedCardBody>;
 release: (request: KeyedRequest<CardDraft, KeyedCardBody>) => void;
}>;

export function useNewTrackCard({ registry, cardCreatePort, cardIntent, directory, onCreated, compactViewport, newTerminalSession }: {
 registry: CardRegistry;
 cardCreatePort: CardCreatePort;
 cardIntent: CardCreateIntent;
 directory: Parameters<typeof NewCardForm>[0]['listDirectory'];
 onCreated: (cardId: string) => void;
 compactViewport: boolean;
 newTerminalSession: (trigger: HTMLButtonElement | null) => void;
}) {
  const addMenuEntries = useMemo(() => {
    const entries = cardAddMenuEntries(registry);
    return entries.filter((entry) => entry.type === 'terminal');
  }, [registry]);
  const listDirectory = directory;
  const [cardDraft, setCardDraft] = useState<(CardAddMenuEntry & { origin: DialogOrigin }) | null>(null);
  const [creatingCard, setCreatingCard] = useState(false);
  /* A failed create has to be sayable with no dialog on screen: a fieldless kind
   * never opens one, so the message gets a route-level surface. */
  const cardCreateFeedback = useOperationFeedback();
  const newCardFieldRef = useRef<HTMLInputElement | null>(null);
  /* A create that lands after the reader has left must not steer them: one
   * `AbortController` per attempt, aborted on unmount, read before the navigation
   * and every state write. */
  const activeCardCreate = useRef<AbortController | null>(null);
  useEffect(() => () => { activeCardCreate.current?.abort(); }, []);

  /* The create navigates to the new card, the same landing `onOpenCard` gives. `resent`: the held request goes again. */
  const runCardCreate = (draft: CardDraft, keyed: KeyedRequest<CardDraft, KeyedCardBody> | null, resent: boolean) => {
    /* Only the newest gesture may own the landing: a superseded attempt is aborted so
           it neither steers nor clears a busy state that now belongs to the newer attempt. */
    activeCardCreate.current?.abort();
    const controller = new AbortController();
    activeCardCreate.current = controller;
    setCreatingCard(true);
    void cardCreateFeedback
      .run(
        Promise.resolve().then(() => sendCardCreate(cardCreatePort, draft, keyed)).then((card) => {
          if (keyed !== null) cardIntent.release(keyed);
          if (controller.signal.aborted) return;
          setCardDraft(null);
          onCreated(card.id);
        }, (error: unknown) => {
          if (keyed !== null && cardCreateEnded(error, resent)) cardIntent.release(keyed);
          throw error;
        }),
        cardCreateFailureText(draft.entry.label, resent),
        () => controller.signal.aborted,
      )
      .finally(() => {
        /* Only the attempt that still owns the busy state may clear it; both ways of
                   losing ownership abort, so `aborted` is the whole test. */
        if (controller.signal.aborted) return;
        activeCardCreate.current = null;
        setCreatingCard(false);
      });
  };

  const submitNewCard = (entry: CardAddMenuEntry, values: NewCardValues) => {
    const draft: CardDraft = { entry, values };
    // Build once per intent; a retry keeps the first submitted body.
    const body = keyedCardBodyOf(draft);
    if (body === null) { runCardCreate(draft, null, false); return; }
    const resent = cardIntent.held !== null && sameCardDraft(cardIntent.held.draft, draft);
    runCardCreate(draft, cardIntent.request(draft, () => body), resent);
  };
  const heldCardCreate = cardIntent.held;
  const retryCardCreate = heldCardCreate === null || creatingCard ? null
    : () => { runCardCreate(heldCardCreate.draft, heldCardCreate, true); };

  /* A kind with nothing to ask is created on the spot; one with fields opens the form. */
  const pickCardKind = (entry: CardAddMenuEntry, trigger: HTMLButtonElement | null, origin: DialogOrigin) => {
    cardCreateFeedback.clear();
    if (!compactViewport && entry.type === 'terminal') { newTerminalSession(trigger); return; }
    if (entry.fields.length === 0) submitNewCard(entry, {});
    else setCardDraft({ ...entry, origin });
  };
  return {
    menu: <AddCardMenu entries={addMenuEntries} onSelect={pickCardKind} label="Tools" icon="more" />,
    surfaces: <>
    {/* Keyed by kind: switching kinds is a different form, and a shared mount
        would carry the previous kind's typed values into it. */}
    <Dialog purpose="form" origin={cardDraft?.origin ?? { kind: 'surface-center' }}
      open={cardDraft !== null}
      onClose={() => setCardDraft(null)}
      title={cardDraft === null ? '' : `New ${cardDraft.label} card`}
      initialFocusRef={newCardFieldRef}
    >
      {cardDraft !== null && (
        <NewCardForm
          key={cardDraft.type}
          entry={cardDraft}
          submitting={creatingCard}
          error={cardCreateFeedback.error}
          onRetry={retryCardCreate}
          listDirectory={listDirectory}
          firstFieldRef={newCardFieldRef}
          onCancel={() => setCardDraft(null)}
          onSubmit={(values) => submitNewCard(cardDraft, values)}
        />
      )}
    </Dialog>
    {/* Only while the dialog is closed: `NewCardForm` renders the same `error` inline. */}
    {cardDraft === null && <OperationFeedback feedback={cardCreateFeedback}
      action={retryCardCreate === null ? undefined : <Button label="Try again" variant="ghost" onClick={retryCardCreate} />} />}
    </>,
  };
}
