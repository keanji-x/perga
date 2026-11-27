import { act } from '@testing-library/react';
import { expect } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import { README_FILE_URL, README_TRACK_URL, renderReadmeFixture } from './report-readme-fixture.tsx';

export async function verifyNestedReadmeHistory(width: 390 | 1440, cold: boolean) {
  await page.viewport(width, 844);
  let readReady = Promise.resolve();
  const { router, failFileRead, recoverFileRead } = renderReadmeFixture(cold ? README_FILE_URL + '&line=20' : README_TRACK_URL, undefined,
    { shortReport: true, fileReadReady: () => readReady });
  const reportBack = width === 390 ? 'Back to Report' : 'Back to track';
  if (!cold) await page.getByRole('button', { name: 'docs/README.md', exact: true }).click();
  const file = page.getByRole('region', { name: 'File docs/README.md' });
  const heading = file.getByRole('heading', { name: 'Documentation', exact: true });
  await expect.element(heading).toBeInTheDocument();
  const originalHref = router.history.location.href;
  const originalKey = router.history.location.state.__TSR_key;

  for (const deep of [false, true]) {
    const layer = file.element() as HTMLElement;
    layer.scrollTop = deep ? layer.scrollHeight : 0;
    const savedTop = layer.scrollTop;
    if (deep) expect(savedTop).toBeGreaterThan(200);
    const link = file.getByRole('button', { name: deep ? 'Prose ratchet' : 'English README' });
    // Fully expose the link before clicking so Playwright does not change the saved offset.
    const bounds = link.element().getBoundingClientRect();
    const viewport = layer.getBoundingClientRect();
    expect(bounds.top).toBeGreaterThanOrEqual(viewport.top);
    expect(bounds.bottom).toBeLessThanOrEqual(viewport.bottom);
    let clickedAt = -1;
    link.element().addEventListener('pointerdown', () => { clickedAt = layer.scrollTop; }, { once: true });
    await link.click();
    expect(clickedAt).toBe(savedTop);
    const next = page.getByRole('region', { name: `File docs/${deep ? 'prose-ratchet.md' : '../README.md'}` });
    const nextHeading = next.getByRole('heading', { name: deep ? 'Prose ratchet' : 'Why Perga?', exact: true });
    await expect.element(nextHeading).toBeVisible();
    expect(router.history.length).toBe(cold ? 2 : 3);
    const back = page.getByRole('button', { name: 'Back to docs/README.md', exact: true });
    await expect.element(back).toBeVisible();

    for (const returnWith of ['button', 'escape', 'history'] as const) {
      let finishRead!: () => void;
      readReady = new Promise<void>(resolve => { finishRead = resolve; });
      if (returnWith === 'button') await back.click();
      else if (returnWith === 'escape') await userEvent.keyboard('{Escape}');
      else act(() => { router.history.back(); });
      await expect.element(file.getByRole('status')).toHaveTextContent('Loading file');
      expect((file.element() as HTMLElement).scrollTop).toBe(0);
      expect(router.history.location.href).toBe(originalHref);
      expect(router.history.location.state.__TSR_key).toBe(originalKey);
      if (deep && returnWith === 'history') failFileRead();
      finishRead();
      if (deep && returnWith === 'history') {
        await expect.element(file.getByRole('alert')).toBeVisible();
        recoverFileRead();
        await file.getByRole('button', { name: 'Retry', exact: true }).click();
      }
      await expect.element(heading).toBeInTheDocument();
      await expect.poll(() => (file.element() as HTMLElement).scrollTop).toBe(savedTop);
      await expect.element(page.getByRole('button', { name: reportBack, exact: true })).toBeVisible();
      if (returnWith !== 'history') {
        act(() => { router.history.forward(); });
        await expect.element(nextHeading).toBeVisible();
        await expect.element(back).toBeVisible();
      }
    }
  }
  const originalTop = (file.element() as HTMLElement).scrollTop;
  act(() => { router.history.forward(); });
  const lastFile = page.getByRole('region', { name: 'File docs/prose-ratchet.md' });
  await lastFile.getByRole('button', { name: 'Documentation', exact: true }).click();
  await expect.element(heading).toBeInTheDocument();
  expect(router.history.location.state.__TSR_key).not.toBe(originalKey);
  expect((file.element() as HTMLElement).scrollTop).toBe(0);
  act(() => { router.history.back(); });
  await expect.element(lastFile).toBeVisible();
  act(() => { router.history.back(); });
  await expect.element(heading).toBeInTheDocument();
  expect(router.history.location.state.__TSR_key).toBe(originalKey);
  await expect.poll(() => (file.element() as HTMLElement).scrollTop).toBe(originalTop);
  await page.screenshot({ path: `./__screenshots__/nested-file-${width}-${cold ? 'cold' : 'report'}-restored.png` });
  await page.getByRole('button', { name: reportBack, exact: true }).click();
  await expect.poll(() => router.history.location.href).toBe(README_TRACK_URL);
  expect(router.history.canGoBack()).toBe(false);
  await expect.element(file).not.toBeInTheDocument();
}
