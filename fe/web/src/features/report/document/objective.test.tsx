import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { readReportDocument } from '../../../../../core/domain/report.ts';
import { ReportDocument } from './public.tsx';

afterEach(cleanup);

it('keeps a historical objective anchored beside readable prose', () => {
  const report = readReportDocument({
    docRev: 1,
    summary: '',
    leadingComment: '',
    blocks: [
      { id: 'objective-1', rev: 1, kind: 'objective', markdown: '', payload: { title: 'Make it fast' } },
      { id: 'prose', rev: 1, kind: 'prose', markdown: 'Still readable.', payload: null },
    ],
  });
  render(<ReportDocument report={report} empty={null} />);
  const fallback = screen.getByRole('note');
  expect(fallback.textContent).toBe('unsupported block kind objective');
  expect(fallback.closest('#objective-1')).toBeTruthy();
  expect(screen.getByText('Still readable.')).toBeTruthy();
});
