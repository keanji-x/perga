import { useMemo } from 'react';

import canonicalMarkSvg from './perga-mark.svg?raw';
import styles from './mark.module.css';

export type BrandMarkVariant = 'default';

interface ParsedBrandMark {
  readonly viewBox: string;
  readonly paths: readonly string[];
}

function parseBrandMarkSvg(raw: string): ParsedBrandMark {
  const viewBox = /\bviewBox="([^"]+)"/.exec(raw)?.[1];
  const paths = [...raw.matchAll(/<path\b[^>]*\bd="([^"]+)"/g)].map((match) => match[1]);
  if (viewBox === undefined || paths.length === 0) {
    throw new Error('Perga brand mark SVG must define a viewBox and at least one path.');
  }
  return { viewBox, paths };
}

/**
 * Single source lookup for canonical brand mark SVGs.
 * Add future optical variants (for example a simplified compact mark) here without duplicating path literals.
 */
function brandMarkSource(variant: BrandMarkVariant): string {
  switch (variant) {
    case 'default':
      return canonicalMarkSvg;
  }
}

/** Static theme-owned brand mark rendered in currentColor. */
export function BrandMark({ variant = 'default', className = '' }: Readonly<{ variant?: BrandMarkVariant; className?: string }>) {
  const { viewBox, paths } = useMemo(() => parseBrandMarkSvg(brandMarkSource(variant)), [variant]);
  return (
    <svg
      className={`${styles.mark} ${className}`}
      viewBox={viewBox}
      fill="currentColor"
      aria-hidden="true"
      focusable="false"
      data-nc-brand-mark={variant}
    >
      {paths.map((d, index) => <path key={index} d={d} />)}
    </svg>
  );
}
