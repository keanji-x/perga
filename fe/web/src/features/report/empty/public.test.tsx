// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { ReportEmpty } from './public.tsx';

afterEach(cleanup);

describe('ReportEmpty', () => {
  it('renders airy prose guidance and an honest static Flow sample with no fake protocol bar', async () => {
    render(<ReportEmpty />);

    expect(screen.getByRole('heading', { name: 'Progress lives here' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Sample Flow: how multi-step work runs' })).toBeTruthy();
    expect(screen.getByRole('region', { name: 'Sample Flow' })).toBeTruthy();
    expect(screen.queryByRole('region', { name: 'Template protocol' })).toBeNull();
    expect(document.querySelector('[data-nc-report]')).toBeNull();
    expect(screen.queryByRole('table')).toBeNull();

    // Default-selected running sample node and clickable inspection of sibling sample nodes.
    expect(screen.getByText(/Dispatches child sessions in dependency order/)).toBeTruthy();

    await userEvent.click(screen.getByRole('button', { name: /^Scope goal and plan/ }));
    expect(screen.getByText(/attaches a matching template if structured views or schedules fit/)).toBeTruthy();

    await userEvent.click(screen.getByRole('button', { name: /^Synthesize into Report/ }));
    expect(screen.getByText(/Wakes Lead once upstream steps settle/)).toBeTruthy();
  });
});
