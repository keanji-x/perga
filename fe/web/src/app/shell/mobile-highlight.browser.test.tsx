import '../../styles/entry.css';
import { cleanup, render } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { page } from 'vitest/browser';
import { AreaSelector } from './mobile/area-selector.tsx';

afterEach(() => { cleanup(); delete document.documentElement.dataset.theme; });

function highlightRadius(host: HTMLElement) {
  const probe = document.createElement('span');
  probe.style.borderRadius = 'var(--radius-highlight)'; host.append(probe);
  const radius = getComputedStyle(probe).borderRadius; probe.remove(); return radius;
}

it.each(['light', 'dark'])('uses the highlight contour for the real Area menu in %s', async theme => {
  await page.viewport(390, 844); document.documentElement.dataset.theme = theme;
  const area = { id: 'area', name: 'Work', kind: 'user' as const, color: '#5B8DEF', sort: 0, defaultTemplateId: null, defaultCwd: null, createdAt: 0, updatedAt: 0 };
  const view = render(<AreaSelector areas={[area]} activeArea={area} onSelectArea={vi.fn()} onCreateArea={vi.fn()} />);
  await page.getByRole('button', { name: 'Switch area, Work' }).click();
  const item = await page.getByRole('menuitem', { name: 'Work', exact: true }).findElement();
  expect(getComputedStyle(item).borderRadius).toBe(highlightRadius(view.container));
});
