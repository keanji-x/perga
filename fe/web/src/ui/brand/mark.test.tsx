// @vitest-environment jsdom
import { cleanup, render } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';

import canonicalMarkSvg from './perga-mark.svg?raw';
import { BrandMark } from './mark.tsx';

afterEach(cleanup);

describe('BrandMark', () => {
  it('renders the exact viewBox and paths from perga-mark.svg without duplicated path literals', () => {
    const expectedViewBox = /\bviewBox="([^"]+)"/.exec(canonicalMarkSvg)?.[1];
    const expectedPaths = [...canonicalMarkSvg.matchAll(/<path\b[^>]*\bd="([^"]+)"/g)].map((match) => match[1]);

    const { container } = render(<BrandMark />);
    const svg = container.querySelector('svg[data-nc-brand-mark="default"]');
    expect(svg).not.toBeNull();
    expect(svg?.getAttribute('viewBox')).toBe(expectedViewBox);
    expect([...svg!.querySelectorAll('path')].map((node) => node.getAttribute('d'))).toEqual(expectedPaths);
  });
});
