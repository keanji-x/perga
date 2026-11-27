// Settings › Planners with the production styles in a real browser (#1817): a long reason (a path in a
// fix hint) clamps to one line with an ellipsis without widening the pane, and reveals the full text on hover.
import { cleanup, render } from '@testing-library/react';
import { page, userEvent } from 'vitest/browser';
import { afterEach, describe, expect, it, vi } from 'vitest';

import '../../styles/entry.css';

import type { ManagedProviderAvailability } from '../../../../core/domain/agent-providers.ts';
import { Dialog } from '../../ui/dialog/public.tsx';
import { PlannersPane } from './planners.tsx';
import { SettingsSurface } from './public.tsx';

afterEach(() => { cleanup(); document.body.replaceChildren(); });

const LOGGED_OUT = 'not logged in — run `claude /login` with '
  + 'CLAUDE_CONFIG_DIR=/home/owner/.local/share/perga/claude-planner/config-dir-for-the-dedicated-login';

const PROVIDERS: readonly ManagedProviderAvailability[] = [
  { availability: { provider: 'codex', status: 'ready', reason: null, authentication_method: null, authentication_notice: null, checked_at_ms: 1_760_000_000_000 }, accepts_unavailable_blank_track: true, capabilities: { managed_conversation: true, steer: true, images: true, manual_compact: true, history_replacement: true, requires_ready_availability: false } },
  { availability: { provider: 'claude', status: 'unavailable', reason: LOGGED_OUT, authentication_method: null, authentication_notice: null, checked_at_ms: 1_760_000_000_000 }, accepts_unavailable_blank_track: true, capabilities: { managed_conversation: true, steer: false, images: true, manual_compact: true, history_replacement: true, requires_ready_availability: true } },
];

describe('Settings Planners', () => {
  it('shows the selected Grok API key authentication alongside readiness', async () => {
    render(<PlannersPane providers={[{ ...PROVIDERS[0], availability: {
      provider: 'grok', status: 'ready', reason: null, authentication_notice: null,
      authentication_method: 'xai.api_key', checked_at_ms: 1,
    } }]} loadError={null} onRetryLoad={vi.fn()} onRecheck={vi.fn()}
      rechecking={false} recheckError={null} authenticationRecovery={null} />);
    await expect.element(page.getByText('Authentication: xai.api_key')).toBeVisible();
    await expect.element(page.getByText('Ready', { exact: true })).toBeVisible();
    await page.screenshot({ path: '../../../../test-results/grok-api-key-auth.png' });
  });

  it('opens the clipped diagnostic on Tab and dismisses it on Escape inside the dialog', async () => {
    await page.viewport(1180, 720);
    const onClose = vi.fn();
    render(
      <Dialog purpose="form" origin={{ kind: 'surface-center' }} open title="Settings" onClose={onClose} hideTitleRow>
        <PlannersPane authenticationRecovery={null} providers={PROVIDERS} loadError={null}
          onRetryLoad={vi.fn()} onRecheck={vi.fn()} rechecking={false} recheckError={null} />
      </Dialog>,
    );
    const reason = page.getByRole('listitem').filter({ hasText: 'Claude' }).getByText(LOGGED_OUT);
    await expect.element(reason).toHaveAttribute('aria-describedby');
    await expect.element(page.getByRole('button', { name: 'Recheck planners' })).toHaveFocus();
    await userEvent.tab();
    await expect.element(reason).toHaveFocus();
    const tooltip = page.getByRole('tooltip').filter({ hasText: LOGGED_OUT });
    await expect.element(tooltip).toBeVisible();
    expect(tooltip.element().textContent).toBe(reason.element().textContent);
    await userEvent.keyboard('{Escape}');
    await expect.element(tooltip).not.toBeInTheDocument();
    await expect.element(reason).toHaveFocus();
    expect(onClose).not.toHaveBeenCalled();
  });

  for (const [presentation, width, height] of [['desktop', 1180, 720], ['mobile-detail', 390, 844]] as const) {
    it(`clamps a long reason to one line without widening the ${presentation} pane and reveals it on hover`, async () => {
      await page.viewport(width, height);
      render(
        <SettingsSurface presentation={presentation} section="planners" onSelectSection={vi.fn()}>
          <PlannersPane authenticationRecovery={null} providers={PROVIDERS} loadError={null}
            onRetryLoad={vi.fn()} onRecheck={vi.fn()}
            rechecking={false} recheckError={null} />
        </SettingsSurface>,
      );
      const reasonLocator = page.getByRole('listitem').filter({ hasText: 'Claude' }).getByText(LOGGED_OUT);
      await expect.element(reasonLocator).toBeVisible();
      await expect.element(page.getByText('Unavailable')).toBeVisible();
      await expect.element(page.getByText('Ready')).toBeVisible();
      await expect.element(page.getByRole('button', { name: 'Recheck planners' })).toBeVisible();
      expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(width);
      const reason = reasonLocator.element();
      expect(reason.getBoundingClientRect().right).toBeLessThanOrEqual(width);
      expect(reason.scrollWidth).toBeGreaterThan(reason.clientWidth);
      await expect.element(reasonLocator).toHaveAttribute('aria-describedby');
      await reasonLocator.hover();
      await expect.element(page.getByRole('tooltip')).toHaveTextContent(LOGGED_OUT);
      await page.screenshot({ path: `../../../../test-results/settings-planners-${presentation}.png` });
    });
  }
});
