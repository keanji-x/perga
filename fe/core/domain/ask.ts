// Answering the Planner's durable questions (`perga_user_ask`).
// Live execution-scoped approvals belong to `approval.ts`, never here.

import { z } from 'zod';
import { askDtoSchema, askQuestionSchema } from '../api/schemas.js';
import { askQuestionDtoSchema } from '../api/perga-v1.js';
import type { AskAction, AskAnswer } from '../api/generated/wire.js';
import type { ApiOperation } from '../api/types.js';
import type { ActivityItem, AskDelivery, AskId, AskQuestion } from './activity.js';
import { writeFailureText, type FailureTable, type WriteClass, type WriteText } from './failure-class.js';

/**
 * One open durable ask as the questions drawer shows it: the answer route's id, its questions in order, and
 * where the answer goes.
 */
export type OpenAsk = Readonly<{ askId: AskId; questions: readonly AskQuestion[]; delivery: AskDelivery; action?: AskAction }>;

function compareAskIds(left: AskId, right: AskId): number {
  if (typeof left === 'number' && typeof right === 'number') return left - right;
  const leftStr = String(left);
  const rightStr = String(right);
  return leftStr < rightStr ? -1 : leftStr > rightStr ? 1 : 0;
}

/**
 * The track's open asks: oldest first.
 */
export function openAsksOf(items: readonly ActivityItem[]): readonly OpenAsk[] {
  const asks: OpenAsk[] = [];
  for (const item of items) {
    if (item.source === 'ask') {
      asks.push({
        askId: item.askId,
        questions: item.questions,
        delivery: item.delivery,
        ...(item.action === undefined ? {} : { action: item.action }),
      });
    }
  }
  return asks.sort((left, right) => compareAskIds(left.askId, right.askId));
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

/** Whether an ask takes one of its options only, with no free-text input field. */
export function takesOptionsOnly(ask: OpenAsk): boolean {
  return ask.action !== undefined;
}

/** What the reader has given for one question: the option picked (its index), and their own words. */
export type AskDraft = Readonly<{ choice: number | null; own: string }>;

/**
 * A fresh draft per question: the first option, the recommended one, is picked; a question without options has no
 * pick.
 */
export function askDraftsFor(questions: readonly AskQuestion[]): readonly AskDraft[] {
  return questions.map((question) => ({ choice: question.options.length > 0 ? 0 : null, own: '' }));
}

/**
 * The `answers` body, one per question in order, or `null` while a question has none.
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

/** The server's bound on one answer, in Unicode code points. */
export const ASK_ANSWER_MAX_CHARS = 2000;

export function clampAskAnswer(text: string): string {
  const characters = Array.from(text);
  return characters.length <= ASK_ANSWER_MAX_CHARS ? text : characters.slice(0, ASK_ANSWER_MAX_CHARS).join('');
}

/**
 * Answer every question of one durable ask via `POST /api/v1/tracks/{trackId}/asks/{askId}/answer`.
 */
export function answerAskOperation(
  trackId: string,
  askId: AskId,
  answers: readonly AskAnswer[],
  questions?: readonly AskQuestion[],
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
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}/asks/${encodeURIComponent(String(askId))}/answer`,
    body: { answers: mappedAnswers },
    responseSchema: z.union([askDtoSchema.transform(() => undefined), z.undefined()]),
  };
}

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

export function answerAskFailureText(): (error: unknown) => string | null {
  return writeFailureText(ANSWER_ASK_FAILURES, ANSWER_ASK_TEXT);
}
