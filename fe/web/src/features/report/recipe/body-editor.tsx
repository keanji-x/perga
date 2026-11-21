// The recipe body's text editor: CodeMirror, Markdown, soft wrap, nothing else.
// Its own module so jsdom-tier tests can substitute it: CodeMirror measures a layout jsdom does not have.

import { EditorView } from '@codemirror/view';
import { languages } from '@codemirror/language-data';
import { LanguageDescription, type LanguageSupport } from '@codemirror/language';
import { githubDark, githubLight } from '@uiw/codemirror-theme-github';
import CodeMirror from '@uiw/react-codemirror';
import { useEffect, useMemo } from 'react';
import { useState } from '../../../ui/state/public.ts';

/** Light or dark, resolved by `app/theme` and injected: `features/**` may not import `app/**`. */
export type RecipeEditorTheme = 'light' | 'dark';

export function RecipeBodyEditor({ id, value, theme, label, readOnly, onChange }: Readonly<{
  id: string;
  value: string;
  theme: RecipeEditorTheme;
  /** The editable's accessible name, carried as an attribute: CodeMirror's `contenteditable` has no control for a `<label for>`. */
  label: string;
  /** Shown but not editable: the text is held by a write whose outcome is unknown. */
  readOnly: boolean;
  onChange: (next: string) => void;
}>) {
  const [markdown, setMarkdown] = useState<LanguageSupport | null>(null);
  useEffect(() => {
    let active = true;
    const language = LanguageDescription.matchLanguageName(languages, 'markdown');
    void language?.load().then(
      support => { if (active) setMarkdown(support); },
      () => { /* Highlighting is optional; retain the usable plain editor. */ },
    );
    return () => { active = false; };
  }, []);
  const extensions = useMemo(() => {
    /* `contentAttributes`, not an `aria-label` prop: `@uiw/react-codemirror` spreads unknown props onto the wrapper div, leaving the editable unnamed. */
    const named = EditorView.contentAttributes.of({ 'aria-label': label });
    return markdown === null
      ? [EditorView.lineWrapping, named]
      : [EditorView.lineWrapping, named, markdown];
  }, [label, markdown]);

  return (
    <CodeMirror
      id={id}
      value={value}
      theme={theme === 'dark' ? githubDark : githubLight}
      extensions={extensions}
      basicSetup={{ lineNumbers: true, foldGutter: false }}
      readOnly={readOnly}
      onChange={onChange}
    />
  );
}
