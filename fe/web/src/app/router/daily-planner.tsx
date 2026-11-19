import { DailyCalendar } from '../../features/today/daily-calendar.tsx';
import { CenteredComposerIntroduction } from '../../ui/drawer/centered-composer-introduction.tsx';
import { EmptyState } from '../../ui/empty-state/public.tsx';
import { Icon } from '../../ui/icon/public.tsx';
import { useCompactViewport } from '../../ui/viewport/public.ts';
import styles from './daily-planner.module.css';
// Reuse the Track route body; this composition only resolves dates and report evidence.
import { useCallback, useLayoutEffect, useRef, type ReactNode, type RefObject } from 'react';
import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import type { TrackAggregateSnapshotDto } from '../../../../core/api/schemas.ts';
import { dailyTrackOperation, isDailyDate, reportChangesOperation, reportEditsOperation, shiftDailyDate, supervisorSnapshotOperation, todayCalendarOperation, todaySchedulesOperation, type ReportChange } from '../../../../core/domain/daily-planner.ts';
import { PanelCard } from '../../ui/panel-card/public.tsx';
import { DEPLOYMENT_OWNER_USER_ID } from '../../../../core/api/auth.ts';
import { supervisorDetails, supervisorReport } from '../../../../core/domain/supervisor-view.ts';
import { SupervisorWaitChart } from '../../features/today/supervisor-wait.tsx';
import { ReportDocument } from '../../features/report/document/public.tsx';
import { useCurrentUser } from '../auth/current-user.tsx';
import { ReportDetails } from '../../features/report/document/details.tsx';
import { ReportChangeDetails, ReportEditDetails } from '../../features/report/changes/public.tsx';
import { ApiError } from '../../../../core/domain/failure-class.ts';
import { readErrorText } from '../../../../core/domain/read-failure.ts';
import { ErrorBox } from '../../ui/error-box/public.tsx';
import { useState } from '../../ui/state/public.ts';
import { runOperation, trackDetailQueryOptions } from '../providers/queries.ts';

type DailyCenterView = 'assistant' | 'changes' | 'supervisor';

export type DailyCenterPresentation = Readonly<{
  title: string;
  presented: boolean;
  onOpenAssistant: () => void;
  footerPrefix: ReactNode;
  emptyIntroduction?: ReactNode;
  render: (assistant: ReactNode) => ReactNode;
}>;

function browserLocalDay(): string {
  const now = new Date();
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
}

function initialCalendarRange(anchor: string): Readonly<{ start: string; end: string }> {
  const [year, month, day] = anchor.split('-').map(Number);
  const date = new Date(Date.UTC(year, month - 1, day));
  const mondayOffset = (date.getUTCDay() + 6) % 7;
  const start = shiftDailyDate(anchor, -mondayOffset);
  return { start, end: shiftDailyDate(start, 6) };
}

export function DailyTodayRoute({ transport, unauthorized, selectedDate, onOpenTrack, onSelectDate, renderTrack, pergaMode = false }: Readonly<{
  transport: ApiTransportPort; unauthorized: UnauthorizedChannel; selectedDate?: string; pergaMode?: boolean;
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
  const user = useCurrentUser();
  const showSupervisor = !pergaMode && user?.userId === DEPLOYMENT_OWNER_USER_ID;
  const validSelectedDate = selectedDate !== undefined && isDailyDate(selectedDate) ? selectedDate : undefined;
  const [visibleRange, setVisibleRange] = useState(() => initialCalendarRange(validSelectedDate ?? browserLocalDay()));
  const handleRangeChange = useCallback((next: Readonly<{ start: string; end: string }>) => {
    setVisibleRange((current) => (current.start === next.start && current.end === next.end ? current : next));
  }, [setVisibleRange]);
  const daily = useQuery({
    queryKey: ['daily-planner', selectedDate ?? 'today'],
    queryFn: async () => {
      try {
        return await runOperation(transport, dailyTrackOperation(selectedDate), unauthorized);
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
    queryKey: ['daily-calendar', visibleRange.start, visibleRange.end],
    enabled: pergaMode,
    queryFn: () => runOperation(transport, todayCalendarOperation(visibleRange.start, visibleRange.end), unauthorized),
    refetchInterval: 30_000,
  });
  const serverToday = calendar.data?.today ?? (selectedDate === undefined ? daily.data?.date : undefined);
  const activeCalendarDay = validSelectedDate ?? serverToday ?? browserLocalDay();
  const daySchedules = useQuery({
    queryKey: ['daily-schedules', activeCalendarDay],
    enabled: pergaMode && isDailyDate(activeCalendarDay),
    queryFn: () => runOperation(transport, todaySchedulesOperation(activeCalendarDay), unauthorized),
    refetchInterval: 30_000,
  });
  const handleSelectDate = useCallback((day: string) => {
    if (onSelectDate === undefined) return;
    onSelectDate(serverToday !== undefined && day === serverToday ? undefined : day);
  }, [onSelectDate, serverToday]);
  const calendarNode = pergaMode ? (
    <DailyCalendar
      today={serverToday}
      selectedDate={validSelectedDate ?? serverToday}
      days={calendar.data?.days}
      selectedSchedules={daySchedules.data?.schedules}
      scheduleTimeZone={daySchedules.data?.time_zone ?? calendar.data?.time_zone}
      onSelectDate={onSelectDate === undefined ? undefined : handleSelectDate}
      onVisibleRangeChange={handleRangeChange}
      onOpenTrack={onOpenTrack}
    />
  ) : null;
  const trackId = daily.data?.track_id;
  const detail = useQuery({ ...trackDetailQueryOptions(transport, trackId ?? '', unauthorized), enabled: trackId !== undefined });
  const supervisor = !pergaMode && selectedDate === undefined && <DailySupervisor transport={transport} unauthorized={unauthorized} />;
  const withSupervisor = (content: ReactNode) => <>{supervisor}{content}</>;
  if (daily.isError) return withSupervisor(<ErrorBox message={readErrorText(daily.error, 'The daily Planner is unavailable.')} onRetry={() => { void daily.refetch(); }} />);
  if (daily.isPending) return withSupervisor(<p role="status">Loading daily Planner…</p>);
  if (daily.data === null) {
    const emptyNotice = selectedDate === undefined
      ? <p role="status">Preparing today’s Track…</p>
      : <EmptyState variant="panel" icon={<Icon name="file" />} title="No daily Track was created for this date." description="这一天还没有日报记录。" />;
    if (pergaMode) {
      return withSupervisor(
        <div className={styles.missingDayLayout}>
          <div className={styles.missingDayMain}>{emptyNotice}</div>
          <aside className={styles.missingDayAside}><PanelCard>{calendarNode}</PanelCard></aside>
        </div>,
      );
    }
    return withSupervisor(emptyNotice);
  }
  if (detail.isError) return withSupervisor(<ErrorBox message={readErrorText(detail.error, 'The daily Track could not be loaded.')} onRetry={() => { void detail.refetch(); }} />);
  if (detail.data === undefined || detail.data.track.id !== trackId) return withSupervisor(<p role="status">Loading daily Track…</p>);
  const date = daily.data.date;
  return renderTrack({ ...detail.data, track: { ...detail.data.track, title: date } },
    pergaMode ? null : <DailyReportChanges key={date} isToday={selectedDate === undefined} date={shiftDailyDate(date, -1)}
      transport={transport} unauthorized={unauthorized} onOpenTrack={onOpenTrack} />,
    calendarNode,
    <header className={styles.reportIntro}><h2>{selectedDate === undefined ? '今天的重点' : '当天的重点'}</h2><p>让工作更轻松，也更有条理。</p></header>,
    supervisor,
    selectedDate === undefined ? {
      title: 'Today Assistant',
      presented: view === 'assistant',
      onOpenAssistant: () => setView('assistant'),
      emptyIntroduction: <CenteredComposerIntroduction title="今天想推进什么？" />,
      footerPrefix: pergaMode ? null : <nav className={styles.guide} aria-label="Today views">
        <button ref={changesGuide} type="button" onClick={() => setView('changes')}><span>Report changes</span><Icon name="chevron-right" size="sm" /></button>
        {showSupervisor && <>
          <span className={styles.guideSep} aria-hidden="true">·</span>
          <button ref={supervisorGuide} type="button" onClick={() => setView('supervisor')}><span>Daily Supervisor</span><Icon name="chevron-right" size="sm" /></button>
        </>}
      </nav>,
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

function DailySupervisor({ transport, unauthorized }: Readonly<{ transport: ApiTransportPort; unauthorized: UnauthorizedChannel }>) {
  const user = useCurrentUser();
  const isOwner = user?.userId === DEPLOYMENT_OWNER_USER_ID;
  const snapshot = useQuery({
    queryKey: ['daily-supervisor', user?.userId],
    queryFn: () => runOperation(transport, supervisorSnapshotOperation(), unauthorized),
    enabled: isOwner,
    refetchInterval: isOwner ? 30_000 : false,
  });
  if (!isOwner) return null;

  if (snapshot.isError) return <ErrorBox message="无法读取当前实例状态。" onRetry={() => { void snapshot.refetch(); }} />;
  if (snapshot.data === undefined) return <p role="status">正在读取实例状态…</p>;

  return <section aria-label="Daily Supervisor">
    <ReportDocument report={supervisorReport(snapshot.data)} empty={null} />
    <SupervisorWaitChart writes={snapshot.data.database_writes} observedAt={snapshot.data.observed_at_ms}
      bucketMs={snapshot.data.write_bucket_ms} bucketCount={snapshot.data.write_bucket_count} />
    <ReportDetails title="实例明细" layout="appendix">
      <dl>{supervisorDetails(snapshot.data).map(({ label, value }) =>
        <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}</dl>
    </ReportDetails>
  </section>;
}

function DailyReportChanges({ date, isToday, transport, unauthorized, onOpenTrack, standalone = false }: Readonly<{
  date: string; isToday: boolean; standalone?: boolean; transport: ApiTransportPort; unauthorized: UnauthorizedChannel; onOpenTrack: (trackId: string) => void;
}>) {
  const compactViewport = useCompactViewport();
  const [open, setOpen] = useState(standalone);
  const changes = useInfiniteQuery({ queryKey: ['daily-report-changes', date], enabled: open,
    initialPageParam: undefined as Readonly<{ cursor: string; through: number }> | undefined,
    queryFn: ({ pageParam }) => runOperation(transport, reportChangesOperation(date, pageParam), unauthorized),
    getNextPageParam: (last) => last.next_cursor === null ? undefined : { cursor: last.next_cursor, through: last.through_event_id },
  });
  const content = <>
    {changes.isPending && open && <p role="status">Loading report changes…</p>}
    {changes.isError && <ErrorBox message={readErrorText(changes.error, 'Report changes could not be loaded.')} onRetry={() => { void changes.refetch(); }} />}
    {!changes.isError && changes.data?.pages[0]?.changes.length === 0 && <EmptyState variant="compact" icon={<Icon name="file" />} title="No report changes recorded for visible Tracks on this day." description="这一天的报告更新会显示在这里。" />}
    {changes.data?.pages.flatMap((page) => page.changes.map((change) => <ReportChangeItem key={change.track_id} card={standalone} change={change}
      date={date} timeZone={page.timezone} through={page.through_event_id} transport={transport} unauthorized={unauthorized} onOpenTrack={onOpenTrack} />))}
    {changes.hasNextPage && <button type="button" disabled={changes.isFetchingNextPage} onClick={() => { void changes.fetchNextPage(); }}>Load more reports</button>}
  </>;
  if (standalone) return <><h2 className={styles.subviewTitle}>Report changes · {date}</h2>{content}</>;
  return <ReportDetails title={compactViewport ? (isToday ? '昨天的更新' : '前一天的更新') : 'Report changes'} meta={date} layout="appendix" variant="section" onToggle={setOpen}>
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
  const edits = useInfiniteQuery({ queryKey: ['daily-report-edits', date, change.track_id, through], enabled: open,
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam }) => runOperation(transport, reportEditsOperation(date, change.track_id, through, pageParam), unauthorized),
    getNextPageParam: (last) => last.next_cursor ?? undefined,
  });
  return <ReportChangeDetails card={card} change={change} onOpenTrack={onOpenTrack} onToggleEdits={setOpen}>
    {edits.isPending && open && <p role="status">Loading edits…</p>}
    {edits.isError && <ErrorBox message={readErrorText(edits.error, 'Edits could not be loaded.')} onRetry={() => { void edits.refetch(); }} />}
    {edits.data?.pages.flatMap((page) => page.edits.map((entry) => <ReportEditDetails key={entry.event_id}
      entry={entry} timeZone={timeZone} onOpenTrack={onOpenTrack} />))}
    {edits.hasNextPage && <button type="button" disabled={edits.isFetchingNextPage} onClick={() => { void edits.fetchNextPage(); }}>Load more edits</button>}
  </ReportChangeDetails>;
}
