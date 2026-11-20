import { cleanup, render, screen } from '@testing-library/react';
import { page } from 'vitest/browser';
import { afterEach, expect, it } from 'vitest';

import '../../../styles/entry.css';
import { toTrack, trackWireSchema } from '../../../../../core/domain/track.js';
import { TemplateProtocolBar } from './public.tsx';

afterEach(() => { cleanup(); document.documentElement.removeAttribute('data-theme'); });

it.each(['light', 'dark'])('keeps the protocol on one line in %s and opens its drawer', async theme => {
  await page.viewport(390, 800);
  document.documentElement.setAttribute('data-theme', theme);
  const track = toTrack(trackWireSchema.parse({ id: 't', area_id: 'a', title: 'Report', sort: 0,
    workspace: { isolation: 'managed', kind: 'managed', path: '', frozen_at: null }, created_at: 1, updated_at: 1,
    templateId: 'dev', templateTitle: 'Dev', templateInputs: { branch: 'main' },
    templateHeaderView: { badges: Array.from({ length: 8 }, (_, i) => ({ label: `Badge ${i}`, tone: 'success' })) },
  }));
  render(<TemplateProtocolBar track={track} inputsSchema={{ branch: { type: 'string' } }} />);
  const bar = screen.getByText('Dev').parentElement!;
  expect(getComputedStyle(bar).whiteSpace).toBe('nowrap');
  expect(bar.scrollWidth).toBeGreaterThan(bar.clientWidth);
  const positions = [...bar.children].map(child => Math.round(child.getBoundingClientRect().top));
  expect(Math.max(...positions) - Math.min(...positions)).toBeLessThan(10);
  const toggle = screen.getByRole('button', { name: /Details/ });
  const toggleBox = toggle.getBoundingClientRect();
  expect(toggleBox.left).toBeGreaterThanOrEqual(0);
  expect(toggleBox.right).toBeLessThanOrEqual(390);
  expect(bar.getBoundingClientRect().right).toBeLessThanOrEqual(toggleBox.left);
  bar.scrollLeft = bar.scrollWidth;
  expect(toggle.getBoundingClientRect()).toEqual(toggleBox);
  await page.getByRole('button', { name: /Details/ }).click();
  await page.getByRole('tab', { name: 'Inputs' }).click();
  expect(screen.getByRole<HTMLInputElement>('textbox', { name: 'branch' }).value).toBe('main');
  expect(screen.getByRole('tab', { name: 'Inputs' }).getAttribute('aria-selected')).toBe('true');
  await page.getByRole('button', { name: /Details/ }).click();
  expect(screen.queryByRole('tabpanel')).toBeNull();
});

it('edits typed inputs and creates schedules in Chromium', async () => {
  await page.viewport(390, 800);
  const inputs: unknown[] = [];
  const schedules: unknown[] = [];
  const track = toTrack(trackWireSchema.parse({ id: 't', area_id: 'a', title: 'Report', sort: 0,
    workspace: { isolation: 'managed', kind: 'managed', path: '', frozen_at: null }, created_at: 1, updated_at: 1,
    templateId: 'dev', templateInputs: { branch: 'main', count: 1, enabled: false, mode: 'slow', note: 'clear me' },
  }));
  render(<TemplateProtocolBar track={track}
    inputsSchema={{ note: { type: 'string', optional: true }, branch: { type: 'string' }, count: { type: 'number' }, enabled: { type: 'boolean' }, mode: { type: 'enum', values: ['slow', 'fast'] } }}
    onSaveInputs={patch => { inputs.push(patch); return Promise.resolve(); }} onSaveSchedule={request => { schedules.push(request); return Promise.resolve(); }} />);
  await page.getByRole('button', { name: /Details/ }).click();
  await page.getByRole('tab', { name: 'Inputs' }).click();
  await page.getByLabelText('note').fill('');
  await page.getByLabelText('branch').fill('next');
  await page.getByLabelText('count').fill('2.5');
  await page.getByLabelText('enabled').click();
  await page.getByLabelText('mode').selectOptions('fast');
  await page.getByRole('button', { name: 'Save inputs' }).click();
  await expect.poll(() => inputs).toEqual([{ branch: 'next', count: 2.5, enabled: true, mode: 'fast', note: null }]);
  await page.getByRole('tab', { name: 'Schedules' }).click();
  await page.getByLabelText('Title', { exact: true }).fill('Daily');
  await page.getByLabelText('Prompt', { exact: true }).fill('Check daily');
  await page.getByLabelText('Schedule type').selectOptions('cron');
  await page.getByLabelText('Cron expression').fill('0 9 * * *');
  await page.getByRole('button', { name: 'Add schedule', exact: true }).click();
  await expect.poll(() => schedules).toEqual([{ title: 'Daily', prompt: 'Check daily', cronExpr: '0 9 * * *', timezone: 'UTC' }]);
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(390);
});
