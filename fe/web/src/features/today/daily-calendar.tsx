import { useEffect, useMemo } from 'react';
import { SegmentedControl, SegmentedControlItem } from '@astryxdesign/core/SegmentedControl';
import { localDailyDate, shiftDailyDate, startOfDailyWeek, type TodayCalendarDay, type TodayScheduleItem } from '../../../../core/domain/daily-planner.ts';
import { Icon } from '../../ui/icon/public.tsx';
import { PanelModule } from '../../ui/panel-card/public.tsx';
import { useState } from '../../ui/state/public.ts';
import styles from './daily-calendar.module.css';

export type CalendarViewMode = 'week' | 'month';

export type DailyCalendarProps = Readonly<{
  today?: string;
  selectedDate?: string | null;
  mode?: CalendarViewMode;
  onModeChange?: (mode: CalendarViewMode) => void;
  days?: readonly TodayCalendarDay[];
  schedules?: readonly TodayScheduleItem[];
  timeZone?: string;
  errorNotice?: string;
  onSelectDate?: (date: string) => void;
  onVisibleRangeChange?: (range: Readonly<{ start: string; end: string }>) => void;
  onOpenTrack?: (trackId: string) => void;
}>;

function formatScheduleTime(ms: number, timeZone: string): string {
  return new Intl.DateTimeFormat('en-GB', {
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
    timeZone,
  }).format(new Date(ms));
}

const WEEKDAY_LABELS = Object.freeze(['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'] as const);

/** Week/month calendar navigation with optional day indicators and scheduled wakes. */
export function DailyCalendar({
  today = localDailyDate(),
  selectedDate,
  mode: controlledMode,
  onModeChange,
  days,
  schedules,
  timeZone,
  errorNotice,
  onSelectDate,
  onVisibleRangeChange,
  onOpenTrack,
}: DailyCalendarProps) {
  const [internalSelected, setInternalSelected] = useState(selectedDate ?? today);
  const [internalMode, setInternalMode] = useState<CalendarViewMode>('week');
  const [anchorDate, setAnchorDate] = useState(selectedDate ?? today);
  const [trackedSelectedProp, setTrackedSelectedProp] = useState(selectedDate);
  const [trackedTodayProp, setTrackedTodayProp] = useState(today);

  if (selectedDate !== trackedSelectedProp || today !== trackedTodayProp) {
    setTrackedSelectedProp(selectedDate);
    setTrackedTodayProp(today);
    setAnchorDate(selectedDate ?? today);
  }

  const mode = controlledMode ?? internalMode;
  const setMode = (next: CalendarViewMode) => {
    if (controlledMode === undefined) setInternalMode(next);
    onModeChange?.(next);
  };

  const activeSelected = selectedDate === undefined ? internalSelected : selectedDate;
  const anchorMonth = anchorDate.slice(0, 7);
  const [year, monthNumber] = anchorMonth.split('-').map(Number);

  const move = (delta: number) => {
    if (mode === 'week') {
      setAnchorDate((current) => shiftDailyDate(current, delta * 7));
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
      ? Array.from({ length: 7 }, (_, index) => shiftDailyDate(startOfDailyWeek(anchorDate), index))
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

  const schedulesByDate = useMemo(() => {
    const map = new Map<string, TodayScheduleItem[]>();
    if (schedules !== undefined) {
      for (const item of schedules) {
        const list = map.get(item.date);
        if (list === undefined) map.set(item.date, [item]);
        else list.push(item);
      }
    }
    return map;
  }, [schedules]);

  const selectedDaySchedules = (activeSelected === null ? undefined : schedulesByDate.get(activeSelected)) ?? [];

  const selectDay = (day: string) => {
    if (selectedDate === undefined) setInternalSelected(day);
    setAnchorDate(day);
    if (day !== activeSelected) onSelectDate?.(day);
  };

  const monthTitle = new Intl.DateTimeFormat('en-US', { month: 'long', year: 'numeric', timeZone: 'UTC' })
    .format(new Date(`${anchorMonth}-01T12:00:00Z`));
  const showTodayJump = activeSelected !== today || (mode === 'week'
    ? startOfDailyWeek(anchorDate) !== startOfDailyWeek(today)
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
            const hasTrack = info?.hasDailyTrack === true;
            const hasReport = info?.hasReport === true;
            const scheduleCount = schedulesByDate.get(day)?.length ?? 0;
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

        {errorNotice !== undefined && (
          <p className={styles.calendarError} role="status">{errorNotice}</p>
        )}

        {selectedDaySchedules.length > 0 && (
          <section className={styles.agenda} aria-label="Scheduled wakes">
            <h3 className={styles.agendaHeading}>Scheduled · {selectedDaySchedules.length}</h3>
            <ul className={styles.agendaList}>
              {selectedDaySchedules.map((item) => (
                <li key={`${item.trackId}:${item.id}:${item.date}`} className={styles.agendaItem} data-nc-schedule-state={item.state}>
                  <span className={styles.agendaTime}>
                    {formatScheduleTime(item.nextFireAtMs, timeZone ?? item.timezone)}
                  </span>
                  <span className={styles.agendaBody}>
                    <span className={styles.agendaTitle}>{item.title}</span>
                    <span className={styles.agendaMeta}>
                      {onOpenTrack !== undefined ? (
                        <button
                          type="button"
                          className={styles.agendaTrackButton}
                          onClick={() => onOpenTrack(item.trackId)}
                        >
                          {item.trackTitle || item.trackId}
                        </button>
                      ) : (
                        <span>{item.trackTitle || item.trackId}</span>
                      )}
                      {item.scheduleKind === 'cron' && (
                        <span className={styles.agendaCron}>{item.cronExpr ?? 'cron'}</span>
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
