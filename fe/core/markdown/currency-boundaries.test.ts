import { beforeEach, expect, it, vi } from 'vitest';
import { parse } from './public.js';

const { parseMarkdown } = vi.hoisted(() => ({ parseMarkdown: vi.fn() }));
vi.mock('mdast-util-from-markdown', async importOriginal => {
  const actual = await importOriginal<{ fromMarkdown: (...args: unknown[]) => unknown }>();
  parseMarkdown.mockImplementation(actual.fromMarkdown);
  return { ...actual, fromMarkdown: parseMarkdown };
});
beforeEach(() => { parseMarkdown.mockClear(); });

it('uses one math parse for many currency amounts beside real formulas', () => {
  const source = Array.from({ length: 40 }, (_, index) => `Price $${index + 1}, formula $x_${index}$, then $100.`).join('\r\n');
  const result = parse(source);
  expect(result.status).toBe('ready');
  if (result.status !== 'ready') throw new Error('expected parsed markdown');
  expect(parseMarkdown).toHaveBeenCalledTimes(1);
  expect(result.value.sourceLines).toHaveLength(40);
  const paragraph = result.value.children[0];
  if (paragraph?.type !== 'paragraph') throw new Error('expected paragraph');
  const formulas = paragraph.children.filter(node => node.type === 'inlineMath');
  expect(formulas.map(node => node.value)).toEqual(Array.from({ length: 40 }, (_, index) => `x_${index}`));
  for (const node of formulas) {
    expect(source.slice(node.position.start.offset, node.position.end.offset)).toBe(`$${node.value}$`);
  }
});

it('keeps reference labels, escaped dollars, code and formulas across many mixed rows', () => {
  const row = '[item $5] costs $10; **$20**; `$30`; \\$40; $x$';
  const source = `${Array.from({ length: 20 }, () => row).join('\n\n')}\n\n[item $5]: /target "$50"`;
  const result = parse(source);
  if (result.status !== 'ready') throw new Error('expected parsed markdown');
  // GFM boundaries are resolved once, followed by one math parse.
  expect(parseMarkdown).toHaveBeenCalledTimes(2);
  expect(result.value.children).toHaveLength(20);
  for (const paragraph of result.value.children) {
    if (paragraph.type !== 'paragraph') throw new Error('expected paragraph');
    expect(paragraph.children.filter(node => node.type === 'inlineMath')).toMatchObject([{ value: 'x' }]);
    expect(paragraph.children.find(node => node.type === 'link')).toMatchObject({
      destination: '/target', title: '$50', referenceSource: '[item $5]', children: [{ type: 'text', value: 'item $5' }],
    });
    expect(paragraph.children.find(node => node.type === 'inlineCode')).toMatchObject({ value: '$30' });
    expect(paragraph.children.filter(node => node.type === 'text').map(node => node.value).join('')).toContain('$40');
  }
});
