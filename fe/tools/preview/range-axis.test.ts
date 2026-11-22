import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { resolve } from 'node:path';
import { expect, it } from 'vitest';

it('shows a positive current price before valuation targets are recorded', () => {
  const { JSDOM } = createRequire(import.meta.url)('jsdom') as {
    JSDOM: new (html: string, options: { runScripts: 'dangerously' }) => { window: Window & typeof globalThis };
  };
  const html = readFileSync(resolve(import.meta.dirname, '../../../templates/equity-research/widgets/range-axis.html'), 'utf8');
  const { window } = new JSDOM(html, { runScripts: 'dangerously' });
  try {
    function update(data: unknown) {
      window.dispatchEvent(new window.MessageEvent('message', {
        source: window.parent,
        data: { type: 'perga:data', data },
      }));
    }
    update({ current: { value: 123.45, display: '$123.45', label: '现价' }, points: [] });
    expect(window.document.querySelector('#app')?.textContent).toBe('现价$123.45');
    update({ current: { value: 0 }, points: [] });
    expect(window.document.querySelector('#app')?.textContent).toBe('价格与估值区间待录入。');
  } finally {
    window.close();
  }
});
