import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { LanguageDescription } from '@codemirror/language';
import { afterEach, expect, it, vi } from 'vitest';
import { RecipeBodyEditor } from './body-editor.tsx';

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

it('keeps the body editable when Markdown support cannot load', async () => {
  const load = vi.spyOn(LanguageDescription.prototype, 'load').mockRejectedValue(new Error('Language chunk unavailable'));
  const changed = vi.fn();
  render(<RecipeBodyEditor id="recipe-body" value="Original body" theme="light" label="Recipe body" readOnly={false} onChange={changed} />);
  await waitFor(() => expect(load).toHaveBeenCalled());
  const editor = screen.getByRole('textbox', { name: 'Recipe body' });
  expect(editor.textContent).toBe('Original body');
  await userEvent.click(editor);
  await userEvent.keyboard('{Control>}{End}{/Control} continued');
  expect(changed.mock.calls.at(-1)?.[0]).toBe('Original body continued');
});
