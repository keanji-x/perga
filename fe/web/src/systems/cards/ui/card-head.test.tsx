// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';

import { CardHead } from './card-head.tsx';

afterEach(cleanup);

describe('CardHead (web CardHead DOM contract)', () => {
  it('renders the letter avatar, title, and status', () => {
    render(
      <CardHead title="terminal" status={<span>live</span>} />,
    );
    expect(screen.getByText('terminal')).toBeTruthy();
    expect(screen.getByText('T')).toBeTruthy();
    expect(screen.getByText('live')).toBeTruthy();
  });

  it('marks a standalone card head', () => {
    render(<CardHead  title="X" />);
    expect(screen.getAllByText('X')[0]?.closest('[data-nc-card-head]')).toBeTruthy();
  });
});
