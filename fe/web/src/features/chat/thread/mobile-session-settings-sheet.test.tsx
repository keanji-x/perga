import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { FOLLOW_INSTALLATION_DEFAULT } from '../../../../../core/domain/conversation.ts';
import type { ModelGroup } from './model-selection.ts';
import { MobileSessionSettingsControl } from './mobile-session-settings-sheet.tsx';

afterEach(cleanup);

it('exposes Claude effort only after switching a Codex draft to Claude', () => {
  const groups: ModelGroup[] = [{
    provider: 'claude',
    availability: null,
    selectionProblem: null,
    catalog: {
      models: [],
      default: {
        model: 'claude-sonnet',
        reasoning_effort: 'high',
        supported_reasoning_efforts: [{ reasoning_effort: 'high', description: null }],
      },
      default_source: 'claude_cli',
      source: 'live',
      fetched_at_ms: 1,
    },
  }];
  const onChangeModel = vi.fn();
  const props = { groups, selection: FOLLOW_INSTALLATION_DEFAULT, onChangeModel };
  const { rerender } = render(<MobileSessionSettingsControl {...props} provider="codex" />);
  const trigger = screen.getByRole('button', { name: /^Session model and mode:/ });
  fireEvent.click(trigger);
  expect(screen.queryByRole('radiogroup', { name: 'Reasoning effort' })).toBeNull();
  expect(screen.getByRole('button', { name: 'Session model and mode: Default' })).toBe(trigger);
  expect(onChangeModel).not.toHaveBeenCalled();

  fireEvent.click(screen.getByRole('radio', { name: /^Default\s*Claude/ }));
  expect(onChangeModel).toHaveBeenCalledExactlyOnceWith(FOLLOW_INSTALLATION_DEFAULT, 'claude');
  rerender(<MobileSessionSettingsControl {...props} provider="claude" />);
  fireEvent.click(screen.getByRole('radio', { name: 'High' }));
  expect(onChangeModel).toHaveBeenLastCalledWith({ model: null, reasoning_effort: 'high' }, 'claude');
});

it('explains alternate providers before selection and preserves their model admission', () => {
  const groups: ModelGroup[] = [
    { provider: 'acp:opencode', catalog: null, availability: null, selectionProblem: null },
    { provider: 'claude', catalog: null, availability: null, selectionProblem: 'Sign in before sending.' },
    { provider: 'codex', catalog: null, selectionProblem: null, availability: {
      provider: 'codex', status: 'unavailable', reason: 'Account read failed.',
      authentication_method: null, authentication_notice: null, checked_at_ms: 1,
    } },
  ];
  const onChangeModel = vi.fn();
  render(<MobileSessionSettingsControl groups={groups} provider="acp:opencode" selection={FOLLOW_INSTALLATION_DEFAULT}
    onChangeModel={onChangeModel} />);
  fireEvent.click(screen.getByRole('button', { name: /^Session model and mode:/ }));

  expect(screen.queryAllByRole('note').map(note => note.textContent)).toEqual([
    'Claude · Sign in before sending.',
    'Codex · Account read failed.',
  ]);
  expect(screen.queryByRole('radio', { name: /^Default\s*Claude/ })).toBeNull();
  expect(screen.getByRole('radio', { name: /^Default\s*Codex/ }).hasAttribute('disabled')).toBe(false);
  expect(onChangeModel).not.toHaveBeenCalled();
});

it('opens the picker when the only content is a blocked alternate provider notice', () => {
  render(<MobileSessionSettingsControl provider="codex" selection={FOLLOW_INSTALLATION_DEFAULT} onChangeModel={vi.fn()}
    groups={[{ provider: 'claude', catalog: null, availability: null, selectionProblem: 'Sign in before sending.' }]} />);
  const trigger = screen.getByRole('button', { name: /^Session model and mode:/ });
  expect(trigger.hasAttribute('disabled')).toBe(false);
  fireEvent.click(trigger);
  expect(screen.getByRole('note').textContent).toBe('Sign in before sending.');
});
