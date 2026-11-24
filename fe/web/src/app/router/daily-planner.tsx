import { queryKeys } from '../providers/query-keys.ts';
import { DailyCalendar,type CalendarViewMode } from '../../features/today/daily-calendar.tsx';
import { CenteredComposerIntroduction } from '../../ui/drawer/centered-composer-introduction.tsx';
import { EmptyState } from '../../ui/empty-state/public.tsx';
import { Icon } from '../../ui/icon/public.tsx';
import { useCompactViewport } from '../../ui/viewport/public.ts';
import styles from './daily-planner.module.css';
// Reuse the Track route body; this composition only resolves dates and report evidence.
import { useInfiniteQuery,useQuery } from '@tanstack/react-query';
import { useCallback,useLayoutEffect,useRef,type ReactNode,type RefObject } from 'react';
import type { TrackAggregateSnapshotDto } from "../../../../core/api/perga-v1.ts";
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { dailyTrackOperation,isDailyDate,localDailyDate,reportChangesOperation,reportEditsOperation,shiftDailyDate,startOfDailyWeek,todayCalendarOperation,type ReportChange } from '../../../../core/domain/daily-planner.ts';
import { ApiError } from '../../../../core/domain/failure-class.ts';
import { readErrorText } from '../../../../core/domain/read-failure.ts';
import { ReportChangeDetails,ReportEditDetails } from '../../features/report/changes/public.tsx';
import { ReportDetails } from '../../features/report/document/details.tsx';
import { ErrorBox } from '../../ui/error-box/public.tsx';
import { PanelCard } from '../../ui/panel-card/public.tsx';
import { useState } from '../../ui/state/public.ts';
import { runOperation } from '../providers/query-operation.ts';
import { trackDetailQueryOptions } from '../providers/track-queries.ts';

type DailyCenterView = 'assistant' | 'changes' | 'supervisor';

export type DailyCenterPresentation = Readonly<{
  title: string;
  presented: boolean;
  onOpenAssistant: () => void;
  footerPrefix: ReactNode;
  emptyIntroduction?: ReactNode;
  render: (assistant: ReactNode) => ReactNode;
}>;

export function DailyTodayRoute({ transport, unauthorized, selectedDate, onOpenTrack, onSelectDate, renderTrack, }: Readonly<{
  transport: ApiTransportPort; unauthorized: UnauthorizedChannel; selectedDate?: string;
  onOpenTrack: (trackId: string) => void;
  onSelectDate?: (date: string | undefined) => void;
  renderTrack: (detail: TrackAggregateSnapshotDto, evidence: ReactNode, panelContent: ReactNode, intro: ReactNode, supervisor: ReactNode, center?: DailyCenterPresentation) => ReactNode;
}>) {
  const [view, setView] = useState<DailyCenterView>('assistant');
  const previousView = useRef(view);
  const restoreGuide = useRef(false);
  const backRef = useRef<HTMLButtonElement>(null);
  const changesGuide = useRef<HTMLButtonElement>(null);
  const supervisorGuide = useRef<HTMLButtonElement>(null);
  useLayoutEffect(() => {
    if (previousView.current === view) return;
    const from = previousView.current;
    previousView.current = view;
    if (view !== 'assistant') backRef.current?.focus();
    else if (restoreGuide.current) (from === 'changes' ? changesGuide.current : supervisorGuide.current)?.focus();
    restoreGuide.current = false;
  }, [view]);
  const showSupervisor = false;
  const isDefaultToday = selectedDate === undefined;
  const isInvalidDate = selectedDate !== undefined && !isDailyDate(selectedDate);
  const validSelectedDate = isInvalidDate ? undefined : selectedDate;
  const [calendarMode, setCalendarMode] = useState<CalendarViewMode>('week');
  const [knownServerToday, setKnownServerToday] = useState<string | undefined>(undefined);
  const [visibleRange, setVisibleRange] = useState(() => {
    const start = startOfDailyWeek(validSelectedDate ?? localDailyDate());
    return { start, end: shiftDailyDate(start, 6) };
  });
  const handleRangeChange = useCallback((next: Readonly<{ start: string; end: string }>) => {
    setVisibleRange((current) => (current.start === next.start && current.end === next.end ? current : next));
  }, [setVisibleRange]);
  const daily = useQuery({
    queryKey: queryKeys.dailyPlanner(validSelectedDate ?? (isInvalidDate ? 'invalid' : 'today')),
    enabled: !isInvalidDate,
    queryFn: async () => {
      try {
        return await runOperation(transport, dailyTrackOperation(validSelectedDate), unauthorized);
      } catch (error) {
        if (error instanceof ApiError && error.failure.kind === 'http' && error.failure.status === 404) {
          return null;
        }
        throw error;
      }
    },
    refetchInterval: 30_000,
  });
  const calendar = useQuery({
    queryKey: queryKeys.dailyCalendar(visibleRange.start, visibleRange.end),
    enabled: true,
    queryFn: () => runOperation(transport, todayCalendarOperation(visibleRange.start, visibleRange.end), unauthorized),
    refetchInterval: 30_000,
  });
  const discoveredToday = calendar.data?.today ?? (isDefaultToday ? daily.data?.date : undefined);
  if (discoveredToday !== undefined && discoveredToday !== knownServerToday) {
    setKnownServerToday(discoveredToday);
  }
  const serverToday = discoveredToday ?? knownServerToday;
  const handleSelectDate = useCallback((day: string) => {
    if (onSelectDate === undefined) return;
    const nextDay = serverToday !== undefined && day === serverToday ? undefined : day;
    if (nextDay === validSelectedDate && !isInvalidDate) return;
    onSelectDate(nextDay);
  }, [isInvalidDate, onSelectDate, serverToday, validSelectedDate]);
  const calendarErrorNotice = calendar.isError
    ? readErrorText(calendar.error, 'Calendar data could not be loaded.')
    : undefined;
  const calendarNode = (
    <DailyCalendar
      today={serverToday}
      selectedDate={isInvalidDate ? null : (validSelectedDate ?? serverToday)}
      mode={calendarMode}
      onModeChange={setCalendarMode}
      days={calendar.data?.days}
      schedules={calendar.data?.schedules}
      timeZone={calendar.data?.timeZone}
      errorNotice={calendarErrorNotice}
      onSelectDate={onSelectDate === undefined ? undefined : handleSelectDate}
      onVisibleRangeChange={handleRangeChange}
      onOpenTrack={onOpenTrack}
    />
  );
  const trackId = daily.data?.trackId;
  const detail = useQuery({ ...trackDetailQueryOptions(transport, trackId ?? '', unauthorized), enabled: trackId !== undefined });
  const supervisor = false;
  const withCalendarShell = (content: ReactNode) => (
    <>{supervisor}{calendarNode !== undefined ? (
      <div className={styles.missingDayLayout}>
        <div className={styles.missingDayMain}>{content}</div>
        <aside className={styles.missingDayAside}><PanelCard>{calendarNode}</PanelCard></aside>
      </div>
    ) : content}</>
  );
  if (isInvalidDate) return withCalendarShell(<ErrorBox message={`Invalid date "${selectedDate}". Expected YYYY-MM-DD.`} onRetry={() => onSelectDate?.(undefined)} />);
  if (daily.isError) return withCalendarShell(<ErrorBox message={readErrorText(daily.error, 'The daily Planner is unavailable.')} onRetry={() => { void daily.refetch(); }} />);
  if (daily.isPending) return withCalendarShell(<p role="status">Loading daily Planner…</p>);
  if (daily.data === null) {
    const emptyNotice = isDefaultToday
      ? <p role="status">Preparing today’s Track…</p>
      : <EmptyState variant="panel" icon={<Icon name="file" />} title="No daily Track was created for this date." description="No daily log recorded for this date yet." />;
    return withCalendarShell(emptyNotice);
  }
  if (detail.isError) return withCalendarShell(<ErrorBox message={readErrorText(detail.error, 'The daily Track could not be loaded.')} onRetry={() => { void detail.refetch(); }} />);
  if (detail.data === undefined || detail.data.track.id !== trackId) return withCalendarShell(<p role="status">Loading daily Track…</p>);
  const date = daily.data.date;
  return renderTrack({ ...detail.data, track: { ...detail.data.track, title: date } },
    null,
    calendarNode,
    <header className={styles.reportIntro}><h2>{isDefaultToday ? 'Today’s focus' : 'Focus for this day'}</h2><p>Keep your work clear, calm, and well-ordered.</p></header>,
    supervisor,
    isDefaultToday ? {
      title: 'Today Assistant',
      presented: view === 'assistant',
      onOpenAssistant: () => setView('assistant'),
      emptyIntroduction: <CenteredComposerIntroduction title="What would you like to move forward today?" />,
      footerPrefix: null,
      render: (assistant) => <TodayCenter view={view} backRef={backRef} onBack={() => { restoreGuide.current = true; setView('assistant'); }} assistant={assistant}
        changes={<DailyReportChanges key={date} standalone isToday date={shiftDailyDate(date, -1)} transport={transport} unauthorized={unauthorized} onOpenTrack={onOpenTrack} />}
        supervisor={showSupervisor ? supervisor : undefined} />,
    } : undefined);
}

/** View state is visit-local; keep the live conversation mounted so its draft and history survive. */
function TodayCenter({ assistant, changes, supervisor, view, backRef, onBack }: Readonly<{
  view: DailyCenterView;
  backRef: RefObject<HTMLButtonElement | null>;
  onBack: () => void;
  assistant: ReactNode;
  changes: ReactNode;
  supervisor?: ReactNode;
}>) {
  const compact = useCompactViewport();
  return <div className={styles.center}>
    <div className={styles.assistant} hidden={view !== 'assistant'}>{assistant}</div>
    {!compact && view !== 'assistant' && <section className={styles.subview} aria-label={view === 'changes' ? 'Report changes' : 'Daily Supervisor view'}>
      <div className={styles.subviewColumn}>
        <button ref={backRef} type="button" className={styles.backToToday} onClick={onBack}><Icon name="arrow-left" size="sm" />Back to Today Assistant</button>
        {view === 'changes' ? changes : supervisor}
      </div>
    </section>}
  </div>;
}

function DailyReportChanges({ date, isToday, transport, unauthorized, onOpenTrack, standalone = false }: Readonly<{
  date: string; isToday: boolean; standalone?: boolean; transport: ApiTransportPort; unauthorized: UnauthorizedChannel; onOpenTrack: (trackId: string) => void;
}>) {
  const compactViewport = useCompactViewport();
  const [open, setOpen] = useState(standalone);
  const changes = useInfiniteQuery({ queryKey: queryKeys.dailyReportChanges(date), enabled: open,
    initialPageParam: undefined as Readonly<{ cursor: string; through: number }> | undefined,
    queryFn: ({ pageParam }) => runOperation(transport, reportChangesOperation(date, pageParam), unauthorized),
    getNextPageParam: (last) => last.nextCursor === null ? undefined : { cursor: last.nextCursor, through: last.throughEventId },
  });
  const content = <>
    {changes.isPending && open && <p role="status">Loading report changes…</p>}
    {changes.isError && <ErrorBox message={readErrorText(changes.error, 'Report changes could not be loaded.')} onRetry={() => { void changes.refetch(); }} />}
    {!changes.isError && changes.data?.pages[0]?.changes.length === 0 && <EmptyState variant="compact" icon={<Icon name="file" />} title="No report changes recorded for visible Tracks on this day." description="Report updates for this date appear here." />}
    {changes.data?.pages.flatMap((page) => page.changes.map((change) => <ReportChangeItem key={change.trackId} card={standalone} change={change}
      date={date} timeZone={page.timezone} through={page.throughEventId} transport={transport} unauthorized={unauthorized} onOpenTrack={onOpenTrack} />))}
    {changes.hasNextPage && <button type="button" disabled={changes.isFetchingNextPage} onClick={() => { void changes.fetchNextPage(); }}>Load more reports</button>}
  </>;
  if (standalone) return <><h2 className={styles.subviewTitle}>Report changes · {date}</h2>{content}</>;
  return <ReportDetails title={compactViewport ? (isToday ? 'Yesterday’s updates' : 'Previous day’s updates') : 'Report changes'} meta={date} layout="appendix" variant="section" onToggle={setOpen}>
    {(close) => <>
      {open && <div className={styles.subviewHeader}>
        <button type="button" className={styles.backToToday} onClick={close}>
          <Icon name="arrow-left" size="sm" />
          <span>Back to Today</span>
        </button>
      </div>}
      {content}
    </>}
  </ReportDetails>;
}

function ReportChangeItem({ card, change, date, timeZone, through, transport, unauthorized, onOpenTrack }: Readonly<{
  card?: boolean; change: ReportChange; date: string; timeZone: string; through: number; transport: ApiTransportPort;
  unauthorized: UnauthorizedChannel; onOpenTrack: (trackId: string) => void;
}>) {
  const [open, setOpen] = useState(false);
  const edits = useInfiniteQuery({ queryKey: queryKeys.dailyReportEdits(date, change.trackId, through), enabled: open,
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam }) => runOperation(transport, reportEditsOperation(date, change.trackId, through, pageParam), unauthorized),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
  return <ReportChangeDetails card={card} change={change} onOpenTrack={onOpenTrack} onToggleEdits={setOpen}>
    {edits.isPending && open && <p role="status">Loading edits…</p>}
    {edits.isError && <ErrorBox message={readErrorText(edits.error, 'Edits could not be loaded.')} onRetry={() => { void edits.refetch(); }} />}
    {edits.data?.pages.flatMap((page) => page.edits.map((entry) => <ReportEditDetails key={entry.eventId}
      entry={entry} timeZone={timeZone} onOpenTrack={onOpenTrack} />))}
    {edits.hasNextPage && <button type="button" disabled={edits.isFetchingNextPage} onClick={() => { void edits.fetchNextPage(); }}>Load more edits</button>}
  </ReportChangeDetails>;
}
