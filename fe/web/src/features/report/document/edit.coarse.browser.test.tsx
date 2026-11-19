import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';

import '../../../styles/entry.css';
import { ReportDocument } from './public.tsx';

afterEach(cleanup);

it('keeps the prose edit control discoverable on touch screens without moving text', () => {
  expect(window.matchMedia('(hover: none)').matches).toBe(true);
  render(<ReportDocument report={{ summary: '', body: '', docRev: 1,
    blocks: [{ id: 'editable', kind: 'prose', payload: { markdown: 'Original prose' } }] }}
    empty={<p>Empty</p>} onSaveProseBlock={vi.fn().mockResolvedValue(undefined)} />);
  const edit = screen.getByRole('button', { name: 'Edit block editable' });
  expect(getComputedStyle(edit).opacity).toBe('1');
  expect(getComputedStyle(edit).position).toBe('absolute');
});
