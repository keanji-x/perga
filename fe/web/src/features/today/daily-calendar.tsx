import { SegmentedControl, SegmentedControlItem } from '@astryxdesign/core/SegmentedControl';
import { Icon } from '../../ui/icon/public.tsx';
import { PanelModule } from '../../ui/panel-card/public.tsx';
import { useState } from '../../ui/state/public.ts';
import styles from './daily-calendar.module.css';

type CalendarViewMode = 'week' | 'month';

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

const WEEKDAY_LABELS = Object.freeze(['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'] as const);

/** Visit-local week/month visual anchor; clicking a day updates selection in place. */
export function DailyCalendar({ today = localDay() }: Readonly<{ today?: string }>) {
  const [selected, setSelected] = useState(today);
  const [mode, setMode] = useState<CalendarViewMode>('week');
  const [anchorDate, setAnchorDate] = useState(today);

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

  const visibleDays: readonly string[] = mode === 'week'
    ? Array.from({ length: 7 }, (_, index) => addDays(startOfWeekMonday(anchorDate), index))
    : Array.from(
        { length: new Date(Date.UTC(year, monthNumber, 0)).getUTCDate() },
        (_, index) => `${anchorMonth}-${String(index + 1).padStart(2, '0')}`,
      );

  const monthTitle = new Intl.DateTimeFormat('en-US', { month: 'long', year: 'numeric', timeZone: 'UTC' })
    .format(new Date(`${anchorMonth}-01T12:00:00Z`));
  const showTodayJump = selected !== today || (mode === 'week'
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
              onClick={() => { setSelected(today); setAnchorDate(today); }}
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
            const isSelected = day === selected;
            return (
              <button
                key={day}
                type="button"
                className={styles.dateLink}
                aria-label={day}
                aria-pressed={isSelected}
                data-nc-today={isToday ? '' : undefined}
                data-nc-selected={isSelected ? '' : undefined}
                onClick={() => { setSelected(day); setAnchorDate(day); }}
              >
                <span className={`${styles.dateBadge} ${isSelected ? styles.selectedDate : ''}`}>
                  <span className={`${styles.dayNumber} ${isToday ? styles.todayNumber : ''}`}>
                    {Number(day.slice(8, 10))}
                  </span>
                </span>
              </button>
            );
          })}
        </div>
      </section>
    </PanelModule>
  );
}
