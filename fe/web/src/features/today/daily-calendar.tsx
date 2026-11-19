import { useEffect, useMemo } from 'react';
import { SegmentedControl, SegmentedControlItem } from '@astryxdesign/core/SegmentedControl';
import type { TodayCalendarDay, TodayScheduleItem } from '../../../../core/domain/daily-planner.ts';
import { Icon } from '../../ui/icon/public.tsx';
import { PanelModule } from '../../ui/panel-card/public.tsx';
import { useState } from '../../ui/state/public.ts';
import styles from './daily-calendar.module.css';

type CalendarViewMode = 'week' | 'month';

export type DailyCalendarProps = Readonly<{
  today?: string;
  selectedDate?: string;
  days?: readonly TodayCalendarDay[];
  selectedSchedules?: readonly TodayScheduleItem[];
  scheduleTimeZone?: string;
  onSelectDate?: (date: string) => void;
  onVisibleRangeChange?: (range: Readonly<{ start: string; end: string }>) => void;
  onOpenTrack?: (trackId: string) => void;
}>;

function localDay(): string {
  const now = new Date();
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
}

function parseLocalDate(isoDate: string): Date {
  const [year, month, day] = isoDate.split('-').map(Number);
  return new Date(year, month - 1, day);
}

function formatIsoDate(date: Date): string {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
}

function addDays(isoDate: string, delta: number): string {
  const date = parseLocalDate(isoDate);
  date.setDate(date.getDate() + delta);
  return formatIsoDate(date);
}

function startOfWeekMonday(isoDate: string): string {
  const date = parseLocalDate(isoDate);
  const mondayOffset = (date.getDay() + 6) % 7;
  date.setDate(date.getDate() - mondayOffset);
  return formatIsoDate(date);
}

function formatScheduleTime(ms: number, timeZone: string): string {
  try {
    return new Intl.DateTimeFormat('en-GB', {
      hour: '2-digit',
      minute: '2-digit',
      hour12: false,
      timeZone,
    }).format(new Date(ms));
  } catch {
    return new Intl.DateTimeFormat('en-GB', {
      hour: '2-digit',
      minute: '2-digit',
      hour12: false,
      timeZone: 'UTC',
    }).format(new Date(ms));
  }
}

const WEEKDAY_LABELS = Object.freeze(['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'] as const);

/** Week/month calendar navigation with optional day indicators and scheduled wakes. */
export function DailyCalendar({
  today = localDay(),
  selectedDate,
  days,
  selectedSchedules,
  scheduleTimeZone,
  onSelectDate,
  onVisibleRangeChange,
  onOpenTrack,
}: DailyCalendarProps) {
  const [internalSelected, setInternalSelected] = useState(selectedDate ?? today);
  const [mode, setMode] = useState<CalendarViewMode>('week');
  const [anchorDate, setAnchorDate] = useState(selectedDate ?? today);
  const [trackedSelectedProp, setTrackedSelectedProp] = useState(selectedDate);

  if (selectedDate !== trackedSelectedProp) {
    setTrackedSelectedProp(selectedDate);
    setAnchorDate(selectedDate ?? today);
  }

  const activeSelected = selectedDate ?? internalSelected;
  const anchorMonth = anchorDate.slice(0, 7);
  const [year, monthNumber] = anchorMonth.split('-').map(Number);

  const move = (delta: number) => {
    if (mode === 'week') {
      setAnchorDate((current) => addDays(current, delta * 7));
      return;
    }
    const next = new Date(Date.UTC(year, monthNumber - 1 + delta, 1));
    setAnchorDate(next.toISOString().slice(0, 10));
  };

  const monthFirstOffset = mode === 'month'
    ? (new Date(Date.UTC(year, monthNumber - 1, 1)).getUTCDay() + 6) % 7
    : 0;

  const visibleDays: readonly string[] = useMemo(() => (
    mode === 'week'
      ? Array.from({ length: 7 }, (_, index) => addDays(startOfWeekMonday(anchorDate), index))
      : Array.from(
          { length: new Date(Date.UTC(year, monthNumber, 0)).getUTCDate() },
          (_, index) => `${anchorMonth}-${String(index + 1).padStart(2, '0')}`,
        )
  ), [anchorDate, anchorMonth, mode, monthNumber, year]);

  const visibleStart = visibleDays[0];
  const visibleEnd = visibleDays[visibleDays.length - 1];

  useEffect(() => {
    onVisibleRangeChange?.({ start: visibleStart, end: visibleEnd });
  }, [onVisibleRangeChange, visibleEnd, visibleStart]);

  const daysByDate = useMemo(() => {
    const map = new Map<string, TodayCalendarDay>();
    if (days !== undefined) {
      for (const entry of days) map.set(entry.date, entry);
    }
    return map;
  }, [days]);

  const selectDay = (day: string) => {
    if (selectedDate === undefined) setInternalSelected(day);
    setAnchorDate(day);
    onSelectDate?.(day);
  };

  const monthTitle = new Intl.DateTimeFormat('en-US', { month: 'long', year: 'numeric', timeZone: 'UTC' })
    .format(new Date(`${anchorMonth}-01T12:00:00Z`));
  const showTodayJump = activeSelected !== today || (mode === 'week'
    ? startOfWeekMonday(anchorDate) !== startOfWeekMonday(today)
    : anchorMonth !== today.slice(0, 7));

  return (
    <PanelModule
      title="Calendar"
      icon="calendar"
      className={styles.calendarModule}
      action={
        <span className={styles.viewControl}>
          <SegmentedControl
            label="Calendar view"
            size="sm"
            value={mode}
            onChange={(value) => { if (value === 'week' || value === 'month') setMode(value); }}
          >
            <SegmentedControlItem value="week" label="Week" />
            <SegmentedControlItem value="month" label="Month" />
          </SegmentedControl>
        </span>
      }
    >
      <section className={styles.calendar} aria-label="Calendar">
        <div className={styles.toolbar}>
          <button
            type="button"
            className={styles.navigationButton}
            aria-label={mode === 'week' ? 'Previous week' : 'Previous month'}
            onClick={() => move(-1)}
          >
            <Icon name="chevron-left" size="sm" />
          </button>
          <span className={styles.monthTitle} aria-live="polite">{monthTitle}</span>
          {showTodayJump && (
            <button
              type="button"
              className={styles.todayButton}
              onClick={() => selectDay(today)}
            >
              Today
            </button>
          )}
          <button
            type="button"
            className={styles.navigationButton}
            aria-label={mode === 'week' ? 'Next week' : 'Next month'}
            onClick={() => move(1)}
          >
            <Icon name="chevron-right" size="sm" />
          </button>
        </div>

        <div className={styles.weekdays} aria-hidden="true">
          {WEEKDAY_LABELS.map((label) => <span key={label} className={styles.weekday}>{label}</span>)}
        </div>

        <div className={styles.days}>
          {Array.from({ length: monthFirstOffset }, (_, index) => <span key={`blank-${index}`} aria-hidden="true" />)}
          {visibleDays.map((day) => {
            const isToday = day === today;
            const isSelected = day === activeSelected;
            const info = daysByDate.get(day);
            const hasTrack = info?.has_daily_track === true;
            const hasReport = info?.has_report === true;
            const scheduleCount = info?.schedule_count ?? 0;
            return (
              <button
                key={day}
                type="button"
                className={styles.dateLink}
                aria-label={day}
                aria-pressed={isSelected}
                data-nc-today={isToday ? '' : undefined}
                data-nc-selected={isSelected ? '' : undefined}
                data-nc-has-track={hasTrack ? '' : undefined}
                data-nc-has-report={hasReport ? '' : undefined}
                data-nc-schedule-count={scheduleCount > 0 ? String(scheduleCount) : undefined}
                onClick={() => selectDay(day)}
              >
                <span className={`${styles.dateBadge} ${isSelected ? styles.selectedDate : ''}`}>
                  <span className={`${styles.dayNumber} ${isToday ? styles.todayNumber : ''}`}>
                    {Number(day.slice(8, 10))}
                  </span>
                  {(hasTrack || hasReport || scheduleCount > 0) && (
                    <span className={styles.dayDots} aria-hidden="true">
                      {(hasTrack || hasReport) && (
                        <span className={`${styles.dayDot} ${hasReport ? styles.reportDot : styles.trackDot}`} />
                      )}
                      {scheduleCount > 0 && <span className={`${styles.dayDot} ${styles.scheduleDot}`} />}
                    </span>
                  )}
                </span>
              </button>
            );
          })}
        </div>

        {selectedSchedules !== undefined && selectedSchedules.length > 0 && (
          <section className={styles.agenda} aria-label="Scheduled wakes">
            <h3 className={styles.agendaHeading}>Scheduled · {selectedSchedules.length}</h3>
            <ul className={styles.agendaList}>
              {selectedSchedules.map((item) => (
                <li key={item.id} className={styles.agendaItem}>
                  <span className={styles.agendaTime}>
                    {formatScheduleTime(item.next_fire_at_ms, scheduleTimeZone ?? item.timezone)}
                  </span>
                  <span className={styles.agendaBody}>
                    <span className={styles.agendaTitle}>{item.title}</span>
                    <span className={styles.agendaMeta}>
                      {onOpenTrack !== undefined ? (
                        <button
                          type="button"
                          className={styles.agendaTrackButton}
                          onClick={() => onOpenTrack(item.track_id)}
                        >
                          {item.track_title || item.track_id}
                        </button>
                      ) : (
                        <span>{item.track_title || item.track_id}</span>
                      )}
                      {item.schedule_kind === 'cron' && (
                        <span className={styles.agendaCron}>{item.cron_expr ?? 'cron'}</span>
                      )}
                    </span>
                  </span>
                </li>
              ))}
            </ul>
          </section>
        )}
      </section>
    </PanelModule>
  );
}
