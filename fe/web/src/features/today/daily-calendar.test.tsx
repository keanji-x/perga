import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
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

it('renders day indicators, reports visible ranges, and lists scheduled wakes for the selected day', async () => {
  const onSelectDate = vi.fn();
  const onVisibleRangeChange = vi.fn();
  const onOpenTrack = vi.fn();
  render(
    <DailyCalendar
      today="2026-10-04"
      selectedDate="2026-10-03"
      days={[
        { date: '2026-10-03', has_daily_track: false, has_report: false },
        { date: '2026-10-04', has_daily_track: true, has_report: true },
      ]}
      schedules={[
        {
          id: 'daily-review',
          date: '2026-10-03',
          track_id: 'trk_release',
          track_title: 'Release Track',
          title: 'Deploy check',
          schedule_kind: 'cron',
          cron_expr: '0 9 * * *',
          timezone: 'UTC',
          next_fire_at_ms: Date.parse('2026-10-03T09:00:00Z'),
          state: 'done',
        },
        {
          id: 'daily-review',
          date: '2026-10-03',
          track_id: 'trk_ops',
          track_title: 'Ops Track',
          title: 'Ops review',
          schedule_kind: 'cron',
          cron_expr: '0 10 * * *',
          timezone: 'UTC',
          next_fire_at_ms: Date.parse('2026-10-03T10:00:00Z'),
          state: 'active',
        },
        {
          id: 'daily-review',
          date: '2026-10-04',
          track_id: 'trk_release',
          track_title: 'Release Track',
          title: 'Deploy check',
          schedule_kind: 'cron',
          cron_expr: '0 9 * * *',
          timezone: 'UTC',
          next_fire_at_ms: Date.parse('2026-10-04T09:00:00Z'),
          state: 'active',
        },
      ]}
      timeZone="UTC"
      onSelectDate={onSelectDate}
      onVisibleRangeChange={onVisibleRangeChange}
      onOpenTrack={onOpenTrack}
    />,
  );
  const calendar = screen.getByRole('region', { name: 'Calendar' });
  expect(onVisibleRangeChange).toHaveBeenCalledWith({ start: '2026-09-28', end: '2026-10-04' });

  const day3 = within(calendar).getByRole('button', { name: '2026-10-03' });
  const day4 = within(calendar).getByRole('button', { name: '2026-10-04' });
  expect(day3.getAttribute('aria-pressed')).toBe('true');
  expect(day3.getAttribute('data-nc-schedule-count')).toBe('2');
  expect(day4.getAttribute('data-nc-has-track')).toBe('');
  expect(day4.getAttribute('data-nc-has-report')).toBe('');
  expect(day4.getAttribute('data-nc-schedule-count')).toBe('1');

  const agenda = within(calendar).getByRole('region', { name: 'Scheduled wakes' });
  within(agenda).getByText('Scheduled · 2');
  within(agenda).getByText('09:00');
  expect(within(agenda).getByText('Deploy check').closest('li')?.getAttribute('data-nc-schedule-state')).toBe('done');
  within(agenda).getByText('Ops review');
  within(agenda).getByText('0 9 * * *');

  await userEvent.click(within(agenda).getByRole('button', { name: 'Release Track' }));
  expect(onOpenTrack).toHaveBeenCalledWith('trk_release');

  await userEvent.click(day4);
  expect(onSelectDate).toHaveBeenCalledWith('2026-10-04');

  await userEvent.click(within(calendar).getByRole('button', { name: 'Today' }));
  expect(onSelectDate).toHaveBeenLastCalledWith('2026-10-04');
});

it('leaves every day unpressed with a null selection and lets Today or a day resolve it', async () => {
  const onSelectDate = vi.fn();
  render(<DailyCalendar today="2026-10-04" selectedDate={null} onSelectDate={onSelectDate} />);
  const calendar = screen.getByRole('region', { name: 'Calendar' });
  expect(within(calendar).getByRole('button', { name: '2026-10-04' }).getAttribute('aria-pressed')).toBe('false');
  await userEvent.click(within(calendar).getByRole('button', { name: 'Today' }));
  expect(onSelectDate).toHaveBeenLastCalledWith('2026-10-04');
  await userEvent.click(within(calendar).getByRole('button', { name: '2026-10-03' }));
  expect(onSelectDate).toHaveBeenLastCalledWith('2026-10-03');
});
