// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react';
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
  it('renders pure EmptyState with no interactive actions when no ready templates are available', () => {
    const { container } = render(<ReportEmpty />);
    expect(screen.getByRole('heading', { name: '进展会整理在这里' })).toBeTruthy();
    expect(screen.getByText('说说想做的事，计划与结论会随对话整理。')).toBeTruthy();
    expect(container.querySelector('button, a, input, textarea')).toBeNull();
  });

  it('reuses TemplateProtocolBar, ReportDocument, ReportFlowBlock, and EditorialChronicle without a template picker section', async () => {
    render(<ReportEmpty templates={TEMPLATES} />);

    expect(screen.getByRole('region', { name: 'Template protocol' })).toBeTruthy();
    expect(screen.getByRole('region', { name: '起步流程' })).toBeTruthy();
    expect(screen.getByRole('region', { name: '工作方式' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: '进展会整理在这里' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: '工作方式' })).toBeTruthy();
    expect(screen.queryByRole('heading', { name: '从模板起稿' })).toBeNull();

    await userEvent.click(screen.getByRole('button', { name: /^按需匹配模板/ }));
    expect(screen.getByText(/在动工前自动套用合适模板/)).toBeTruthy();

    await userEvent.click(screen.getByRole('button', { name: /与 Lead 及子会话协作/ }));
    expect(screen.getByText(/用于提出目标、澄清需求/)).toBeTruthy();
  });

  it('reflects live running Lead state in the protocol bar and flow node and opens Lead on click', async () => {
    const onOpenLead = vi.fn();
    render(
      <ReportEmpty
        templates={TEMPLATES}
        lead={{ id: 'ses_lead_1', phase: 'running', modelLabel: 'claude-sonnet', startedAtMs: 1_700_000_000_000 }}
        onOpenLead={onOpenLead}
      />,
    );

    expect(screen.getByText('Lead 运行中')).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'claude-sonnet' }));
    expect(onOpenLead).toHaveBeenCalledWith('ses_lead_1');
  });
});
