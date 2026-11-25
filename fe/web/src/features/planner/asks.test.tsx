// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { OpenAsk } from '../../../../core/domain/ask.ts';
import { ApiError } from '../../../../core/domain/failure-class.ts';
import { PlannerAskDrawer, type AnswerAsk } from './asks.tsx';

afterEach(cleanup);
const TWO: OpenAsk = { askId: 7, delivery: 'wake', questions: [
  { title: 'Which branch?', options: ['main', 'release'] }, { title: 'Notes?', options: [] },
] };
const SINGLE: OpenAsk = { askId: 9, delivery: 'wake', questions: [{ title: 'Merge PR #12?', options: ['Merge', 'Hold'] }] };
const http = (status: number, message: string) => new ApiError({ kind: 'http', status, code: 'error', message });
function setup(asks: readonly OpenAsk[], onAnswer: AnswerAsk = vi.fn(() => Promise.resolve())) {
  const view = render(<PlannerAskDrawer asks={asks} onAnswer={onAnswer} />);
  return { ...view, onAnswer };
}

describe('PlannerAskDrawer', () => {
  it('renders nothing without open questions', () => {
    expect(setup([]).container.innerHTML).toBe('');
  });
  it('uses Astryx’s questions drawer and its collapse disclosure', async () => {
    const { container } = setup([TWO]);
    expect(container.firstElementChild?.className).toContain('astryx-chat-composer-drawer');
    const toggle = screen.getByRole('button', { name: 'Collapse Questions' });
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    await userEvent.click(toggle);
    expect(screen.getByRole('button', { name: 'Expand Questions' }).getAttribute('aria-expanded')).toBe('false');
  });
  it('answers a single choice directly from the conversation card', async () => {
    const { onAnswer } = setup([SINGLE]);
    await userEvent.click(screen.getByRole('button', { name: 'Hold' }));
    await waitFor(() => expect(onAnswer).toHaveBeenCalledWith(9, [{ option: 1 }]));
    expect(screen.queryByRole('group', { name: 'The Planner asks' })).toBeNull();
  });
  it('commits all answers in order only at the last question', async () => {
    const { onAnswer } = setup([TWO]);
    expect(screen.getByRole('heading', { name: 'Which branch?' })).toBeTruthy();
    expect(screen.queryByRole('textbox', { name: 'Notes?' })).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'release' }));
    expect(onAnswer).not.toHaveBeenCalled();
    await userEvent.type(screen.getByRole('textbox', { name: 'Notes?' }), ' Ship it ');
    await userEvent.click(screen.getByRole('button', { name: 'Answer' }));
    await waitFor(() => expect(onAnswer).toHaveBeenCalledWith(7, [{ option: 1 }, { text: 'Ship it' }]));
  });
  it('marks the chosen option when returning, and clears it for a custom answer', async () => {
    setup([TWO]);
    await userEvent.click(screen.getByRole('button', { name: 'release' }));
    await userEvent.click(screen.getByRole('button', { name: 'Previous question' }));
    expect(screen.getByRole('button', { name: 'release' }).getAttribute('aria-pressed')).toBe('true');
    expect(screen.getByRole('button', { name: 'main' }).getAttribute('aria-pressed')).toBe('false');
    await userEvent.click(screen.getByRole('button', { name: 'Other…' }));
    await userEvent.type(screen.getByRole('textbox', { name: 'Which branch?' }), 'custom branch');
    expect(screen.getByRole('button', { name: 'release' }).getAttribute('aria-pressed')).toBe('false');
  });
  it('preserves drafts on return to an earlier question', async () => {
    const { onAnswer } = setup([TWO]);
    await userEvent.click(screen.getByRole('button', { name: 'release' }));
    await userEvent.type(screen.getByRole('textbox', { name: 'Notes?' }), 'Keep these notes');
    await userEvent.click(screen.getByRole('button', { name: 'Previous question' }));
    await userEvent.click(screen.getByRole('button', { name: 'main' }));
    expect(screen.getByRole<HTMLInputElement>('textbox', { name: 'Notes?' }).value).toBe('Keep these notes');
    await userEvent.click(screen.getByRole('button', { name: 'Answer' }));
    await waitFor(() => expect(onAnswer).toHaveBeenCalledWith(7, [{ option: 0 }, { text: 'Keep these notes' }]));
  });
  it('accepts the reader’s own words for an option question', async () => {
    const { onAnswer } = setup([SINGLE]);
    expect(screen.queryByRole('textbox', { name: 'Merge PR #12?' })).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Other…' }));
    await userEvent.type(screen.getByRole('textbox', { name: 'Merge PR #12?' }), 'Wait for CI');
    await userEvent.click(screen.getByRole('button', { name: 'Answer' }));
    await waitFor(() => expect(onAnswer).toHaveBeenCalledWith(9, [{ text: 'Wait for CI' }]));
  });
  it('submits a custom answer with Enter', async () => {
    const { onAnswer } = setup([SINGLE]);
    await userEvent.click(screen.getByRole('button', { name: 'Other…' }));
    await userEvent.type(screen.getByRole('textbox', { name: 'Merge PR #12?' }), ' Wait for CI {Enter}');
    await waitFor(() => expect(onAnswer).toHaveBeenCalledExactlyOnceWith(9, [{ text: 'Wait for CI' }]));
    expect(screen.queryByRole('group', { name: 'The Planner asks' })).toBeNull();
  });
  it('advances custom answers with Enter and sends only after the last question', async () => {
    const { onAnswer } = setup([TWO]);
    await userEvent.click(screen.getByRole('button', { name: 'Other…' }));
    await userEvent.type(screen.getByRole('textbox', { name: 'Which branch?' }), 'hotfix{Enter}');
    expect(onAnswer).not.toHaveBeenCalled();
    const notes = screen.getByRole('textbox', { name: 'Notes?' });
    expect(document.activeElement).toBe(notes);
    await userEvent.type(notes, 'Ship it{Enter}');
    await waitFor(() => expect(onAnswer).toHaveBeenCalledExactlyOnceWith(7, [{ text: 'hotfix' }, { text: 'Ship it' }]));
  });
  it.each([{ isComposing: true }, { keyCode: 229 }])('does not submit an IME confirmation (%j)', async ime => {
    const { onAnswer } = setup([SINGLE]);
    fireEvent.click(screen.getByRole('button', { name: 'Other…' }));
    const input = screen.getByRole<HTMLInputElement>('textbox', { name: 'Merge PR #12?' });
    fireEvent.change(input, { target: { value: '等检查完成' } });
    fireEvent.keyDown(input, { key: 'Enter', ...ime });
    expect(onAnswer).not.toHaveBeenCalled();
    expect(input.value).toBe('等检查完成');
    fireEvent.keyDown(input, { key: 'Enter' });
    await waitFor(() => expect(onAnswer).toHaveBeenCalledExactlyOnceWith(9, [{ text: '等检查完成' }]));
  });
  it('ignores blank Enter and repeated Enter while an answer is pending', async () => {
    let finish!: () => void;
    const onAnswer = vi.fn<AnswerAsk>(() => new Promise<void>(resolve => { finish = resolve; }));
    setup([SINGLE], onAnswer);
    fireEvent.click(screen.getByRole('button', { name: 'Other…' }));
    const input = screen.getByRole('textbox', { name: 'Merge PR #12?' });
    fireEvent.change(input, { target: { value: '   ' } });
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(onAnswer).not.toHaveBeenCalled();
    fireEvent.change(input, { target: { value: 'Wait for CI' } });
    fireEvent.keyDown(input, { key: 'Enter' });
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(onAnswer).toHaveBeenCalledExactlyOnceWith(9, [{ text: 'Wait for CI' }]);
    finish();
    await waitFor(() => expect(screen.queryByRole('group', { name: 'The Planner asks' })).toBeNull());
  });
  it('rejects blank free answers and clamps the server’s character limit', () => {
    setup([SINGLE]);
    fireEvent.click(screen.getByRole('button', { name: 'Other…' }));
    const input = screen.getByRole<HTMLInputElement>('textbox', { name: 'Merge PR #12?' });
    fireEvent.change(input, { target: { value: '   ' } });
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Answer' }).getAttribute('aria-disabled')).toBe('true');
    fireEvent.change(input, { target: { value: 'x'.repeat(2001) } });
    expect(input.value).toHaveLength(2000);
  });
  it('preserves the earlier group’s draft and position across group switches', async () => {
    setup([TWO, SINGLE]);
    await userEvent.click(screen.getByRole('button', { name: 'release' }));
    await userEvent.type(screen.getByRole('textbox', { name: 'Notes?' }), 'Draft');
    await userEvent.click(screen.getByRole('button', { name: '1 more ask' }));
    await userEvent.click(screen.getByRole('button', { name: 'Merge PR #12?' }));
    expect(screen.getByRole('heading', { name: 'Merge PR #12?' })).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: '1 more ask' }));
    await userEvent.click(screen.getByRole('button', { name: 'Which branch?' }));
    expect(screen.getByRole<HTMLInputElement>('textbox', { name: 'Notes?' }).value).toBe('Draft');
  });
  it('settles a confirmed ask before a stale overlay catches up', async () => {
    const { rerender, onAnswer } = setup([SINGLE, TWO]);
    await userEvent.click(screen.getByRole('button', { name: 'Hold' }));
    await screen.findByRole('heading', { name: 'Which branch?' });
    rerender(<PlannerAskDrawer asks={[SINGLE, TWO]} onAnswer={onAnswer} />);
    expect(screen.queryByRole('heading', { name: 'Merge PR #12?' })).toBeNull();
  });
  it('settles a 409 without inventing a local answer receipt', async () => {
    setup([SINGLE], () => Promise.reject(http(409, 'already answered')));
    await userEvent.click(screen.getByRole('button', { name: 'Hold' }));
    await waitFor(() => expect(screen.queryByRole('group', { name: 'The Planner asks' })).toBeNull());
    expect(screen.queryByRole('status', { name: 'Submitted Planner answers' })).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
  });
  it.each([400, 404])('keeps the question after a refusal (%i)', async status => {
    setup([SINGLE], () => Promise.reject(http(status, 'Not permitted')));
    await userEvent.click(screen.getByRole('button', { name: 'Hold' }));
    expect(await screen.findByText('Not permitted')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Merge PR #12?' })).toBeTruthy();
    expect(screen.queryByRole('status', { name: 'Submitted Planner answers' })).toBeNull();
  });
  it('keeps uncertain writes retryable and settles a 409 retry', async () => {
    const onAnswer = vi.fn<AnswerAsk>().mockRejectedValueOnce(new ApiError({ kind: 'transport', message: 'dropped' }))
      .mockRejectedValueOnce(http(409, 'already answered'));
    setup([SINGLE], onAnswer);
    await userEvent.click(screen.getByRole('button', { name: 'Hold' }));
    expect(await screen.findByText('Sending your answer is unconfirmed.')).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'Hold' }));
    await waitFor(() => expect(screen.queryByRole('group', { name: 'The Planner asks' })).toBeNull());
    expect(onAnswer).toHaveBeenCalledTimes(2);
  });
  it('locks every choice and group switch during a write', async () => {
    let finish!: () => void;
    const onAnswer = vi.fn<AnswerAsk>(() => new Promise<void>(resolve => { finish = resolve; }));
    setup([SINGLE, TWO], onAnswer);
    const hold = screen.getByRole('button', { name: 'Hold' });
    fireEvent.click(hold); fireEvent.click(hold);
    fireEvent.click(screen.getByRole('button', { name: 'Merge' }));
    expect(onAnswer).toHaveBeenCalledOnce();
    const switcher = screen.getByRole<HTMLButtonElement>('button', { name: '1 more ask' });
    expect(switcher.disabled || switcher.getAttribute('aria-disabled') === 'true').toBe(true);
    finish();
    await screen.findByRole('heading', { name: 'Which branch?' });
  });
  it('wraps multi-option choices in a bounded scrollable list and steps through 4 questions without unrolling all questions', async () => {
    const FOUR_BY_SIX: OpenAsk = {
      askId: 44,
      delivery: 'wake',
      questions: [
        { title: 'Q1: Primary goal?', options: ['A1', 'A2', 'A3', 'A4', 'A5', 'A6'] },
        { title: 'Q2: Target layer?', options: ['B1', 'B2', 'B3', 'B4', 'B5', 'B6'] },
        { title: 'Q3: Verification scope?', options: ['C1', 'C2', 'C3', 'C4', 'C5', 'C6'] },
        { title: 'Q4: Rollout mode?', options: ['D1', 'D2', 'D3', 'D4', 'D5', 'D6'] },
      ],
    };
    const { container, onAnswer } = setup([FOUR_BY_SIX]);
    expect(screen.getByText('1 / 4')).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Q2: Target layer?' })).toBeNull();
    const optionList = container.querySelector('[data-nc-ask-options]');
    expect(optionList).toBeTruthy();
    expect(optionList?.querySelectorAll('button')).toHaveLength(6);
    await userEvent.click(screen.getByRole('button', { name: 'A5' }));
    expect(screen.getByText('2 / 4')).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'B4' }));
    expect(screen.getByText('3 / 4')).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'C6' }));
    expect(screen.getByText('4 / 4')).toBeTruthy();
    expect(onAnswer).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole('button', { name: 'D2' }));
    await waitFor(() => expect(onAnswer).toHaveBeenCalledWith(44, [
      { option: 4 }, { option: 3 }, { option: 5 }, { option: 1 },
    ]));
  });
});

it('offers only the two explicit choices for a lifecycle question, with no ambiguous own-answer input', () => {
  const ask = { ...SINGLE, action: { kind: 'reopen_track' as const, closed_at: 42 },
    questions: [{ title: 'Continue this closed track?', options: ['Reopen and continue', 'Keep closed'] }] };
  setup([ask]);
  expect(screen.queryByRole('textbox', { name: 'Continue this closed track?' })).toBeNull();
  expect(screen.queryByRole('button', { name: 'Other…' })).toBeNull();
  expect(screen.getByRole('button', { name: 'Reopen and continue' })).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Keep closed' })).toBeTruthy();
});
