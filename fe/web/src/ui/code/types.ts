import type { CodeSource } from './language.ts';

export type CodeTheme = 'light' | 'dark';
export type ReadOnlyCodeProps = Readonly<{
  text: string;
  source: CodeSource;
  theme?: CodeTheme;
  highlightLines?: Readonly<{ start: number; end: number }>;
  highlightRequest?: number;
  wrapLines?: boolean;
  savedScroll?: Readonly<{ top: number; left: number }>;
  onScrollOffset?: (offset: Readonly<{ top: number; left: number }>) => void;
}>;
