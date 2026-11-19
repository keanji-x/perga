// Answering the Planner's questions (#2209). An open ask reaches the client only as an item of the
// kernel's activity overlay; the one write is `POST /api/tracks/{id}/asks/{ask_id}/answer`.

import { z } from 'zod';
import { askDtoSchema, askQuestionSchema } from '../api/schemas.js';
import { approvalDtoSchema, askQuestionDtoSchema, type ApprovalDto } from '../api/perga-v1.js';
import type { AskAction, AskAnswer } from '../api/generated/wire.js';
import type { ApiOperation } from '../api/types.js';
import type { ActivityItem, AskDelivery, AskId, AskQuestion } from './activity.js';
import { writeClassOf, writeFailureText, type FailureTable, type WriteClass, type WriteText } from './failure-class.js';

/**
 * One open ask as the questions drawer shows it: the answer route's id, its questions in order, and
 * where the answer goes.
 */
export type OpenAsk = Readonly<{ askId: AskId; questions: readonly AskQuestion[]; delivery: AskDelivery; executionId?: string; action?: AskAction }>;

function compareAskIds(left: AskId, right: AskId): number {
  if (typeof left === 'number' && typeof right === 'number') return left - right;
  const leftStr = String(left);
  const rightStr = String(right);
  return leftStr < rightStr ? -1 : leftStr > rightStr ? 1 : 0;
}

/**
 * The track's open asks: every `hold` ask first, since each one blocks the running turn (#2348), then the
 * `wake` asks; oldest first within each. The overlay lists items newest first; an ask's id is its
 * `ask.requested` event id, so ascending ids are the order the Planner asked in.
 */
export function openAsksOf(items: readonly ActivityItem[]): readonly OpenAsk[] {
  const asks: OpenAsk[] = [];
  for (const item of items) {
    if (item.source === 'ask') asks.push({ askId: item.askId, questions: item.questions, delivery: item.delivery,
      ...(item.action === undefined ? {} : { action: item.action }),
    });
  }
  const blocking = (ask: OpenAsk) => (ask.delivery === 'hold' ? 0 : 1);
  return asks.sort((left, right) => blocking(left) - blocking(right) || compareAskIds(left.askId, right.askId));
}

export function normalizeAskQuestions(raw: unknown): readonly z.infer<typeof askQuestionSchema>[] {
  const source = typeof raw === 'string'
    ? (() => { try { return JSON.parse(raw) as unknown; } catch { return []; } })()
    : raw;
  if (!Array.isArray(source)) return [];
  return source.flatMap((entry) => {
    const legacy = askQuestionSchema.safeParse(entry);
    if (legacy.success) return [legacy.data];
    const dto = askQuestionDtoSchema.safeParse(entry);
    if (dto.success) return [{ title: dto.data.question, options: [...dto.data.options] }];
    return [];
  });
}

export function pendingApprovalQuestions(approvals: readonly ApprovalDto[]): readonly OpenAsk[] {
  return approvals.map(approval => ({
    askId: approval.id, executionId: approval.executionId, delivery: 'hold',
    questions: approval.questions.map(question => ({ title: question.question, options: question.options })),
  }));
}

/**
 * Whether an ask takes one of its options only, with no field for the reader's own words: a `hold` ask's paused request
 * takes an option index (#2348), and an ask carrying an action is granted only by a click on its option (#2410).
 */
export function takesOptionsOnly(ask: OpenAsk): boolean {
  return ask.delivery === 'hold' || ask.action !== undefined;
}

/** What the reader has given for one question: the option picked (its index), and their own words. */
export type AskDraft = Readonly<{ choice: number | null; own: string }>;

/**
 * A fresh draft per question: the first option, the recommended one, is picked; a question without options has no
 * pick. A `hold` ask's options are an approval (#2348), so none is picked until the reader picks one.
 */
export function askDraftsFor(questions: readonly AskQuestion[], delivery: AskDelivery): readonly AskDraft[] {
  return questions.map((question) => ({ choice: delivery === 'wake' && question.options.length > 0 ? 0 : null, own: '' }));
}

/**
 * The `answers` body, one per question in order, or `null` while a question has none. Words of the
 * reader's own, once they are more than whitespace, win over the picked option and go as text,
 * trimmed as the server stores them; a picked option goes as its index, so clicking an option and
 * typing its label are different answers.
 */
export function askAnswers(questions: readonly AskQuestion[], drafts: readonly AskDraft[]): readonly AskAnswer[] | null {
  if (drafts.length !== questions.length) return null;
  const answers: AskAnswer[] = [];
  for (const [index, question] of questions.entries()) {
    const draft = drafts[index];
    const own = draft.own.trim();
    if (own !== '') answers.push({ text: own });
    else if (draft.choice !== null && draft.choice < question.options.length) answers.push({ option: draft.choice });
    else return null;
  }
  return answers;
}

/** The server's bound on one answer, in characters (Unicode scalar values, as Rust's `chars()` counts). */
export const ASK_ANSWER_MAX_CHARS = 2000;

/**
 * An answer as the field keeps it: at most {@link ASK_ANSWER_MAX_CHARS} characters, what `maxLength` would
 * do on a field that offers none. Counted in code points, as the server counts, never in UTF-16 units.
 */
export function clampAskAnswer(text: string): string {
  const characters = Array.from(text);
  return characters.length <= ASK_ANSWER_MAX_CHARS ? text : characters.slice(0, ASK_ANSWER_MAX_CHARS).join('');
}

/**
 * Answer every question of one ask via `POST /api/v1/tracks/{trackId}/asks/{askId}/answer`.
 * Accepts both Perga's `200 AskDto` and `204` responses; failures read through {@link ANSWER_ASK_FAILURES}.
 */
export function answerAskOperation(
  trackId: string,
  askId: AskId,
  answers: readonly AskAnswer[],
  questions?: readonly AskQuestion[],
  executionId?: string,
): ApiOperation<undefined> {
  const mappedAnswers = questions === undefined
    ? [...answers]
    : answers.map((answer, index) => {
      if ('text' in answer) return { ...answer, value: answer.text };
      const label = questions[index]?.options[answer.option];
      return label !== undefined ? { ...answer, value: label } : answer;
    });
  return {
    method: 'POST',
    path: executionId === undefined
      ? `/api/v1/tracks/${encodeURIComponent(trackId)}/asks/${encodeURIComponent(String(askId))}/answer`
      : `/api/v1/tracks/${encodeURIComponent(trackId)}/executions/${encodeURIComponent(executionId)}/approvals/${encodeURIComponent(String(askId))}/decision`,
    body: { answers: mappedAnswers },
    responseSchema: z.union([(executionId === undefined ? askDtoSchema : approvalDtoSchema).transform(() => undefined), z.undefined()]),
  };
}

/**
 * What a failed answer says. A 409 is an ask no longer open (answered in another tab, an earlier
 * attempt whose answer was lost, or a paused request that went away): it is no longer the reader's
 * to answer, so it is `done`; {@link answerAskFailureText} words a paused request's apart. 400 (a count, an option the question lacks, an empty or over-long
 * answer, or text for a paused request), 403, 404 (no such ask on this track) and the extractor's
 * 413, 415 and 422 store nothing; anything else may have stored it.
 */
export const ANSWER_ASK_FAILURES: FailureTable<WriteClass> = Object.freeze({
  rules: Object.freeze([
    Object.freeze({ status: Object.freeze([409]), is: 'done' as const }),
    Object.freeze({ status: Object.freeze([400, 403, 404, 413, 415, 422]), is: 'refused' as const }),
  ]),
  unauthorized: 'refused',
  otherwise: 'unknown',
});

export const ANSWER_ASK_TEXT: WriteText = Object.freeze({
  refused: 'Your answer was not sent.', unknown: 'Sending your answer is unconfirmed.',
});

/** A paused request's answer met 409: the request went away, so the click reached nothing. */
export const HOLD_ASK_GONE_TEXT = 'This request is no longer pending.';

/**
 * How the drawer reads a failed answer to an ask of `delivery`. A `wake` ask's 409 is `done`: it is no longer the
 * reader's to answer, and its row settles at once. A `hold` ask's 409 says {@link HOLD_ASK_GONE_TEXT} instead, since
 * nothing the reader chose was delivered; its row goes when the overlay drops it. Every other failure reads alike.
 */
export function answerAskFailureText(delivery: AskDelivery): (error: unknown) => string | null {
  const read = writeFailureText(ANSWER_ASK_FAILURES, ANSWER_ASK_TEXT);
  if (delivery === 'wake') return read;
  return (error) => writeClassOf(error, ANSWER_ASK_FAILURES) === 'done' ? HOLD_ASK_GONE_TEXT : read(error);
}
