// The Planner's questions use Astryx’s drawer above the message field.
import { useLayoutEffect, useRef } from 'react';
import { Button } from '@astryxdesign/core/Button';
import { PendingInputDrawer } from './pending-input-drawer.tsx';
import { HStack } from '@astryxdesign/core/HStack';
import { Icon as AstryxIcon } from '@astryxdesign/core/Icon';
import { TextInput } from '@astryxdesign/core/TextInput';

import type { AskId } from '../../../../core/domain/activity.ts';
import {
  answerAskFailureText, askAnswers, askDraftsFor, clampAskAnswer, takesOptionsOnly, type AskDraft, type OpenAsk,
} from '../../../../core/domain/ask.ts';
import type { AskAnswer } from '../../../../core/domain/presentation-types.ts';
import { OperationFeedback, useOperationFeedback } from '../../ui/operation-feedback/public.tsx';
import { Icon } from '../../ui/icon/public.tsx';
import { useState } from '../../ui/state/public.ts';
import styles from './asks.module.css';

export type AnswerAsk = (askId: AskId, answers: readonly AskAnswer[]) => Promise<void>;
export type PlannerAskDrawerProps = Readonly<{ asks: readonly OpenAsk[]; onAnswer: AnswerAsk }>;
type DraftState = Readonly<{ drafts: readonly AskDraft[]; page: number }>;

/** The vendor owns the surface and disclosure; this feature owns question order and answers. */
export function PlannerAskDrawer({ asks, onAnswer }: PlannerAskDrawerProps) {
  const [states, setStates] = useState<Readonly<Record<string, DraftState>>>({});
  return <PendingInputDrawer items={asks} idOf={ask => String(ask.askId)} titleOf={ask => ask.questions[0].title}
    label="Questions" noun="ask" queueLabel="Other Planner asks" countOf={ask => ask.questions.length}>
    {(current, onBusy, onSettled) => {
      const key = String(current.askId);
      return <AskForm key={key} ask={current} onAnswer={onAnswer}
        state={states[key] ?? { drafts: askDraftsFor(current.questions), page: 0 }}
        onChange={state => setStates(previous => ({ ...previous, [key]: state }))}
        onBusy={onBusy} onSettled={onSettled} />;
    }}
  </PendingInputDrawer>;
}

function AskForm({ ask, state, onChange, onAnswer, onBusy, onSettled }: {
  ask: OpenAsk;
  state: DraftState;
  onChange: (state: DraftState) => void;
  onAnswer: AnswerAsk;
  onBusy: (busy: boolean) => void;
  onSettled: () => void;
}) {
  const feedback = useOperationFeedback();
  const [busy, setBusy] = useState(false);
  const [ownExpandedPage, setOwnExpandedPage] = useState<number | null>(null);
  const lock = useRef(false);
  const { drafts, page } = state;
  const question = ask.questions[page];
  const draft = drafts[page];
  const showOwnInput = !takesOptionsOnly(ask)
    && (question.options.length === 0 || draft.own !== '' || ownExpandedPage === page);
  const showOwnToggle = !takesOptionsOnly(ask) && question.options.length > 0 && !showOwnInput;
  const firstOption = useRef<HTMLButtonElement | null>(null);
  const ownInput = useRef<HTMLInputElement | null>(null);
  const previousPage = useRef(page);
  useLayoutEffect(() => {
    if (previousPage.current === page) return;
    previousPage.current = page;
    (question.options.length === 0 ? ownInput.current : firstOption.current)?.focus();
  }, [page, question.options.length]);
  useLayoutEffect(() => {
    if (ownExpandedPage === page) ownInput.current?.focus();
  }, [ownExpandedPage, page]);
  const send = async (next: readonly AskDraft[]) => {
    const answers = askAnswers(ask.questions, next);
    if (answers === null || lock.current) return;
    // This form owns the write lifecycle; option onClick keeps its shared lock outside Astryx’s async transition.
    lock.current = true;
    setBusy(true);
    onBusy(true);
    try {
      const success = await feedback.run(onAnswer(ask.askId, answers), answerAskFailureText());
      if (success) onSettled();
    } finally {
      lock.current = false;
      setBusy(false);
      onBusy(false);
    }
  };
  const advance = async (next: readonly AskDraft[]) => {
    if (lock.current) return;
    onChange({ drafts: next, page });
    feedback.clear();
    if (page < ask.questions.length - 1) {
      onChange({ drafts: next, page: page + 1 });
    } else await send(next);
  };
  const ownAnswer = () => {
    const answer = draft.own.trim();
    if (answer === '' || lock.current) return;
    return advance(drafts.map((item, index) => index === page ? { ...item, own: answer } : item));
  };
  return <section role="group" aria-label="The Planner asks" className={styles.ask} data-nc-ask={ask.askId}>
    {ask.questions.length > 1 && <HStack justify="between" align="center" className={styles.meta}>
      <span className={styles.stepBadge}>{page + 1} / {ask.questions.length}</span>
      {page > 0 && <Button label="Previous question" tooltip="Previous question" icon={<Icon name="arrow-left" size="sm" />} isIconOnly size="sm" variant="secondary" className={styles.back}
        isDisabled={busy} onClick={() => { feedback.clear(); onChange({ drafts, page: page - 1 }); }} />}
    </HStack>}
    <h3 className={styles.title}>{question.title}</h3>
    {question.detail !== undefined && <pre className={styles.detail} aria-label="Question details">{question.detail}</pre>}
    <div className={styles.questions} data-nc-ask-questions="">
      {question.options.length > 0 && <div className={styles.optionList} data-nc-ask-options="">
        {question.options.map((option, index) => <Button key={index} ref={index === 0 ? firstOption : undefined} label={option} variant="secondary"
          className={`${styles.option} ${draft.choice === index && draft.own.trim() === '' ? styles.optionSelected : ''}`}
          aria-pressed={draft.choice === index && draft.own.trim() === ''} isDisabled={busy}
          onClick={() => { setOwnExpandedPage(null); void advance(drafts.map((item, at) => at === page ? { choice: index, own: '' } : item)); }}>
          <span className={styles.optionContent}>
            <span className={styles.optionLabel}>{option}</span>
            {draft.choice === index && draft.own.trim() === ''
              ? <AstryxIcon icon="check" size="sm" color="secondary" />
              : <span className={styles.optionHint} aria-hidden="true">↵</span>}
          </span>
        </Button>)}
      </div>}
      {showOwnToggle && <button type="button" className={styles.customToggle} disabled={busy}
        onClick={() => setOwnExpandedPage(page)}>
        Other…
      </button>}
      {showOwnInput && <div className={styles.own} role="group" aria-label="Your answer">
        <TextInput ref={ownInput} label={question.title} isLabelHidden
          className={styles.answerInput} placeholder={question.options.length > 0 ? 'Or your own answer…' : 'Your answer…'} size="sm"
          value={draft.own} isDisabled={busy}
          onEnter={() => { void ownAnswer(); }}
          onChange={own => onChange({ drafts: drafts.map((item, index) => index === page
            ? { ...item, own: clampAskAnswer(own) } : item), page })} />
        <Button label={ask.questions.length > 1 && page < ask.questions.length - 1 ? 'Next question' : 'Answer'}
          icon={<Icon name="arrow-up" size="sm" />} isIconOnly variant="secondary" size="sm" className={styles.answerButton}
          isDisabled={busy || draft.own.trim() === ''} onClick={() => { void ownAnswer(); }} />
      </div>}
    </div>
    {busy && <span className={styles.meta} role="status">Sending answer…</span>}
    <OperationFeedback feedback={feedback} />
  </section>;
}
