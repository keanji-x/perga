// Exercise the app's composition of the Area form and Chat model control
// with the production styles and a real narrow-screen browser layout.
import '../../styles/entry.css';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { expect, it, vi } from 'vitest';
import { page } from 'vitest/browser';
import { FOLLOW_INSTALLATION_DEFAULT, type ModelSelection } from '../../../../core/domain/conversation.ts';
import { type ModelCatalog } from '../../../../core/domain/conversation-models.ts';
import type { ListDirectory } from '../../ui/directory-browser/public.tsx';
import { NewTrackForm } from '../../features/area/new-track/public.tsx';
import { ModelPill } from '../../features/chat/thread/model-pill.tsx';
import { useState } from '../../ui/state/public.ts';

const OVERFLOW_CWD = '/srv/a-long-folder-for-the-overflow-layout';
const OVERFLOW_FOLDER_LABEL = `Repo: a-long-folder-for-the-overflow-layout (${OVERFLOW_CWD})`;

function ModelForm({
  onChange = () => undefined, submitting = false, locked = false, initialCwd = null,
  modelName = 'Test model',
  listDirectory = (path) => Promise.resolve({ path: path ?? '/', parent: null, entries: [] }),
}: Readonly<{
  onChange?: (selection: ModelSelection) => void;
  submitting?: boolean;
  locked?: boolean;
  initialCwd?: string | null;
  modelName?: string;
  listDirectory?: ListDirectory;
}>) {
  const [selection, setSelection] = useState<ModelSelection>(FOLLOW_INSTALLATION_DEFAULT);
  const catalog: ModelCatalog = {
    models: [{ id: 'test-model', model: 'test-model', resolved_model: null, display_name: modelName, description: '',
      is_default: true, default_reasoning_effort: 'low', supported_reasoning_efforts: [
        { reasoning_effort: 'low', description: 'Faster' },
        { reasoning_effort: 'high', description: 'More reasoning' },
      ] }],
    default: { model: null, reasoning_effort: null, supported_reasoning_efforts: null }, default_source: 'unknown',
    source: 'live', fetched_at_ms: 1,
  };
  return <NewTrackForm
    area={{ id: 'work', name: 'Work' }}
    submitting={submitting} locked={locked} error={null} initialCwd={initialCwd}
    listDirectory={listDirectory} onSubmit={vi.fn()}
    modelControls={<ModelPill provider="codex" groups={[{ provider: 'codex', catalog, availability: null }]} selection={selection} effortControl="in-menu" onChange={(next) => { setSelection(next); onChange(next); }} />}
  />;
}

function expectFullLabel(name: string) {
  const label = [...screen.getByRole('button', { name }).querySelectorAll('span')]
    .find((element) => getComputedStyle(element).textOverflow === 'ellipsis');
  if (label === undefined) throw new Error(`Missing visible label for ${name}`);
  expect(label.scrollWidth).toBeLessThanOrEqual(label.clientWidth);
}

it('keeps phone preferences compact while selecting model and effort in one menu', async () => {
  await page.viewport(390, 844);
  const onChange = vi.fn();
  const view = render(<ModelForm onChange={onChange} />);
  try {
    await userEvent.click(screen.getByRole('button', { name: /^Model:/ }));
    await userEvent.click(screen.getByRole('radio', { name: 'Test model' }));
    expect(onChange).toHaveBeenLastCalledWith({ model: 'test-model', reasoning_effort: null });
    // Picking a model with reasoning efforts automatically transitions into the Effort view.
    const slider = within(await screen.findByRole('dialog')).getByRole('slider', { name: 'Reasoning effort' });
    slider.focus();
    await userEvent.keyboard('{End}');
    expect(onChange).toHaveBeenLastCalledWith({ model: 'test-model', reasoning_effort: 'high' });
    await userEvent.keyboard('{Escape}');
    expect(screen.queryByRole('button', { name: /^Reasoning effort:/ })).toBeNull();
    expect(screen.getAllByRole('button')).toHaveLength(3); // Options, Model and Create track.
    for (const [width, height] of [[390, 844], [320, 700]]) {
      await page.viewport(width, height);
      expect(await screen.findByRole('button', { name: 'Default folder' })).toBeTruthy();
      const model = screen.getByRole('button', { name: /^Model: Test model/ }).getBoundingClientRect();
      const send = screen.getByRole('button', { name: 'Create track' }).getBoundingClientRect();
      expect(Math.abs(model.top + model.height / 2 - send.top - send.height / 2)).toBeLessThan(2);
      expect(model.right).toBeLessThanOrEqual(send.left);
      expect(send.right).toBeLessThanOrEqual(window.innerWidth);
      expect(document.documentElement.scrollWidth).toBe(window.innerWidth);
    }
  } finally {
    view.unmount();
    await page.viewport(1280, 720);
  }
});

it('gives the phone composer a compact input and one aligned preference row without overflow', async () => {
  const view = render(<ModelForm />);
  try {
    for (const [width, height] of [[390, 844], [320, 700], [844, 390]]) {
      await page.viewport(width, height);
      const heading = screen.getByRole('heading', { level: 1 });
      const input = screen.getByRole('textbox', { name: 'What this track should do' });
      const model = screen.getByRole('button', { name: 'Model: Default' }).getBoundingClientRect();
      const send = screen.getByRole('button', { name: 'Create track' }).getBoundingClientRect();
      await waitFor(() => {
        expect(screen.queryByRole('button', { name: 'Track options' })).toBeNull();
        for (const name of ['Default folder', 'Model: Default']) expectFullLabel(name);
      });
      expect(Math.abs(model.top + model.height / 2 - send.top - send.height / 2)).toBeLessThan(2);
      expect(model.right).toBeLessThanOrEqual(send.left);
      expect(model.bottom).toBeLessThanOrEqual(height);
      expect(input.getBoundingClientRect().height).toBeLessThan(66);
      expect(parseFloat(getComputedStyle(input).fontSize)).toBe(16);
      expect(parseFloat(getComputedStyle(heading).fontSize)).toBe(20);
      expect(document.documentElement.scrollWidth).toBe(width);
      expect(getComputedStyle(heading).fontFamily).toBe(getComputedStyle(screen.getByRole('button', { name: 'Create track' })).fontFamily);
    }
  } finally {
    view.unmount();
    await page.viewport(1280, 720);
  }
});

it('keeps a retained phone draft at the same text size and height while submitting or locked', async () => {
  await page.viewport(320, 740);
  const view = render(<ModelForm />);
  try {
    const input = screen.getByRole('textbox', { name: 'What this track should do' });
    await userEvent.type(input, '这个输入框在等待创建结果时，字体和换行应该保持完全一致。');
    const originalText = input.textContent;
    const originalStyle = getComputedStyle(input);
    const fontSize = originalStyle.fontSize;
    const lineHeight = originalStyle.lineHeight;
    const height = input.getBoundingClientRect().height;
    for (const [submitting, locked] of [[true, false], [false, true], [false, false]]) {
      view.rerender(<ModelForm submitting={submitting} locked={locked} />);
      expect(input.textContent).toBe(originalText);
      expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Default folder' }).getAttribute('aria-disabled') === 'true').toBe(submitting || locked);
      expect(getComputedStyle(input).fontSize).toBe(fontSize);
      expect(getComputedStyle(input).lineHeight).toBe(lineHeight);
      expect(input.getBoundingClientRect().height).toBe(height);
    }
  } finally {
    view.unmount();
    await page.viewport(1280, 720);
  }
});

it('keeps long custom preferences readable inside overflow and switches back via the Repo menu', async () => {
  const modelName = 'A long custom model for careful reasoning';
  const cwd = '/srv/projects/a-long-custom-folder-for-the-mobile-layout';
  const view = render(<ModelForm initialCwd={cwd} modelName={modelName} />);
  try {
    await page.viewport(320, 740);
    await userEvent.click(screen.getByRole('button', { name: 'Model: Default' }));
    await userEvent.click(screen.getByRole('radio', { name: modelName }));
    await userEvent.click(await screen.findByRole('button', { name: 'Track options' }));
    const options = await screen.findByRole('dialog', { name: 'Track options' });
    for (const width of [320, 390]) {
      await page.viewport(width, 740);
      const folder = within(options).getByRole('button', { name: `Repo: a-long-custom-folder-for-the-mobile-layout (${cwd})` }).getBoundingClientRect();
      expect(folder.right).toBeLessThanOrEqual(width);
      expect(document.documentElement.scrollWidth).toBe(width);
    }
    await page.elementLocator(within(options).getByRole('button', { name: `Repo: a-long-custom-folder-for-the-mobile-layout (${cwd})` })).click();
    await page.getByRole('menuitem', { name: /^Default$/ }).click();
    await page.viewport(600, 740);
    await expect.element(page.getByRole('button', { name: 'Default folder' })).toBeVisible();
  } finally {
    view.unmount();
    await page.viewport(1280, 720);
  }
});

it('keeps Model beside Create and moves preferences into overflow before they stop fitting', async () => {
  const view = render(<ModelForm initialCwd={OVERFLOW_CWD} />);
  try {
    await page.viewport(600, 844);
    const model = screen.getByRole('button', { name: 'Model: Default' }).getBoundingClientRect();
    const send = screen.getByRole('button', { name: 'Create track' }).getBoundingClientRect();
    expect(Math.abs(model.top + model.height / 2 - send.top - send.height / 2)).toBeLessThan(2);
    expect(await screen.findByRole('button', { name: OVERFLOW_FOLDER_LABEL })).toBeTruthy();
    await page.viewport(320, 740);
    expect(await screen.findByRole('button', { name: 'Track options' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Template: No template' })).toBeNull();
    expect(screen.queryByRole('button', { name: OVERFLOW_FOLDER_LABEL })).toBeNull();
    await page.viewport(600, 740);
    await waitFor(() => { expect(screen.queryByRole('button', { name: 'Track options' })).toBeNull(); });
    expectFullLabel(OVERFLOW_FOLDER_LABEL);
  } finally {
    view.unmount();
    await page.viewport(1280, 720);
  }
});

it('keeps directory selection and cancellation usable through overflow', async () => {
  await page.viewport(320, 740);
  const cwd = '/srv/a-long-directory-selected-through-the-real-browser';
  const view = render(<ModelForm initialCwd={OVERFLOW_CWD} listDirectory={() => Promise.resolve({ path: cwd, parent: null, entries: [] })} />);
  try {
    const more = await screen.findByRole('button', { name: 'Track options' });
    more.focus();
    await userEvent.keyboard('{Enter}');
    const options = await screen.findByRole('dialog', { name: 'Track options' });
    await page.elementLocator(within(options).getByRole('button', { name: OVERFLOW_FOLDER_LABEL })).click();
    await page.getByRole('menuitem', { name: /^Change folder…/ }).click();
    const cancelledDirectory = await screen.findByRole('dialog', { name: 'Choose a directory' });
    await page.elementLocator(within(cancelledDirectory).getByRole('button', { name: 'Cancel' })).click();
    await waitFor(() => { expect(screen.queryByRole('dialog', { name: 'Choose a directory' })).toBeNull(); });
    expect(screen.queryByRole('dialog', { name: 'Track options' })).toBeNull();
    await waitFor(() => { expect(document.activeElement).toBe(more); });
    await page.getByRole('button', { name: 'Track options' }).click();
    await page.getByRole('button', { name: OVERFLOW_FOLDER_LABEL }).click();
    await page.getByRole('menuitem', { name: /^Change folder…/ }).click();
    const directory = await screen.findByRole('dialog', { name: 'Choose a directory' });
    await page.elementLocator(await within(directory).findByRole('button', { name: 'Select this directory' })).click();
    await waitFor(() => { expect(screen.queryByRole('dialog', { name: 'Choose a directory' })).toBeNull(); });
    expect(screen.queryByRole('dialog', { name: 'Track options' })).toBeNull();
    await waitFor(() => { expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Track options' })); });
    await page.getByRole('button', { name: 'Track options' }).click();
    expect(await screen.findByRole('button', { name: `Repo: a-long-directory-selected-through-the-real-browser (${cwd})` })).toBeTruthy();
    await userEvent.keyboard('{Escape}');
    await waitFor(() => { expect(screen.queryByRole('dialog', { name: 'Track options' })).toBeNull(); });
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Track options' }));
    await page.viewport(600, 844);
    await waitFor(() => { expect(screen.queryByRole('button', { name: 'Track options' })).toBeNull(); });
    await page.getByRole('button', { name: `Repo: a-long-directory-selected-through-the-real-browser (${cwd})` }).click();
    await page.getByRole('menuitem', { name: /^Default$/ }).click();
    await waitFor(() => { expect(screen.queryByRole('button', { name: 'Track options' })).toBeNull(); });
    expect(screen.getByRole('button', { name: 'Default folder' })).toBeTruthy();
    expectFullLabel('Default folder');
  } finally {
    view.unmount();
    await page.viewport(1280, 720);
  }
});

it('restores the visible options trigger after a trusted pointer cancels directory browsing', async () => {
  await page.viewport(320, 740);
  const view = render(<ModelForm initialCwd={OVERFLOW_CWD} />);
  try {
    await page.getByRole('button', { name: 'Track options', exact: true }).click();
    await page.getByRole('button', { name: OVERFLOW_FOLDER_LABEL, exact: true }).click();
    await page.getByRole('menuitem', { name: /^Change folder…/ }).click();
    await page.getByRole('dialog', { name: 'Choose a directory', exact: true }).getByRole('button', { name: 'Cancel', exact: true }).click();
    await waitFor(() => { expect(screen.queryByRole('dialog', { name: 'Choose a directory' })).toBeNull(); });
    expect(screen.queryByRole('dialog', { name: 'Track options' })).toBeNull();
    await waitFor(() => { expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Track options' })); });
  } finally {
    view.unmount();
    await page.viewport(1280, 720);
  }
});

it('keeps standard secondary button affordances on the visible model and overflow controls', async () => {
  await page.viewport(320, 740);
  const view = render(<ModelForm initialCwd={OVERFLOW_CWD} />);
  try {
    const more = await screen.findByRole('button', { name: 'Track options' });
    const model = screen.getByRole('button', { name: 'Model: Default' });
    expect(more.getAttribute('data-variant')).toBe('secondary');
    expect(model.getAttribute('data-variant')).toBe('secondary');
    expect(getComputedStyle(model).backgroundColor).not.toBe('rgba(0, 0, 0, 0)');
    expect(parseFloat(getComputedStyle(model).paddingInlineStart)).toBeGreaterThan(0);
  } finally {
    view.unmount();
    await page.viewport(1280, 720);
  }
});

it('restores a focused overflow trigger into inline preferences without moving unrelated focus on resize', async () => {
  await page.viewport(320, 740);
  const view = render(<ModelForm initialCwd={OVERFLOW_CWD} />);
  try {
    const more = await screen.findByRole('button', { name: 'Track options' });
    more.focus();
    expect(document.activeElement).toBe(more);
    await page.viewport(844, 740);
    const folder = await screen.findByRole('button', { name: OVERFLOW_FOLDER_LABEL });
    await waitFor(() => { expect(document.activeElement).toBe(folder); });

    const editor = screen.getByRole('textbox', { name: 'What this track should do' });
    editor.focus();
    await page.viewport(320, 740);
    expect(await screen.findByRole('button', { name: 'Track options' })).toBeTruthy();
    expect(document.activeElement).toBe(editor);

    const model = screen.getByRole('button', { name: 'Model: Default' });
    model.focus();
    await page.viewport(844, 740);
    expect(await screen.findByRole('button', { name: OVERFLOW_FOLDER_LABEL })).toBeTruthy();
    expect(document.activeElement).toBe(model);
  } finally {
    view.unmount();
    await page.viewport(1280, 720);
  }
});


it('uses the card radius for the composer while retaining full pill and send contours', async () => {
  await page.viewport(320, 844);
  const view = render(<ModelForm />);
  try {
    const input = screen.getByRole('textbox', { name: 'What this track should do' });
    const body = input.closest('[data-density]')!.firstElementChild!;
    expect(getComputedStyle(body).borderRadius).toBe('16px');
    input.blur();
    const restingShadow = getComputedStyle(body).boxShadow;
    expect(restingShadow).not.toBe('none');
    await page.elementLocator(body).hover();
    expect(getComputedStyle(body).boxShadow).toBe(restingShadow);
    await page.getByRole('textbox', { name: 'What this track should do' }).click();
    await expect.poll(() => getComputedStyle(body).boxShadow).toContain('0px 0px 0px 2px');
    expect(getComputedStyle(body).borderRadius).toBe('16px');

    for (const name of ['Default folder', 'Model: Default', 'Create track']) {
      expect(getComputedStyle(await screen.findByRole('button', { name })).borderRadius).toBe('999px');
    }

    await page.getByRole('button', { name: 'Model: Default', exact: true }).click();
    const modelMenu = await page.getByRole('dialog').findElement();
    expect(getComputedStyle(modelMenu).borderRadius).toBe('16px');
    expect(getComputedStyle(modelMenu).paddingTop).toBe('4px');
    expect(getComputedStyle(await page.getByRole('radio', { name: 'Test model', exact: true }).findElement()).borderRadius).toBe('4px');
    expect(getComputedStyle(document.documentElement).getPropertyValue('-webkit-tap-highlight-color')).toBe('rgba(0, 0, 0, 0)');
  } finally {
    view.unmount();
    await page.viewport(1280, 720);
  }
});

it('keeps attach button and options aligned without overlap on narrow mobile viewports', async () => {
  await page.viewport(320, 740);
  const catalog: ModelCatalog = {
    models: [{ id: 'test-model', model: 'test-model', resolved_model: null, display_name: 'Test model', description: '',
      is_default: true, default_reasoning_effort: 'low', supported_reasoning_efforts: [{ reasoning_effort: 'low', description: 'Faster' }] }],
    default: { model: null, reasoning_effort: null, supported_reasoning_efforts: null }, default_source: 'unknown',
    source: 'live', fetched_at_ms: 1,
  };
  const view = render(
    <NewTrackForm
      area={{ id: 'work', name: 'Work' }}
      submitting={false}
      error={null}
      initialCwd={OVERFLOW_CWD}
      listDirectory={(path) => Promise.resolve({ path: path ?? '/', parent: null, entries: [] })}
      onSubmit={vi.fn()}
      attachButton={<button type="button" data-nc-attach="">Attach</button>}
      modelControls={<ModelPill provider="codex" groups={[{ provider: 'codex', catalog, availability: null }]} selection={FOLLOW_INSTALLATION_DEFAULT} effortControl="in-menu" onChange={() => undefined} />}
    />,
  );
  try {
    const attach = await screen.findByRole('button', { name: 'Attach' });
    const options = await screen.findByRole('button', { name: 'Track options' });
    const model = await screen.findByRole('button', { name: 'Model: Default' });
    await waitFor(() => {
      const attachRect = attach.getBoundingClientRect();
      const optionsRect = options.getBoundingClientRect();
      const modelRect = model.getBoundingClientRect();
      expect(attachRect.right).toBeLessThanOrEqual(optionsRect.left + 0.5);
      expect(optionsRect.right).toBeLessThanOrEqual(modelRect.left + 0.5);
    });
  } finally {
    view.unmount();
    await page.viewport(1280, 720);
  }
});
