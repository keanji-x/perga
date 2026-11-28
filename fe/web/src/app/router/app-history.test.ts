import { createMemoryHistory } from '@tanstack/react-router';
import { describe, expect, it } from 'vitest';
import type { Area } from '../../../../core/domain/area.ts';
import { createAppHistory } from './app-history.tsx';
import { mobileBackLabel } from './mobile-back.ts';
import { isNewTrackPath } from './navigation.ts';

function setup(initialEntries: string[]) {
  const history = createMemoryHistory({ initialEntries });
  const stack = createAppHistory(history, ({ pathname, search }) => ({ pathname, searchStr: search }));
  const stop = stack.observe();
  return { history, stack, stop };
}

describe('App history', () => {
  it('observes pushes, replacements and both traversal directions by index', () => {
    const { history, stack, stop } = setup(['/']);
    expect(stack.snapshot()).toEqual({ previousEntry: null, canGoBack: false, lastAction: null });

    history.push('/workspace');
    expect(stack.snapshot()).toEqual({ previousEntry: { pathname: '/', searchStr: '' }, canGoBack: true, lastAction: 'PUSH' });
    history.push('/area/work/new');
    history.replace('/track/new');
    expect(stack.snapshot()).toEqual({ previousEntry: { pathname: '/workspace', searchStr: '' }, canGoBack: true, lastAction: 'REPLACE' });
    history.back();
    expect(stack.snapshot()).toEqual({ previousEntry: { pathname: '/', searchStr: '' }, canGoBack: true, lastAction: 'BACK' });
    history.forward();
    expect(stack.snapshot().lastAction).toBe('FORWARD');
    expect(history.location.pathname).toBe('/track/new');
    history.go(-2);
    expect(stack.snapshot()).toEqual({ previousEntry: null, canGoBack: false, lastAction: 'GO' });
    stop();
    history.forward();
    expect(stack.snapshot().lastAction).toBe('GO');
  });

  it('starts at the mounted entry and lowers the floor after external traversal', () => {
    const { history, stack } = setup(['/old', '/workspace', '/track/new?card=c']);
    expect(history.canGoBack()).toBe(true);
    expect(stack.snapshot()).toMatchObject({ previousEntry: null, canGoBack: false });
    history.replace('/track/new');
    expect(stack.snapshot()).toMatchObject({ previousEntry: null, canGoBack: false });
    history.back();
    expect(stack.snapshot()).toMatchObject({ previousEntry: null, canGoBack: false });
    history.forward();
    expect(stack.snapshot()).toMatchObject({ previousEntry: { pathname: '/workspace', searchStr: '' }, canGoBack: true });
  });

  it('exits only contiguous observed Settings entries', () => {
    const { history, stack } = setup(['/track/one']);
    const settings = (entry: { pathname: string }) => entry.pathname.startsWith('/settings');
    history.push('/settings');
    history.push('/settings/planners');
    history.replace('/settings/about');
    expect(stack.stepsBackOutOf(settings)).toBe(2);
    history.go(-stack.stepsBackOutOf(settings));
    expect(history.location.pathname).toBe('/track/one');

    const cold = setup(['/unobserved', '/settings/appearance']);
    cold.history.push('/settings/about');
    expect(cold.stack.stepsBackOutOf(settings)).toBe(0);
  });

  it('exits Settings after a forward traversal skips an unobserved entry', () => {
    const history = createMemoryHistory({ initialEntries: ['/track/one'] });
    history.push('/settings');
    history.push('/settings/about');
    history.go(-2);
    const stack = createAppHistory(history, ({ pathname, search }) => ({ pathname, searchStr: search }));
    stack.observe();
    history.go(2);
    expect(history.location.pathname).toBe('/settings/about');
    const distance = stack.stepsBackOutOf((entry) => entry.pathname.startsWith('/settings'));
    expect(distance).toBe(2);
    history.go(-distance);
    expect(history.location.pathname).toBe('/track/one');
  });

  it('restores entry-specific scroll and view state and forgets discarded forward entries', () => {
    const { history, stack } = setup(['/']);
    stack.rememberScroll(0, 240);
    stack.rememberView(0, { tab: 'all' });
    history.push('/area/work');
    expect(stack.scrollTop(1)).toBe(0);
    stack.rememberScroll(1, 480);
    stack.rememberView(1, true);

    history.back();
    expect(stack.scrollTop(0)).toBe(240);
    expect(stack.readView(0)).toEqual({ tab: 'all' });
    history.forward();
    expect(stack.scrollTop(1)).toBe(480);
    expect(stack.readView(1)).toBe(true);
    history.replace('/area/work');
    expect(stack.scrollTop(1)).toBe(480);
    history.replace('/workspace');
    expect(stack.scrollTop(1)).toBe(0);
    expect(stack.readView(1)).toBeUndefined();

    stack.rememberScroll(1, 100);
    history.back();
    history.push('/area/work');
    expect(stack.scrollTop(1)).toBe(0);
  });
});

describe('Mobile Back labels', () => {
  const areas = [{ id: 'design', name: 'Design' }, { id: 'memory', name: 'Archive' }] satisfies readonly Pick<Area, 'id' | 'name'>[];
  it.each([
    ['/', '', 'Focus'], ['/', '?view=today', 'Today'], ['/', '?day=2026-10-04', 'Today'],
    ['/workspace', '', 'Workspace'], ['/area/design', '', 'Design'], ['/track/t', '?file=a', 'Report'],
    ['/memory', '', 'Memory'], ['/area/design/memory', '', 'Memory'], ['/settings/appearance', '', 'Settings'],
    ['/area/missing', '', 'Fallback'], ['/area/memory', '', 'Archive'],
  ])('labels %s%s as %s', (pathname, searchStr, label) => {
    expect(mobileBackLabel({ pathname, searchStr }, 'Fallback', areas)).toBe(label);
  });

  it('uses the logical parent when history has no predecessor', () => {
    expect(mobileBackLabel(null, 'Settings', areas)).toBe('Settings');
  });
});

it('distinguishes a draft route from an Area named new', () => {
  expect(isNewTrackPath('/area/new')).toBe(false);
  expect(isNewTrackPath('/area/new/new/')).toBe(true);
});
