import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';

import { toTrack, type Track } from '../../../../../core/domain/track.js';
import { trackDtoSchema } from '../../../../../core/api/perga-v1.ts';
import { TemplateProtocolBar } from './public.tsx';

afterEach(cleanup);

function track(template: Partial<Track> = {}): Track {
  return { ...toTrack(trackDtoSchema.parse({ id: 't', areaId: 'a', title: 'Report', sortOrder: 0,
    workspaceDriver: 'managed', workspaceSpec: {}, createdAtMs: 1, updatedAtMs: 1 })), ...template };
}

describe('TemplateProtocolBar', () => {
  it('renders nothing without a template', () => {
    expect(render(<TemplateProtocolBar track={track()} />).container.innerHTML).toBe('');
  });

  it('shows a quiet template identity without rules toggle or inputs form', () => {
    render(<TemplateProtocolBar track={track({ templateId: 'dev', templateTitle: 'Dev' })} />);
    expect(screen.getByText('Dev')).toBeTruthy();
    expect(screen.queryByRole('button')).toBeNull();
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

  it('toggles read-only rules drawer when rules exist', () => {
    render(<TemplateProtocolBar track={track({ templateId: 'dev',
      templateHeaderView: { rules: [{ label: 'Review', text: 'Check tests' }] },
    })} />);
    fireEvent.click(screen.getByRole('button', { name: /Rules/ }));
    expect(screen.getByText('Check tests')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Rules/ }));
    expect(screen.queryByText('Check tests')).toBeNull();
  });

  it('fails soft with missing headerView', () => {
    render(<TemplateProtocolBar track={track({ templateId: 'legacy', templateHeaderView: undefined })} />);
    expect(screen.getByText('legacy')).toBeTruthy();
    expect(screen.queryByRole('button')).toBeNull();
  });
});
