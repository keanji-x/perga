import { cleanup, render, screen } from '@testing-library/react';
import { page } from 'vitest/browser';
import { afterEach, expect, it } from 'vitest';

import '../../../styles/entry.css';
import { toTrack } from '../../../../../core/domain/track.js';
import { trackDtoSchema } from '../../../../../core/api/perga-v1.ts';
import { TemplateProtocolBar } from './public.tsx';

afterEach(() => { cleanup(); document.documentElement.removeAttribute('data-theme'); });

it.each(['light', 'dark'])('keeps the protocol on one line in %s and toggles rules', async theme => {
  await page.viewport(390, 800);
  document.documentElement.setAttribute('data-theme', theme);
  const track = toTrack(trackDtoSchema.parse({ id: 't', areaId: 'a', title: 'Report', sortOrder: 0,
    workspaceDriver: 'managed', workspaceSpec: {}, createdAtMs: 1, updatedAtMs: 1,
    templateId: 'dev', templateTitle: 'Dev',
    templateHeaderView: {
      badges: Array.from({ length: 8 }, (_, i) => ({ label: `Badge ${i}`, tone: 'success' })),
      rules: [{ label: 'Review', text: 'Check tests before merge.' }],
    },
  }));
  render(<TemplateProtocolBar track={track} />);
  const bar = screen.getByText('Dev').parentElement!;
  expect(getComputedStyle(bar).whiteSpace).toBe('nowrap');
  expect(bar.scrollWidth).toBeGreaterThan(bar.clientWidth);
  const positions = [...bar.children].map(child => Math.round(child.getBoundingClientRect().top));
  expect(Math.max(...positions) - Math.min(...positions)).toBeLessThan(10);
  const toggle = screen.getByRole('button', { name: /Rules/ });
  const toggleBox = toggle.getBoundingClientRect();
  expect(toggleBox.left).toBeGreaterThanOrEqual(0);
  expect(toggleBox.right).toBeLessThanOrEqual(390);
  expect(bar.getBoundingClientRect().right).toBeLessThanOrEqual(toggleBox.left);
  bar.scrollLeft = bar.scrollWidth;
  expect(toggle.getBoundingClientRect()).toEqual(toggleBox);
  await page.getByRole('button', { name: /Rules/ }).click();
  expect(screen.getByText('Check tests before merge.')).toBeTruthy();
  await page.getByRole('button', { name: /Rules/ }).click();
  expect(screen.queryByText('Check tests before merge.')).toBeNull();
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(390);
});
