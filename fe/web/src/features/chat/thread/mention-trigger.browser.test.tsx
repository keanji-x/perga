import '../../../styles/entry.css';
import { cleanup,render } from '@testing-library/react';
import { afterEach,expect,it,vi } from 'vitest';
import { page,userEvent } from 'vitest/browser';

import { mentionQueryOf,type MentionSearch } from '../../../../../core/domain/mentions.ts';
import { useMentionTrigger } from './mention-trigger.tsx';
import { OutgoingMessage } from './outgoing-message.tsx';
import { ChatComposer } from './public.tsx';

afterEach(cleanup);

function Composer({ search, onSend }: { search: MentionSearch; onSend: (text: string) => void }) {
  const trigger = useMentionTrigger(search);
  return <div style={{ position: 'fixed', top: '400px', left: '32px', width: 'min(400px, calc(100vw - 64px))' }}>
    <ChatComposer mentionTrigger={trigger} onSend={onSend} />
  </div>;
}

it.each([1200])('shows readable type entrances and enters each filtered list by mouse at %ipx', async (width) => {
  await page.viewport(width, 900);
  const search = vi.fn<MentionSearch>((query) => {
    const kind = mentionQueryOf(query).kind;
    const suggestions = [
      { id: 'tag', kind: 'tag' as const, label: '#release', chip: '#release', detail: '2 tracks', insert: '@`tag:release`' },
      { id: 'track', kind: 'track' as const, label: 'Release notes', chip: 'Release notes', detail: null, insert: '@`/研发/Release notes/report.md`' },
      { id: 'block', kind: 'block' as const, label: 'Checklist', chip: 'Checklist', detail: 'Release notes', insert: '@`/研发/Release notes/report.md#b1`' },
    ];
    return Promise.resolve(suggestions.filter(item => kind === null || item.kind === kind));
  });
  render(<Composer search={search} onSend={vi.fn()} />);
  const field = page.getByRole('combobox', { name: 'Message' });
  await field.click();
  await userEvent.keyboard('@');
  await expect.element(page.getByRole('option', { name: /^Tags/ })).toBeVisible();
  expect(document.querySelectorAll('[role="option"]')).toHaveLength(12);
  const row = document.querySelector('[data-nc-mention-category="tag"]')!;
  const label = row.children[0].getBoundingClientRect();
  const description = row.children[1].getBoundingClientRect();
  expect(description.left).toBeGreaterThan(label.right);
  const example = document.querySelector('[data-nc-mention="tag"]')!;
  const titleStyle = getComputedStyle(row.children[0]);
  const exampleStyle = getComputedStyle(example.children[1]);
  const captionStyle = getComputedStyle(row.children[1]);
  expect(titleStyle.fontSize).toBe(captionStyle.fontSize);
  expect(parseFloat(exampleStyle.fontSize)).toBeGreaterThan(parseFloat(titleStyle.fontSize));
  expect(titleStyle.fontFamily).toBe(getComputedStyle(document.body).fontFamily);
  expect(exampleStyle.fontFamily).toBe(titleStyle.fontFamily);
  expect(getComputedStyle(row).fontSize).toBe(exampleStyle.fontSize);
  expect(parseFloat(exampleStyle.fontSize)).toBeGreaterThan(parseFloat(captionStyle.fontSize));
  expect(example.children[0].textContent).toBe('#');
  expect(example.children[1].textContent).toBe('release');
  expect(example.children[0].getBoundingClientRect().left).toBe(label.left);
  const more = page.getByRole('option', { name: 'More Tags' }).element();
  expect(more.querySelector('[aria-hidden="true"]')!.getBoundingClientRect().left).toBe(label.left);
  expect(exampleStyle.color).not.toBe(titleStyle.color);
  const headingOptionStyle = getComputedStyle(row.closest('[role="option"]')!);
  expect(parseFloat(headingOptionStyle.marginBlockStart)).toBe(4);
  expect(row.closest('[role="option"]')!.getBoundingClientRect().height).toBe(24);
  expect(example.closest('[role="option"]')!.getBoundingClientRect().height).toBe(24);
  expect(description.right).toBe(example.children[2].getBoundingClientRect().right);
  expect(row.children).toHaveLength(2);
  await page.screenshot({ path: `./__screenshots__/mention-categories-${width}.png` });

  for (const [category, prefixText, result] of [
    ['Tags', '#', '#release'], ['Tracks', '/', 'Release notes'], ['Blocks', '>', 'Checklist'],
  ]) {
    await page.getByRole('option', { name: `More ${category}` }).click();
    await expect.element(field).toHaveTextContent(`@${prefixText}`);
    await expect.element(page.getByRole('option', { name: new RegExp(result) })).toBeVisible();
    expect(document.querySelectorAll('[role="option"]')).toHaveLength(1);
    expect(search).toHaveBeenLastCalledWith(prefixText, expect.anything());
    await userEvent.keyboard('{Backspace}');
    await expect.element(page.getByRole('option', { name: /^Tags/ })).toBeVisible();
  }
  await page.getByRole('option', { name: /^Plugins/ }).click();
  await expect.element(field).toHaveTextContent('@+');
  await expect.element(page.getByText('No matches')).toBeVisible();
  expect(document.querySelectorAll('[role="option"]')).toHaveLength(0);
});

/** Resolve authored color spaces and alpha overlays through the browser's compositor. */
function captionContrast(caption: Element): number {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = 1;
  const context = canvas.getContext('2d')!;
  const ancestors: Element[] = [];
  for (let element: Element | null = caption; element; element = element.parentElement) ancestors.unshift(element);
  context.fillStyle = 'white';
  context.fillRect(0, 0, 1, 1);
  for (const element of ancestors) {
    context.fillStyle = getComputedStyle(element).backgroundColor;
    context.fillRect(0, 0, 1, 1);
  }
  const background = [...context.getImageData(0, 0, 1, 1).data].slice(0, 3);
  context.clearRect(0, 0, 1, 1);
  context.fillStyle = getComputedStyle(caption).color;
  context.fillRect(0, 0, 1, 1);
  const foreground = [...context.getImageData(0, 0, 1, 1).data].slice(0, 3);
  const luminance = (rgb: number[]) => rgb.map(value => {
    const channel = value / 255;
    return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
  }).reduce((sum, value, index) => sum + value * [0.2126, 0.7152, 0.0722][index], 0);
  const first = luminance(foreground), second = luminance(background);
  return (Math.max(first, second) + 0.05) / (Math.min(first, second) + 0.05);
}

it.each(['light', 'dark'])('keeps %s selected menu captions readable', async theme => {
  const previousTheme = document.documentElement.dataset.theme;
  document.documentElement.dataset.theme = theme;
  try {
    await page.viewport(1200, 900);
    render(<Composer search={() => Promise.resolve([])} onSend={vi.fn()} />);
    await page.getByRole('combobox', { name: 'Message' }).click();
    await userEvent.keyboard('@');
    await expect.element(page.getByRole('option', { name: /^Plugins/ })).toBeVisible();
    const selected = page.getByRole('option', { selected: true }).element();
    const caption = selected.querySelector('[data-nc-mention-category]')!.children[1];
    expect(captionContrast(caption)).toBeGreaterThanOrEqual(4.5);
  } finally {
    if (previousTheme === undefined) delete document.documentElement.dataset.theme;
    else document.documentElement.dataset.theme = previousTheme;
  }
});

it('opens Templates by mouse, shows its marker and sends the badge reference', async () => {
  await page.viewport(1200, 900);
  const onSend = vi.fn();
  const search = vi.fn<MentionSearch>(() => Promise.resolve([{
    id: 'template:deploy', kind: 'template', label: 'Deploy', chip: 'Deploy', detail: 'Ship a change', insert: '@`template:deploy`',
  }]));
  render(<Composer search={search} onSend={onSend} />);
  const field = page.getByRole('combobox', { name: 'Message' });
  await field.click();
  await userEvent.keyboard('@');
  await expect.element(page.getByRole('option', { name: /^Templates/ })).toBeVisible();
  expect(document.querySelector('[data-nc-mention="template"]')!.children[0].textContent).toBe(':');
  await page.getByRole('option', { name: /^Templates/ }).click();
  await expect.element(field).toHaveTextContent('@:');
  await expect.element(page.getByRole('option', { name: /Deploy/ })).toBeVisible();
  expect(search).toHaveBeenLastCalledWith(':', expect.anything());
  await page.getByRole('option', { name: /Deploy/ }).click();
  expect(field.element().querySelector('[data-astryx-token]')?.textContent).toBe('Deploy');
  await page.screenshot({ path: './__screenshots__/template-mention-chip.png' });
  await userEvent.keyboard('{Enter}');
  expect(onSend).toHaveBeenLastCalledWith('@`template:deploy`');
});

function verticalMidpoint(rect: DOMRect): number {
  return rect.top + rect.height / 2;
}

function textNodeMidpoint(container: Node, snippet: string): number {
  const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
    const content = node.textContent ?? '';
    const start = content.indexOf(snippet);
    if (start === -1) continue;
    const range = document.createRange();
    range.setStart(node, start);
    range.setEnd(node, start + snippet.length);
    return verticalMidpoint(range.getBoundingClientRect());
  }
  throw new Error(`Text snippet not found: ${snippet}`);
}

it.each(['standard', 'mobile'] as const)('aligns pasted text pills, structured mentions, and long custom mentions in %s composer', async (layout) => {
  await page.viewport(1200, 900);
  const longInsert = `@\`plugin:very-long-payload-${'x'.repeat(220)}\``;
  const search = vi.fn<MentionSearch>((query) => Promise.resolve([
    { id: 'tag:release', kind: 'tag' as const, label: '#release', chip: '#release', detail: '2 tracks', insert: '@`tag:release`' },
    { id: 'plugin:long', kind: 'plugin' as const, label: 'Long plugin', chip: 'Long plugin', detail: 'Custom render', insert: longInsert },
  ].filter(item => query === '' || item.label.toLowerCase().includes(query.toLowerCase()))));
  function WideComposer() {
    const trigger = useMentionTrigger(search);
    return <div style={{ position: 'fixed', top: '400px', left: '32px', width: '760px' }}>
      <ChatComposer layout={layout} mentionTrigger={trigger} onSend={vi.fn()} />
    </div>;
  }
  render(<WideComposer />);
  const field = page.getByRole('combobox', { name: 'Message' });
  const editable = field.element();
  await field.click();

  await userEvent.keyboard('中文Abc @#release');
  await page.getByRole('option', { name: /#release/ }).click();

  await userEvent.keyboard(' 黏贴 ');
  const pastedPayload = '这是一段很长的黏贴文本用于验证药丸对齐，'.repeat(18);
  const clipboardData = new DataTransfer();
  clipboardData.setData('text/plain', pastedPayload);
  editable.dispatchEvent(new ClipboardEvent('paste', { clipboardData, bubbles: true, cancelable: true }));

  await userEvent.keyboard(' 插件 @Long');
  await page.getByRole('option', { name: /Long plugin/ }).click();
  await userEvent.keyboard(' 结束');

  const tokens = [...editable.querySelectorAll<HTMLElement>('[data-astryx-token]')];
  expect(tokens).toHaveLength(3);
  const [structuredToken, pastedToken, customToken] = tokens;
  expect(structuredToken.textContent).toBe('#release');
  expect(pastedToken.textContent).toBe('360 chars');
  expect(customToken.textContent).toBe('Long plugin');
  // Custom tokens must portal directly into [data-astryx-token] with no extra wrapper <span>.
  expect(customToken.firstElementChild?.getAttribute('data-variant')).toBe('neutral');

  const textMid = textNodeMidpoint(editable, '中文Abc');
  for (const token of tokens) {
    const visual = token.querySelector<HTMLElement>('[data-variant]') ?? token.firstElementChild as HTMLElement;
    expect(visual).not.toBeNull();
    expect(Math.abs(verticalMidpoint(visual.getBoundingClientRect()) - textMid)).toBeLessThanOrEqual(0.5);
  }
});

it('aligns persisted outgoing mention pills and memory links with adjacent text', async () => {
  await page.viewport(1200, 900);
  render(
    <div style={{ width: '720px', padding: '16px' }}>
      <OutgoingMessage
        speaker="you"
        mobile={false}
        text="前面中文Abc @`tag:release` 中间 @`memory:area/guide` 后面文字"
        renderMemoryMention={(_scope, pill) => <a href="#memory">{pill}</a>}
      />
    </div>,
  );
  const paragraph = document.querySelector<HTMLElement>('[data-nc-turn="you"]')!;
  const pills = [...paragraph.querySelectorAll<HTMLElement>('[data-nc-sent-mention]')];
  expect(pills).toHaveLength(2);

  const textMid = textNodeMidpoint(paragraph, '前面中文Abc');
  for (const wrapper of pills) {
    const badge = wrapper.querySelector<HTMLElement>('[data-variant]')!;
    expect(badge).not.toBeNull();
    expect(Math.abs(verticalMidpoint(badge.getBoundingClientRect()) - textMid)).toBeLessThanOrEqual(0.5);
  }
});
