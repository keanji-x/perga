import { IconButton } from '@astryxdesign/core/IconButton';
import { TextInput } from '@astryxdesign/core/TextInput';
import { isImeKeyEvent } from '@astryxdesign/core/utils';
import { useEffect, useId, useRef, type KeyboardEvent } from 'react';

import type { DirectoryListingWire } from '../../../../../core/domain/fs.ts';
import { quickOpenDirectoryEntries, quickOpenQuery, type QuickOpenEntry } from '../../../../../core/domain/quick-open.ts';
import { formatFileLine, parseFileOpenInput, isFilePath, type ReportFileLinkTarget } from '../../../../../core/domain/report-file.ts';
import { Dialog } from '../../../ui/dialog/public.tsx';
import { Icon } from '../../../ui/icon/public.tsx';
import { useState } from '../../../ui/state/public.ts';
import styles from './quick-open.module.css';

export type QuickOpenLookup = Readonly<{
  search: (query: string, signal: AbortSignal) => Promise<readonly string[]>;
  list: (directory: string, signal: AbortSignal) => Promise<DirectoryListingWire['entries']>;
}>;

type LookupResult =
  | Readonly<{ query: string; kind: 'search'; paths: readonly string[] }>
  | Readonly<{ query: string; kind: 'directory'; entries: DirectoryListingWire['entries'] }>
  | Readonly<{ query: string; kind: 'error' }>;

function useLookup(lookup: QuickOpenLookup, kind: 'search' | 'directory', query: string) {
  const [result, setResult] = useState<LookupResult | null>(null);
  useEffect(() => {
    if (query === '') return;
    const controller = new AbortController();
    const timer = setTimeout(() => {
      const { signal } = controller;
      const request: Promise<LookupResult> = kind === 'directory'
        ? lookup.list(query, signal).then(entries => ({ query, kind, entries }))
        : lookup.search(query, signal).then(paths => ({ query, kind, paths }));
      void request.then(
        next => { if (!signal.aborted) setResult(next); },
        () => { if (!signal.aborted) setResult({ query, kind: 'error' }); },
      );
    }, 140);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [lookup, kind, query, setResult]);
  return result?.query === query && (result.kind === kind || result.kind === 'error') ? result : null;
}

/** Each mount is one Dialog lifetime, including Escape handling and focus restoration. */
export function QuickOpen({ initialQuery, recent, lookup, onOpen, onClose }: Readonly<{
  initialQuery: string;
  recent: readonly string[];
  lookup: QuickOpenLookup;
  onOpen: (target: ReportFileLinkTarget) => void;
  onClose: () => void;
}>) {
  const [input, setInput] = useState(initialQuery);
  const [active, setActive] = useState<number | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const optionRef = useRef<HTMLButtonElement>(null);
  const listId = useId();
  const mode = quickOpenQuery(input);
  const query = mode.kind === 'directory' ? mode.directory : mode.query;
  const result = useLookup(lookup, mode.kind, query);
  const entries: readonly QuickOpenEntry[] = query === '' ? recent.map(path => ({ path }))
    : result?.kind === 'search' ? result.paths.map(path => ({ path }))
      : result?.kind === 'directory' && mode.kind === 'directory'
        ? quickOpenDirectoryEntries(mode.directory, mode.prefix, result.entries) : [];
  const selected = active ?? (mode.kind === 'search' ? 0 : null);
  const current = selected === null ? undefined : entries[selected];
  const selectedId = current === undefined ? undefined : `${listId}-${selected}`;
  useEffect(() => { optionRef.current?.scrollIntoView?.({ block: 'nearest' }); }, [selectedId]);
  const change = (value: string) => { setInput(value); setActive(null); };
  const choose = (entry: QuickOpenEntry) => {
    const { path } = entry;
    if (!isFilePath(path)) return;
    if (entry.isDirectory) {
      const line = formatFileLine(mode.target);
      change(`${path}/${line === undefined ? '' : ':' + line}`);
    } else {
      onClose();
      onOpen({ ...mode.target, path });
    }
  };
  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (isImeKeyEvent(event.nativeEvent)) return;
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      const step = event.key === 'ArrowDown' ? 1 : -1;
      if (entries.length > 0) setActive(Math.max(0, Math.min(entries.length - 1, (selected ?? (step === 1 ? -1 : entries.length)) + step)));
    } else if (event.key === 'Enter') {
      event.preventDefault();
      if (current !== undefined) choose(current);
      else if (mode.kind === 'directory' && mode.prefix !== '' && mode.prefix !== '.' && mode.prefix !== '..') {
        const target = parseFileOpenInput(input);
        if (target !== null) choose(entries.find(entry => entry.path === target.path) ?? target);
      }
    }
  };
  const status = result?.kind === 'error' ? (mode.kind === 'directory' ? 'Could not list this folder.' : 'Could not search files.')
    : query !== '' && result === null ? 'Loading…'
      : entries.length === 0 ? (query === '' ? 'No recent files' : 'No matching files') : null;
  return <Dialog open purpose="info" origin={{ kind: 'surface-center' }} title="Open a file" hideTitleRow onClose={onClose} initialFocusRef={inputRef}>
    <div className={styles.quickOpen}>
      <div className={styles.inputRow}>
        <TextInput ref={inputRef} label="File path or name" isLabelHidden
          placeholder="File path or name" width="100%" value={input} onChange={change} onKeyDown={onKeyDown}
          role="combobox" aria-controls={listId} aria-expanded aria-autocomplete="list" aria-activedescendant={selectedId} />
        <IconButton label="Close" type="button" variant="ghost" size="sm" icon={<Icon name="close" size="sm" />} onClick={onClose} />
      </div>
      {query === '' && entries.length > 0 && <p className={styles.status}>Recent files</p>}
      <ul id={listId} role="listbox" aria-label={query === '' ? 'Recent files' : 'Matching files'} className={styles.list}>
        {entries.map((entry, index) => <li key={entry.path} role="none">
          <button id={`${listId}-${index}`} ref={index === selected ? optionRef : null} type="button" role="option"
            aria-selected={index === selected} className={styles.option} tabIndex={-1}
            onMouseDown={event => event.preventDefault()} onClick={() => choose(entry)}>
            <Icon name={entry.isDirectory ? 'folder' : 'file'} size="sm" />
            <span title={entry.path}>{entry.path}</span>
          </button>
        </li>)}
      </ul>
      {status !== null && <p className={styles.status} role="status">{status}</p>}
    </div>
  </Dialog>;
}
