// @vitest-environment jsdom
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { TrackTemplate } from '../../../../../core/domain/track.js';
import { ReportEmpty } from './public.tsx';

afterEach(cleanup);

const TEMPLATES: readonly TrackTemplate[] = Object.freeze([
  Object.freeze({
    id: 'agents-delivery',
    title: 'AGENTS.md 阶段交付协议',
    description: '按需求、实现、评审与验证推进的交付流程。',
    availability: Object.freeze({ status: 'ready' as const }),
    tasks: Object.freeze([]),
  }),
  Object.freeze({
    id: 'equity-research',
    title: '个股长期投研',
    description: '持续跟踪基本面、催化事件与核心判断。',
    availability: Object.freeze({ status: 'ready' as const }),
    tasks: Object.freeze([]),
  }),
]);

describe('ReportEmpty', () => {
  it('renders pure EmptyState with no interactive actions when no template callback or catalog is provided', () => {
    const { container } = render(<ReportEmpty />);
    expect(screen.getByRole('heading', { name: '进展会整理在这里' })).toBeTruthy();
    expect(screen.getByText('说说想做的事，计划与结论会随对话整理。')).toBeTruthy();
    expect(container.querySelector('button, a, input, textarea')).toBeNull();
  });

  it('reuses TemplateProtocolBar, ReportDocument, and ReportFlowBlock and applies a template in one click', async () => {
    const onApplyTemplate = vi.fn(() => Promise.resolve());
    render(<ReportEmpty templates={TEMPLATES} onApplyTemplate={onApplyTemplate} />);

    expect(screen.getByRole('region', { name: 'Template protocol' })).toBeTruthy();
    expect(screen.getByRole('region', { name: '起步流程' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: '进展会整理在这里' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: '从模板起稿' })).toBeTruthy();

    await userEvent.click(screen.getByRole('button', { name: /^切换模板或保持空白/ }));
    expect(screen.getByText(/套用最匹配的模板/)).toBeTruthy();

    await userEvent.click(screen.getByRole('button', { name: /AGENTS\.md 阶段交付协议/ }));
    await waitFor(() => expect(onApplyTemplate).toHaveBeenCalledWith('agents-delivery'));
  });

  it('reflects live running Lead state in the protocol bar and flow node and opens Lead on click', async () => {
    const onOpenLead = vi.fn();
    render(
      <ReportEmpty
        templates={TEMPLATES}
        onApplyTemplate={() => Promise.resolve()}
        lead={{ id: 'ses_lead_1', phase: 'running', modelLabel: 'claude-sonnet', startedAtMs: 1_700_000_000_000 }}
        onOpenLead={onOpenLead}
      />,
    );

    expect(screen.getByText('Lead 运行中')).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'claude-sonnet' }));
    expect(onOpenLead).toHaveBeenCalledWith('ses_lead_1');
  });

  it('shows an inline alert when applying a template fails', async () => {
    const onApplyTemplate = vi.fn(() => Promise.reject(new Error('template rejected')));
    render(<ReportEmpty templates={TEMPLATES} onApplyTemplate={onApplyTemplate} />);

    await userEvent.click(screen.getByRole('button', { name: /个股长期投研/ }));

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toBe('套用模板失败，请稍后重试。');
  });
});
