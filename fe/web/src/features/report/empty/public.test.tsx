// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { ReportEmpty } from './public.tsx';

afterEach(cleanup);

describe('ReportEmpty', () => {
  it('renders airy prose guidance and an honest static Flow sample with no fake protocol bar', async () => {
    render(<ReportEmpty />);

    expect(screen.getByRole('heading', { name: '进展会整理在这里' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Flow 示例：多步工作如何自动流转' })).toBeTruthy();
    expect(screen.getByRole('region', { name: '流程示例' })).toBeTruthy();
    expect(screen.queryByRole('region', { name: 'Template protocol' })).toBeNull();
    expect(document.querySelector('[data-nc-report]')).toBeNull();
    expect(screen.queryByRole('table')).toBeNull();

    // Default-selected running sample node and clickable inspection of sibling sample nodes.
    expect(screen.getByText(/按前置依赖自动派发子会话执行调研/)).toBeTruthy();

    await userEvent.click(screen.getByRole('button', { name: /^明确目标与范围/ }));
    expect(screen.getByText(/若任务适合固定结构，会在动工前自动套用合适模板/)).toBeTruthy();

    await userEvent.click(screen.getByRole('button', { name: /^汇总沉淀进 Report/ }));
    expect(screen.getByText(/上游步骤完成后自动唤醒 Lead/)).toBeTruthy();
  });
});
