import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { page } from 'vitest/browser';
import { afterEach, expect, it } from 'vitest';
import { renderPage } from '../../features/track/page/test-fixtures.tsx';
import { ReportDocument } from '../../features/report/document/public.tsx';

afterEach(cleanup);

it('keeps the ordinary Track inventory beside a Report widget', async () => {
  await page.viewport(1440, 1000);
  renderPage({ report: <ReportDocument report={{ summary: '', body: '', blocks: [{ id: 'widget', kind: 'widget', payload: { use: 'builtin:table', view: 'assets' } }] }}
    templateViews={{ templateId: 'test', diagnostics: [], widgets: {}, dataRev: 0, flowRev: 0, views: { assets: { columns: [{ key: 'amount', label: '总资产', align: 'right' }], rows: [{ amount: '1,200' }] } } }} empty={<p>Empty</p>} />,
    conversationList: <button type="button">Existing Planner</button> });
  await expect.element(page.getByRole('heading', { name: 'Conversations', exact: true })).toBeVisible();
  await expect.element(page.getByRole('columnheader', { name: '总资产' })).toBeVisible();
  const amount = page.getByRole('cell', { name: '1,200' });
  await expect.element(amount).toBeVisible();
  expect(getComputedStyle(amount.element()).textAlign).toBe('right');
  await expect.element(page.getByRole('button', { name: 'Existing Planner' })).toBeVisible();
  await expect.element(page.getByRole('button', { name: 'Show track panel' })).not.toBeInTheDocument();
  const panel = document.querySelector('[data-nc-panel]')!;
  expect(panel.getBoundingClientRect().width).toBeGreaterThan(200);
  expect(document.getElementById('widget')!.getBoundingClientRect().right).toBeLessThanOrEqual(panel.getBoundingClientRect().left);
});
