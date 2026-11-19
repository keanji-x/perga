import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it } from 'vitest';
import { DailyCalendar } from './daily-calendar.tsx';

afterEach(cleanup);

it('switches Calendar from a Monday-first week to a leap month and selects dates in place', async () => {
  render(<DailyCalendar today="2024-02-29" />);
  const calendar = screen.getByRole('region', { name: 'Calendar' });
  expect(screen.getByRole('heading', { name: 'Calendar' })).toBeTruthy();
  expect(screen.getByRole('radio', { name: 'Week' }).getAttribute('aria-checked')).toBe('true');
  const dates = () => within(calendar).getAllByRole('button', { name: /^\d{4}-\d{2}-\d{2}$/ });
  expect(dates().map(day => day.getAttribute('aria-label'))).toEqual([
    '2024-02-26', '2024-02-27', '2024-02-28', '2024-02-29', '2024-03-01', '2024-03-02', '2024-03-03',
  ]);
  await userEvent.click(screen.getByRole('radio', { name: 'Month' }));
  expect(dates()).toHaveLength(29);
  expect(within(calendar).queryByRole('button', { name: '2024-02-30' })).toBeNull();
  await userEvent.click(within(calendar).getByRole('button', { name: '2024-02-15' }));
  expect(within(calendar).getByRole('button', { name: '2024-02-15' }).getAttribute('aria-pressed')).toBe('true');
  expect(within(calendar).getByRole('button', { name: '2024-02-29' }).getAttribute('aria-pressed')).toBe('false');
  await userEvent.click(within(calendar).getByRole('button', { name: 'Today' }));
  expect(within(calendar).getByRole('button', { name: '2024-02-29' }).getAttribute('aria-pressed')).toBe('true');
  expect(within(calendar).queryByRole('button', { name: 'Today' })).toBeNull();
  expect(within(calendar).queryByRole('link')).toBeNull();
});

it('moves across year boundaries in month and week views and returns to Today', async () => {
  render(<DailyCalendar today="2024-01-01" />);
  const calendar = screen.getByRole('region', { name: 'Calendar' });
  await userEvent.click(screen.getByRole('radio', { name: 'Month' }));
  await userEvent.click(within(calendar).getByRole('button', { name: 'Previous month' }));
  within(calendar).getByText('December 2023');
  within(calendar).getByRole('button', { name: '2023-12-31' });
  await userEvent.click(within(calendar).getByRole('button', { name: 'Next month' }));
  within(calendar).getByText('January 2024');
  await userEvent.click(screen.getByRole('radio', { name: 'Week' }));
  await userEvent.click(within(calendar).getByRole('button', { name: 'Previous week' }));
  within(calendar).getByRole('button', { name: '2023-12-25' });
  within(calendar).getByRole('button', { name: '2023-12-31' });
  await userEvent.click(within(calendar).getByRole('button', { name: 'Next week' }));
  within(calendar).getByRole('button', { name: '2024-01-01' });
  expect(within(calendar).queryByRole('button', { name: 'Today' })).toBeNull();
});
