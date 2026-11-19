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
  render(<TemplateProtocolBar track={track} />);
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
  expect(screen.getByRole('tabpanel').textContent).toContain('main');
  expect(screen.getByRole('tab', { name: 'Inputs' }).getAttribute('aria-selected')).toBe('true');
  await page.getByRole('button', { name: /Details/ }).click();
  expect(screen.queryByRole('tabpanel')).toBeNull();
});
