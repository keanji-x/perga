import '../../styles/entry.css';
import { fixtureSnapshot, fixtureTrack } from './session-contract-fixture.ts';
import emptyStyles from '../../ui/empty-state/empty-state.module.css';
import { noop, ok, area, navigationActions, mount, EmptySearchComposer, mountEmptyFileViewer, mountEmptyDirectoryBrowser, mountReportComponentStates, mountMissingReportSources, mountEmptyReportReference } from './normal-empty-fixtures.tsx';
import { assertNormalEmptySurface } from './normal-empty-assertions.ts';
import { captureEmptySurface, waitForEmptySurfaceRendering } from './normal-empty-capture.ts';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import { createUiPreferences } from '../providers/ui-preferences.tsx';
import { renderDailyFixture } from './daily-planner-fixture.tsx';
import { AreaLevelTwo } from '../shell/mobile/area-level-two.tsx';
import { WorkspaceLevelOne } from '../shell/mobile/workspace-level-one.tsx';
import { ChatEmpty } from '../../features/chat/thread/public.tsx';
import { ReportDocument } from '../../features/report/document/public.tsx';
it('catalog historical day without Track', async () => {
  await page.viewport(1440,900);
  renderDailyFixture({ initial:'/?day=2026-10-18' });
  await expect.element(page.getByText('No daily Track was created for this date.')).toBeVisible();
  await shot('27-historical-day-no-track', ["No daily Track was created for this date."]);
});
it('catalog missing report reference block', async () => {
  await page.viewport(800,400);
  mountEmptyReportReference(true);
  await expect.element(page.getByText('The referenced section could not be found in this report.')).toBeVisible();
  await shot('30-reference-missing-block', []);
});


afterEach(() => { cleanup(); document.body.replaceChildren(); });
async function shot(name: string, expectedTitles: readonly string[]) {
  await waitForEmptySurfaceRendering();
  assertNormalEmptySurface(expectedTitles, emptyStyles);
  await captureEmptySurface(name);
}

it.each([1440, 390])('catalog main report and sidebar %i', async width => {
  await page.viewport(width, 900);
  renderDailyFixture({ initial: '/track/project', reply: request => request.path === '/api/v1/tracks/project' ? ok(fixtureSnapshot(fixtureTrack('project', { areaId: 'project-area', title: '新项目' }))) : undefined });
  await expect.element(page.getByRole('heading', { name: 'This page becomes your living report' })).toBeVisible();
  await shot('01-report-' + width, []);
  if (width === 390) {
    await page.getByRole('button', { name: 'Track actions', exact: true }).click();
    await page.getByRole('menuitem', { name: 'Conversations', exact: true }).click();
    await expect.element(page.getByText('No conversations yet')).toBeVisible();
    await shot('12-history-current', ["No conversations yet"]);
  }
});
it.each([1440, 390])('catalog Today and conversation %i', async width => {
  await page.viewport(width, 900);
  renderDailyFixture({ initial: width >= 960 ? '/' : '/?day=2026-10-04', reportBody: '', emptyWorkspace: true });
  if (width >= 960) {
    await expect.element(page.getByRole('combobox', { name: 'Message', exact: true })).toBeVisible();
    expect(page.getByRole('heading', { name: 'Start a conversation' }).query()).toBeNull();
  } else await expect.element(page.getByRole('heading', { name: 'Focus for this day' })).toBeVisible();
  await shot('02-today-' + width, []);
  if (width !== 1440) {
    await page.getByRole('textbox', { name: 'Chat message', exact: true }).click();
  }
  if (width === 1440) await expect.element(page.getByRole('region', { name: 'Today Assistant', exact: true }).getByRole('combobox', { name: 'Message', exact: true })).toBeVisible();
  else await expect.element(page.getByRole('heading', { name: 'Start a conversation' })).toBeVisible();
  await shot('03-conversation-' + width, width === 1440 ? [] : ['Start a conversation']);
});
it('catalog heading-only report mobile', async () => {
  await page.viewport(390,844);
  mount(<ReportDocument report={{ summary:'', body:'# 概要\n\n# 今日计划', blocks:null }} empty={null} />, true);
  await expect.element(page.getByRole('heading',{name:'Progress will be compiled here'})).toBeVisible();
  await shot('06-heading-only-report', ["Progress will be compiled here"]);
});
it.each(['areas','tracks'] as const)('catalog mobile navigation %s', async view => {
  await page.viewport(390,844);
  const props = {
    ...navigationActions(), areas: view === 'areas' ? [] : [area], tracksByArea: new Map(),
    scrollRef: null, backLabel: 'Focus', onClosedExpandedChange: noop,
    onOpenTrack: noop, isUnread: () => false, readError: null, readLoading: false, onRetryRead: noop,
  };
  mount(view === 'areas'
    ? <WorkspaceLevelOne {...props} onSelectArea={noop} onOpenToday={noop} onOpenMemory={noop} />
    : <AreaLevelTwo {...props} areaId={area.id} onOpenAreaMemory={noop} />);
  await expect.element(page.getByText(view === 'areas' ? 'No areas yet' : 'No tracks in this area')).toBeVisible();
  await shot(view === 'areas' ? '07-mobile-no-areas' : '08-mobile-no-tracks', [view === 'areas' ? 'No areas yet' : 'No tracks in this area']);
});
it.each(['code','diff'] as const)('catalog file viewer %s', async tab => {
  await page.viewport(1000,650);
  mountEmptyFileViewer(tab);
  await expect.element(page.getByText(tab === 'code' ? 'Select a file to view' : 'No uncommitted changes')).toBeVisible();
  await expect.element(page.getByRole('heading', { name: 'This folder is empty', exact: true })).toBeVisible();
  await shot(tab === 'code' ? '17-files-empty-folder-and-unselected' : '18-files-no-changes', tab === 'code' ? ['This folder is empty','Select a file to view'] : ['No uncommitted changes','Select a changed file']);
});
it.each([false,true])('catalog directory browser filtered %s', async filtered => {
  await page.viewport(900,650);
  mountEmptyDirectoryBrowser(filtered);
  if (filtered) {
    await expect.element(page.getByRole('option',{name:'src'})).toBeVisible();
    await page.getByRole('combobox').fill('/work/nonexistent');
  }
  await expect.element(page.getByText(filtered ? 'No matches' : 'This folder is empty.')).toBeVisible();
  await shot(filtered ? '20-folder-search-no-matches' : '19-folder-picker-empty', [filtered ? 'No matches' : 'This folder is empty.']);
});
it.each(['mention','command'])('catalog composer search %s', async type => {
  await page.viewport(1000,700);
  mount(<div style={{position:'absolute',left:120,top:430,width:480}}><EmptySearchComposer /></div>);
  const field = page.getByRole('combobox',{name:'Message'});
  await field.click();
  await userEvent.keyboard(type === 'mention' ? '@nonexistent' : '/nonexistent');
  await expect.element(page.getByText(type === 'mention' ? 'No matches' : 'No command by that name')).toBeVisible();
  expect(page.getByRole('option').elements()).toHaveLength(0);
  expect(field.element().getAttribute('aria-activedescendant')).toBeNull();
  await userEvent.keyboard('{ArrowDown}');
  expect(field.element().textContent).toBe(type === 'mention' ? '@nonexistent' : '/nonexistent');
  await shot(type === 'mention' ? '21-mention-no-matches' : '22-command-no-matches', [type === 'mention' ? 'No matches' : 'No command by that name']);
});
it('catalog report data and history component states', async () => {
  await page.viewport(1100,950);
  mountReportComponentStates();
  await shot('23-report-component-states', ["No data yet"]);
});
it('catalog waiting conversation', async () => {
  await page.viewport(650,500);
  mount(<div style={{display:'flex',height:420,background:'var(--paper)',borderRadius:12}}><ChatEmpty working /></div>,true);
  await expect.element(page.getByRole('heading',{name:'Organizing your thoughts'})).toBeVisible();
  await shot('24-conversation-working', []);
});
it('catalog report reference without document', async () => {
  await page.viewport(800,400);
  mountEmptyReportReference(false);
  await expect.element(page.getByText('This report has no content yet.')).toBeVisible();
  await shot('25-reference-empty', ["This report has no content yet."]);
});


it('catalog missing report sources', async () => {
  await page.viewport(1100,1100);
  mountMissingReportSources();
  await expect.element(page.getByRole('heading',{name:'Citation anchor missing',exact:true})).toBeVisible();
  await shot('31-source-missing-states', []);
});

it('catalog confirmed empty desktop shortcut groups', async () => {
  await page.viewport(1440,1000);
  const preferences = createUiPreferences();
  for (const id of ['focus','pinned'] as const) {
    preferences.setSidebarGroupVisible(id,true);
    preferences.setSidebarGroupExpanded(id,true);
  }
  renderDailyFixture({emptyWorkspace:true,reportBody:'',uiPreferences:preferences});
  await expect.element(page.getByRole('combobox', { name: 'Message', exact: true })).toBeVisible();
  expect(page.getByRole('heading', { name: 'Start a conversation', exact: true }).query()).toBeNull();
  expect(page.getByRole('group',{name:'Focus',exact:true}).query()).toBeNull();
  expect(page.getByRole('group',{name:'Pinned',exact:true}).query()).toBeNull();
  await shot('34-desktop-empty-shortcuts', []);
});
