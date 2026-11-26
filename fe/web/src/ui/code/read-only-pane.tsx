import { useEffect, useMemo, useRef } from 'react';
import { Compartment, EditorState, Prec } from '@codemirror/state';
import { Decoration, EditorView, ViewPlugin, keymap, lineNumbers } from '@codemirror/view';
import { standardKeymap } from '@codemirror/commands';
import { githubDark, githubLight } from '@uiw/codemirror-theme-github';
import { CopyCodeButton } from './copy-button.tsx';
import { useState } from '../state/public.ts';
import { useCodeLanguage } from './use-language.ts';
import type { ReadOnlyCodeProps } from './types.ts';
import styles from './code.module.css';

const noHighlights = Object.freeze([] as const);

export function ReadOnlyPane({ text, source, theme, highlightLines, highlightRequest, wrapLines = false }: ReadOnlyCodeProps) {
  const parent = useRef<HTMLDivElement | null>(null);
  const viewRef = useRef<EditorView | null>(null);
  const [configuration] = useState(() => new Compartment());
  const [highlights] = useState(() => new Compartment());
  const startLine = highlightLines?.start;
  const endLine = highlightLines?.end;
  const { description, support, status } = useCodeLanguage(source, text);
  const label = status === 'ready' ? description?.name
    : status === 'loading' ? `Loading ${description?.name}…`
      : status === 'limited' ? 'Plain text · large file'
        : status === 'unavailable' ? 'Plain text · highlighting unavailable' : 'Plain text';
  const extensions = useMemo(() => [
    EditorState.readOnly.of(true),
    EditorView.contentAttributes.of({ 'aria-label': 'Code', 'aria-readonly': 'true', tabindex: '0' }),
    lineNumbers(), keymap.of(standardKeymap),
    theme === 'light' ? githubLight : githubDark,
    Prec.highest(EditorView.theme({
      '&': { fontSize: 'inherit', backgroundColor: 'transparent', color: 'var(--text)' },
      '.cm-scroller': { overflow: 'auto', maxHeight: 'var(--read-only-code-max-height, 24rem)', fontFamily: 'var(--font-code)' },
      '.cm-nc-highlight-line': { backgroundColor: 'var(--accent-soft)' },
      '.cm-gutters': { backgroundColor: 'transparent', color: 'var(--text-3)', border: 'none' },
      '.cm-content': { padding: 'var(--space-3) 0' },
      '&.cm-focused': { outline: '2px solid var(--accent)', outlineOffset: '-2px' },
    })),
    ...(wrapLines ? [EditorView.lineWrapping] : []),
    ...(support === null ? [] : [support]),
  ], [support, theme, wrapLines]);
  useEffect(() => {
    if (parent.current === null) return;
    const view = new EditorView({ parent: parent.current, state: EditorState.create({ extensions: [configuration.of([]), highlights.of(noHighlights)] }) });
    view.scrollDOM.dataset.ncCodeScroller = '';
    viewRef.current = view;
    return () => { viewRef.current = null; view.destroy(); };
  }, [configuration, highlights]);
  useEffect(() => {
    const view = viewRef.current;
    if (view === null) return;
    view.dispatch({ effects: configuration.reconfigure(extensions) });
  }, [configuration, extensions]);
  useEffect(() => {
    const view = viewRef.current;
    if (view === null || view.state.doc.toString() === text) return;
    const top = view.scrollDOM.scrollTop;
    const left = view.scrollDOM.scrollLeft;
    const selection = view.state.selection.main;
    const document = view.state.toText(text);
    view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: document },
      selection: { anchor: Math.min(selection.anchor, document.length), head: Math.min(selection.head, document.length) } });
    view.scrollDOM.scrollTop = top;
    view.scrollDOM.scrollLeft = left;
    // Capture against the new document so the next measurement preserves these
    // pixels rather than mapping the old viewport through a full replacement.
    view.dispatch({ effects: view.scrollSnapshot() });
  }, [text]);
  useEffect(() => {
    const view = viewRef.current;
    if (view === null) return;
    view.dispatch({ selection: { anchor: 0 } });
    view.scrollDOM.scrollTop = 0;
    view.scrollDOM.scrollLeft = 0;
    view.dispatch({ effects: view.scrollSnapshot() });
  }, [source.kind, source.value]);
  useEffect(() => {
    const view = viewRef.current;
    if (view === null) return;
    if (startLine === undefined || endLine === undefined || !Number.isSafeInteger(startLine)
      || !Number.isSafeInteger(endLine) || startLine < 1 || endLine < startLine || startLine > view.state.doc.lines) {
      if (highlights.get(view.state) !== noHighlights) view.dispatch({ effects: highlights.reconfigure(noHighlights) });
      return;
    }
    const mark = Decoration.line({ class: 'cm-nc-highlight-line', attributes: { 'data-nc-code-highlight-line': '' } });
    // The editor can virtualize a large range; decorate only the currently drawn lines.
    const decorations = (editor: EditorView) => Decoration.set(editor.viewportLineBlocks.flatMap(block => {
      const line = editor.state.doc.lineAt(block.from);
      return line.number >= startLine && line.number <= endLine ? [mark.range(line.from)] : [];
    }));
    const extension = ViewPlugin.define(editor => ({
      marks: decorations(editor),
      update(update) {
        if (update.docChanged || update.viewportChanged) this.marks = decorations(update.view);
      },
    }), { decorations: plugin => plugin.marks });
    view.dispatch({ effects: [
      highlights.reconfigure(extension),
      EditorView.scrollIntoView(view.state.doc.line(startLine).from, { y: 'center' }),
    ] });
  }, [highlights, startLine, endLine, highlightRequest, text, source.kind, source.value]);
  return <div className={styles.code} data-nc-code-status={status}>
    <div className={styles.toolbar}>
      <span>{label}</span>
      <CopyCodeButton text={text} />
    </div>
    <div ref={parent} />
  </div>;
}
