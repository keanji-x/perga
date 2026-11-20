import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { toTrack, trackWireSchema, type Track } from '../../../../../core/domain/track.js';
import { TemplateProtocolBar } from './public.tsx';

afterEach(cleanup);

function track(template: Partial<Track> = {}): Track {
  return { ...toTrack(trackWireSchema.parse({ id: 't', area_id: 'a', title: 'Report', sort: 0,
    workspace: { isolation: 'managed', kind: 'managed', path: '', frozen_at: null }, created_at: 1, updated_at: 1 })), ...template };
}

describe('TemplateProtocolBar', () => {
  it('renders nothing without a template', () => {
    expect(render(<TemplateProtocolBar track={track()} />).container.innerHTML).toBe('');
  });

  it('shows a compact Dev header and keeps optional strips absent', () => {
    render(<TemplateProtocolBar track={track({ templateId: 'dev', templateTitle: 'Dev' })} />);
    expect(screen.getByText('Dev')).toBeTruthy();
    expect(screen.getByRole('button', { name: /Details/ })).toBeTruthy();
    expect(screen.queryByRole('tab')).toBeNull();
    expect(screen.queryByRole('img')).toBeNull();
  });

  it('shows pending notice and every badge with its tone', () => {
    render(<TemplateProtocolBar track={track({ templateId: 'daily', templateHeaderView: {
      badges: Array.from({ length: 9 }, (_, i) => ({ label: `Badge ${i}`, tone: 'warn' })),
      notice: { text: '数据待更新', tone: 'warn' },
    } })} />);
    expect(screen.getByText('数据待更新').className).toContain('warn');
    expect(screen.getAllByText(/Badge \d/)).toHaveLength(9);
  });

  it('renders all metrics, optional details and meaningful sparklines', () => {
    render(<TemplateProtocolBar track={track({ templateId: 'metrics', templateHeaderView: {
      metrics: [
        { label: 'Revenue', value: '42', delta: '+2', sub: 'Today', tone: 'success', spark: [1, 2, 4] },
        { label: 'Flat', value: '7', spark: [7, 7] },
        { label: 'Pending', value: '—', spark: [1] },
        ...Array.from({ length: 8 }, (_, i) => ({ label: `Extra ${i}`, value: String(i) })),
      ],
    } })} />);
    expect(screen.getByText('42')).toBeTruthy();
    expect(screen.getByText('+2')).toBeTruthy();
    expect(screen.getByText('Today')).toBeTruthy();
    expect(screen.getByText('Extra 7')).toBeTruthy();
    expect(screen.getAllByRole('img')).toHaveLength(2);
    expect(screen.getByRole('img', { name: 'Revenue trend' }).querySelector('polyline')?.getAttribute('points')).toBe('0,28 50,19.333333333333336 100,2');
    expect(screen.getByRole('img', { name: 'Flat trend' }).innerHTML).not.toMatch(/NaN|Infinity/);
  });

  it('opens dynamic drawer tabs and supports keyboard tab navigation', () => {
    render(<TemplateProtocolBar track={track({ templateId: 'dev', templateInputs: { repository: 'perga', options: { review: true } },
      templateHeaderView: { rules: [{ label: 'Review', text: 'Check tests' }] },
    })} />);
    fireEvent.click(screen.getByRole('button', { name: /Details/ }));
    expect(screen.getAllByRole('tab').map(tab => tab.textContent)).toEqual(['Rules', 'Schedules', 'Inputs']);
    expect(screen.getByRole('tabpanel').textContent).toContain('Check tests');
    fireEvent.keyDown(screen.getByRole('tab', { name: 'Rules' }), { key: 'ArrowRight' });
    expect(screen.getByRole('tabpanel').textContent).toContain('Recent runs');
    fireEvent.click(screen.getByRole('tab', { name: 'Inputs' }));
    expect(screen.getByRole('tabpanel').textContent).toContain('No editable inputs.');
    fireEvent.click(screen.getByRole('button', { name: /Details/ }));
    expect(screen.queryByRole('tabpanel')).toBeNull();
  });

  it('fails soft with missing headerView and hides empty tabs', () => {
    render(<TemplateProtocolBar track={track({ templateId: 'legacy', templateHeaderView: undefined, templateInputs: { branch: 'main' } })} />);
    expect(screen.getByText('legacy')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Details/ }));
    expect(screen.getAllByRole('tab').map(tab => tab.textContent)).toEqual(['Schedules', 'Inputs']);
    expect(screen.getByRole('tabpanel').textContent).toContain('Recent runs');
  });
});

it('renders schedules without a template and sends schedule actions', async () => {
  const onTriggerSchedule = vi.fn().mockResolvedValue(undefined);
  const onCancelSchedule = vi.fn().mockResolvedValue(undefined);
  render(<TemplateProtocolBar track={track()} schedules={{ schedules: [schedule()], recentRuns: [{
    wakeId: 'w1', scheduleId: 'daily', state: 'done', reason: 'delivered', turnId: 'turn1', createdAtMs: 1, updatedAtMs: 2,
  }] }} onTriggerSchedule={onTriggerSchedule} onCancelSchedule={onCancelSchedule} />);
  fireEvent.click(screen.getByRole('button', { name: /Details/ }));
  expect(screen.getByRole('tabpanel').textContent).toContain('delivered');
  fireEvent.click(screen.getByRole('button', { name: 'Run now' }));
  await vi.waitFor(() => expect(onTriggerSchedule).toHaveBeenCalledWith('daily'));
  await vi.waitFor(() => expect(screen.getByRole('button', { name: 'Cancel' }).hasAttribute('disabled')).toBe(false));
  fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
  await vi.waitFor(() => expect(onCancelSchedule).toHaveBeenCalledWith('daily'));
});

function schedule() {
  return { id: 'daily', trackId: 't', title: 'Daily', prompt: 'Check', sourceKind: 'user', scheduleKind: 'cron' as const,
    cronExpr: '0 9 * * *', timezone: 'UTC', runAtMs: null, nextFireAtMs: 2000, lastFiredAtMs: null,
    maxOccurrences: null, firedCount: 0, state: 'active' as const, createdAtMs: 1, updatedAtMs: 2 };
}

it('submits typed input values and displays failures', async () => {
  const onSaveInputs = vi.fn().mockRejectedValue(new Error('Invalid input'));
  render(<TemplateProtocolBar track={track({ templateId: 'dev', templateInputs: { name: 'Perga', count: 2, enabled: true, mode: 'a' } })}
    inputsSchema={{ name: { type: 'string' }, count: { type: 'number' }, enabled: { type: 'boolean' }, mode: { type: 'enum', values: ['a', 'b'] } }} onSaveInputs={onSaveInputs} />);
  fireEvent.click(screen.getByRole('button', { name: /Details/ }));
  fireEvent.click(screen.getByRole('tab', { name: 'Inputs' }));
  fireEvent.change(screen.getByLabelText('count'), { target: { value: '3.5' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save inputs' }));
  await vi.waitFor(() => expect(onSaveInputs).toHaveBeenCalledWith({ name: 'Perga', count: 3.5, enabled: true, mode: 'a' }));
  await vi.waitFor(() => expect(screen.getByRole('status').textContent).toBe('Invalid input'));
});

it('adds a once schedule and re-arms terminal schedules through the same form', async () => {
  const onSaveSchedule = vi.fn().mockResolvedValue(undefined);
  render(<TemplateProtocolBar track={track({ templateId: 'dev' })} schedules={{ schedules: [{ ...schedule(), state: 'canceled' }], recentRuns: [] }} onSaveSchedule={onSaveSchedule} />);
  fireEvent.click(screen.getByRole('button', { name: /Details/ }));
  fireEvent.click(screen.getByRole('button', { name: 'Re-arm' }));
  fireEvent.click(screen.getByRole('button', { name: 'Save schedule' }));
  await vi.waitFor(() => expect(onSaveSchedule).toHaveBeenCalledWith({ id: 'daily', sourceKind: 'user', title: 'Daily', prompt: 'Check', cronExpr: '0 9 * * *', timezone: 'UTC' }));
  await vi.waitFor(() => expect(screen.getByLabelText('Title').getAttribute('disabled')).toBeNull());
  fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Once' } });
  fireEvent.change(screen.getByLabelText('Prompt'), { target: { value: 'Do it' } });
  fireEvent.change(screen.getByLabelText('Schedule type'), { target: { value: 'once' } });
  fireEvent.change(screen.getByLabelText('Run at'), { target: { value: '2030-01-01T12:00' } });
  fireEvent.click(screen.getByRole('button', { name: 'Add schedule' }));
  await vi.waitFor(() => expect(onSaveSchedule).toHaveBeenLastCalledWith({ title: 'Once', prompt: 'Do it', runAtMs: Date.parse('2030-01-01T12:00') }));
});
