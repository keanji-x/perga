// The two CodeMirror panes: one file, and one file's two sides. Both are read-only.

import { defaultKeymap } from '@codemirror/commands';
import { bracketMatching, foldGutter, foldKeymap } from '@codemirror/language';
import { MergeView } from '@codemirror/merge';
import { SearchQuery, closeSearchPanel, findNext, findPrevious, getSearchQuery, highlightSelectionMatches, openSearchPanel, search, searchKeymap, searchPanelOpen, setSearchQuery } from '@codemirror/search';
import { Compartment, EditorState, Prec } from '@codemirror/state';
import { EditorView, crosshairCursor, drawSelection, highlightActiveLine, highlightActiveLineGutter, highlightSpecialChars, keymap, lineNumbers, rectangularSelection } from '@codemirror/view';
import { githubDark, githubLight } from '@uiw/codemirror-theme-github';
import { useEffect, useMemo, useRef } from 'react';
import { useCodeLanguage, type CodeTheme } from '../../ui/code/public.tsx';
import { useState } from '../../ui/state/public.ts';

export type PaneTheme = CodeTheme;

/** What the shared search bar drives; the seam that keeps the bar from knowing about CodeMirror. */
export interface PaneSearchAdapter {
  setQuery(pattern: string): void;
  next(): void;
  prev(): void;
  dispose(): void;
}

export interface CodePaneProps {
  path: string;
  text: string;
  theme: PaneTheme;
  onSearchAdapterReady?: (adapter: PaneSearchAdapter | null) => void;
  onSearchCount?: (current: number, total: number) => void;
  onSlashOpen?: () => void;
}

export interface DiffPaneProps {
  path: string;
  headText: string | null;
  workingText: string | null;
  theme: PaneTheme;
}

/* An empty panel handed to `search()` so CodeMirror's own search UI never renders; the bar is React's. */
function emptyPanel() {
  const dom = document.createElement('div');
  dom.className = 'fv-code-search-panel-empty';
  dom.setAttribute('aria-hidden', 'true');
  return {
    dom,
    // Our search row precedes the editor. Its hidden placeholder must not
    // reserve a phantom bottom margin when the editor reveals a match.
    top: true,
    mount() { dom.parentElement?.classList.add('fv-code-search-panels-empty'); },
    destroy() { dom.parentElement?.classList.remove('fv-code-search-panels-empty'); },
  };
}

/** 1-based; `current` of `0` means no current match. */
function computeMatchState(view: EditorView, query: SearchQuery, knownTotal?: number): { current: number; total: number } {
  if (!query.valid) return { current: 0, total: 0 };
  const selectionFrom = view.state.selection.main.from;
  const selectionTo = view.state.selection.main.to;
  // Once the total is known, only the prefix can affect this selection's ordinal.
  // Keep using the editor's matching policy; retain no per-match position array.
  const cursor = query.getCursor(view.state.doc, 0, knownTotal === undefined ? undefined : selectionTo);
  let total = 0;
  let current = 0;
  let step = cursor.next();
  while (!step.done) {
    total += 1;
    if (step.value.from === selectionFrom && step.value.to === selectionTo) current = total;
    step = cursor.next();
  }
  return { current, total: knownTotal ?? total };
}

function buildCodeSearchAdapter(
  view: EditorView,
  onCount: (current: number, total: number) => void,
): PaneSearchAdapter {
  let counted: { document: EditorState['doc']; query: SearchQuery; total: number } | null = null;
  const emit = () => {
    const query = getSearchQuery(view.state);
    const knownTotal = counted?.document === view.state.doc && counted.query === query ? counted.total : undefined;
    const { current, total } = computeMatchState(view, query, knownTotal);
    counted = { document: view.state.doc, query, total };
    onCount(current, total);
  };
  return {
    setQuery(pattern) {
      const query = new SearchQuery({ search: pattern, caseSensitive: false });
      if (pattern !== '' && !searchPanelOpen(view.state)) openSearchPanel(view);
      view.dispatch({ effects: setSearchQuery.of(query) });
      if (pattern === '') {
        counted = null;
        closeSearchPanel(view);
        onCount(0, 0);
        return;
      }
      findNext(view);
      emit();
    },
    next() { findNext(view); emit(); },
    prev() { findPrevious(view); emit(); },
    dispose() {
      counted = null;
      view.dispatch({ effects: setSearchQuery.of(new SearchQuery({ search: '' })) });
      closeSearchPanel(view);
    },
  };
}

export function CodePane({
  path, text, theme, onSearchAdapterReady, onSearchCount, onSlashOpen,
}: CodePaneProps) {
  const { support } = useCodeLanguage({ kind: 'filename', value: path }, text);
  const parentRef = useRef<HTMLDivElement | null>(null);
  const viewRef = useRef<EditorView | null>(null);
  const [configuration] = useState(() => new Compartment());
  /* Callbacks live in refs so a caller re-creating them per render cannot tear the editor down. */
  const onSearchAdapterReadyRef = useRef(onSearchAdapterReady);
  const onSearchCountRef = useRef(onSearchCount);
  const onSlashOpenRef = useRef(onSlashOpen);
  useEffect(() => { onSearchAdapterReadyRef.current = onSearchAdapterReady; }, [onSearchAdapterReady]);
  useEffect(() => { onSearchCountRef.current = onSearchCount; }, [onSearchCount]);
  useEffect(() => { onSlashOpenRef.current = onSlashOpen; }, [onSlashOpen]);

  const extensions = useMemo(
    () => [
      EditorView.lineWrapping,
      theme === 'dark' ? githubDark : githubLight,
      EditorState.readOnly.of(true),
      lineNumbers(), foldGutter(), highlightSpecialChars(), drawSelection(),
      highlightActiveLine(), highlightActiveLineGutter(), bracketMatching(),
      rectangularSelection(), crosshairCursor(), highlightSelectionMatches(),
      EditorState.allowMultipleSelections.of(true),
      keymap.of([...defaultKeymap, ...searchKeymap, ...foldKeymap]),
      EditorView.editorAttributes.of({ style: 'height: 100%' }),
      EditorView.contentAttributes.of({ tabindex: '0', 'aria-readonly': 'true', 'aria-label': 'File code' }),
      ...(support === null ? [] : [support]),
      search({ createPanel: emptyPanel }),
      /* `Prec.highest` so `/` reaches the bar before any language/default binding — and before Firefox's quick-find. */
      Prec.highest(keymap.of([{
        key: '/',
        run: () => { onSlashOpenRef.current?.(); return true; },
      }])),
    ],
    [support, theme],
  );

  useEffect(() => {
    if (parentRef.current === null) return;
    const view = new EditorView({
      parent: parentRef.current,
      state: EditorState.create({ extensions: configuration.of([]) }),
    });
    view.scrollDOM.style.overflow = 'auto';
    viewRef.current = view;
    return () => { viewRef.current = null; view.destroy(); };
  }, [configuration]);

  useEffect(() => {
    viewRef.current?.dispatch({ effects: configuration.reconfigure(extensions) });
  }, [configuration, extensions]);

  useEffect(() => {
    const view = viewRef.current;
    if (view === null || view.state.doc.toString() === text) return;
    view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: text } });
  }, [text]);

  useEffect(() => {
    const view = viewRef.current;
    if (view === null) return;
    const adapter = buildCodeSearchAdapter(view, (current, total) => {
      onSearchCountRef.current?.(current, total);
    });
    onSearchAdapterReadyRef.current?.(adapter);
    return () => {
      // The view may already have been destroyed by the owning effect.
      if (viewRef.current === view) adapter.dispose();
      onSearchAdapterReadyRef.current?.(null);
    };
  }, [path, text]);

  return <div ref={parentRef} style={{ flex: 1, minHeight: 0 }} />;
}

/** HEAD on the left, the working tree on the right. `null` on either side is a real state (not in HEAD / deleted), exposed as `data-nc-fs-empty-*`. */
export function DiffPane({ path, headText, workingText, theme }: DiffPaneProps) {
  const ref = useRef<HTMLDivElement | null>(null);
  const mergeRef = useRef<MergeView | null>(null);
  const [configuration] = useState(() => new Compartment());
  const { support } = useCodeLanguage({ kind: 'filename', value: path }, (headText ?? '') + (workingText ?? ''));
  const extensions = useMemo(() => [
    EditorView.lineWrapping,
    theme === 'dark' ? githubDark : githubLight,
    ...(support === null ? [] : [support]),
  ], [support, theme]);

  useEffect(() => {
    const parent = ref.current;
    if (parent === null) return;
    const initial = [EditorState.readOnly.of(true),
      EditorView.contentAttributes.of({ tabindex: '0', 'aria-readonly': 'true' }), configuration.of([])];
    const merge = new MergeView({
      parent,
      a: { doc: headText ?? '', extensions: [initial, EditorView.contentAttributes.of({ 'aria-label': 'HEAD code' })] },
      b: { doc: workingText ?? '', extensions: [initial, EditorView.contentAttributes.of({ 'aria-label': 'Working tree code' })] },
      collapseUnchanged: { margin: 3, minSize: 4 },
    });
    mergeRef.current = merge;
    return () => { mergeRef.current = null; merge.destroy(); };
  }, [configuration, path, headText, workingText]);

  useEffect(() => {
    const merge = mergeRef.current;
    if (merge === null) return;
    merge.a.dispatch({ effects: configuration.reconfigure(extensions) });
    merge.b.dispatch({ effects: configuration.reconfigure(extensions) });
  }, [configuration, extensions, path, headText, workingText]);

  return (
    <div
      ref={ref}
      className="fv-merge"
      data-nc-fs-merge=""
      data-nc-fs-empty-left={headText === null ? 'true' : undefined}
      data-nc-fs-empty-right={workingText === null ? 'true' : undefined}
    />
  );
}
