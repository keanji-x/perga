import {
  CommandPaletteEmpty, CommandPaletteFooter, CommandPaletteGroup, CommandPaletteInput, CommandPaletteItem, CommandPaletteList,
} from '@astryxdesign/core/CommandPalette';
import { IconButton } from '@astryxdesign/core/IconButton';
import { Kbd } from '@astryxdesign/core/Kbd';
import { Spinner } from '@astryxdesign/core/Spinner';
import { isImeKeyEvent } from '@astryxdesign/core/utils';
import { useId, useRef, type KeyboardEvent } from 'react';

import type { QuickOpenEntry, QuickOpenQuery } from '../../../../../core/domain/quick-open.ts';
import { parseFileOpenInput, isFilePath, type ReportFileLinkTarget } from '../../../../../core/domain/report-file.ts';
import { Dialog } from '../../../ui/dialog/public.tsx';
import { Icon } from '../../../ui/icon/public.tsx';
import { useState } from '../../../ui/state/public.ts';
import styles from './quick-open.module.css';

function MatchedText({ text, positions, offset = 0 }: Readonly<{
  text: string; positions: ReadonlySet<number>; offset?: number;
}>) {
  if (positions.size === 0) return text;
  const runs: { start: number; text: string; matched: boolean }[] = [];
  let start = offset;
  for (const character of text) {
    const matched = positions.has(start) || (character.length === 2 && positions.has(start + 1));
    const last = runs.at(-1);
    if (last?.matched === matched) last.text += character;
    else runs.push({ start, text: character, matched });
    start += character.length;
  }
  return runs.map(run => run.matched ? <mark key={run.start}>{run.text}</mark> : run.text);
}

/** Each mount is one Dialog lifetime, including Escape handling and focus restoration. */
export function QuickOpen({ input, onInputChange, mode, entries, busy, staleDirectory, searchPending,
  emptyMessage, onOpen, onClose }: Readonly<{
  input: string;
  onInputChange: (value: string) => void;
  mode: QuickOpenQuery;
  entries: readonly QuickOpenEntry[];
  busy: boolean;
  staleDirectory: boolean;
  searchPending: boolean;
  emptyMessage: string;
  onOpen: (target: ReportFileLinkTarget) => void;
  onClose: () => void;
}>) {
  const [activePath, setActivePath] = useState<string | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const activeRef = useRef<HTMLDivElement>(null);
  const pointer = useRef<Readonly<{ x: number; y: number }> | null>(null);
  const listId = useId();
  const explicitIndex = entries.findIndex(entry => entry.path === activePath);
  const directPath = mode.kind === 'directory' && !['', '.', '..'].includes(mode.prefix);
  const defaultIndex = directPath && entries[0]?.path !== mode.target.path ? -1 : 0;
  const selected = searchPending ? -1 : Math.max(defaultIndex, explicitIndex);
  const current = entries[selected];
  const completion = current ?? entries[0];
  const canChoose = !searchPending && (!staleDirectory || explicitIndex >= 0);
  const optionId = (index: number) => `${listId}-${index}`;
  const change = (value: string) => { setActivePath(null); onInputChange(value); };
  const complete = (entry: { path: string; isDirectory: boolean }) => {
    const suffix = input.trimStart().slice(mode.target.path.length);
    change(entry.path + (entry.isDirectory && !entry.path.endsWith('/') ? '/' : '') + suffix);
  };
  const choose = (entry: Pick<QuickOpenEntry, 'path'> & { isDirectory?: boolean }) => {
    if (!isFilePath(entry.path)) return;
    if (entry.isDirectory) {
      complete({ path: entry.path, isDirectory: true });
    } else {
      onClose();
      onOpen({ ...mode.target, path: entry.path });
    }
  };
  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (isImeKeyEvent(event.nativeEvent)) return;
    const key = event.key.toLowerCase();
    const ctrl = event.ctrlKey && !event.metaKey && !event.altKey && !event.shiftKey;
    const step = key === 'arrowdown' || (ctrl && key === 'n') ? 1 : key === 'arrowup' || (ctrl && key === 'p') ? -1 : 0;
    if (step !== 0) {
      event.preventDefault();
      if (entries.length > 0 && !searchPending) {
        const index = selected < 0 ? (step > 0 ? 0 : entries.length - 1)
          : (selected + step + entries.length) % entries.length;
        setActivePath(entries[index].path);
      }
    } else if (key === 'pageup' || key === 'pagedown') {
      event.preventDefault();
      if (current === undefined) return;
      const page = Math.max(1, Math.floor((listRef.current?.clientHeight ?? 0) / (activeRef.current?.offsetHeight || 1)));
      const index = selected + (key === 'pageup' ? -page : page);
      setActivePath(entries[Math.max(0, Math.min(entries.length - 1, index))].path);
    } else if (key === 'tab' && !event.shiftKey && completion !== undefined && canChoose) {
      event.preventDefault();
      complete(completion);
    } else if (key === 'enter') {
      event.preventDefault();
      if (current !== undefined && canChoose) choose(current);
      else if (directPath) {
        const target = parseFileOpenInput(input);
        if (target !== null) choose(target);
      }
    }
  };
  const rows = entries.map((entry, index) => {
    const isActive = index === selected;
    return <CommandPaletteItem key={entry.path} id={optionId(index)} value={entry.path}
      aria-label={entry.path} className={styles.option} isHighlighted={isActive} isSelected={isActive}
      ref={isActive ? activeRef : undefined}
      onMouseDown={event => event.preventDefault()} onSelect={() => choose(entry)}
      onMouseMove={event => {
        const { clientX: x, clientY: y, movementX, movementY } = event;
        if ((movementX === 0 && movementY === 0) || (pointer.current?.x === x && pointer.current.y === y)) return;
        pointer.current = { x, y };
        setActivePath(entry.path);
      }}>
      <span className={styles.rowContent} title={entry.path}>
        <span className={styles.entryIcon}><Icon name={entry.isDirectory ? 'folder' : 'file'} size="sm" /></span>
        <span className={styles.filename}>
          <MatchedText text={entry.name} positions={entry.positions} offset={entry.path.length - entry.name.length} />
        </span>
        {entry.directory !== '' && <span className={styles.directory}>
          <MatchedText text={entry.directory} positions={entry.positions} />
        </span>}
      </span>
    </CommandPaletteItem>;
  });
  const recentCount = entries.findIndex(entry => entry.group !== 'Recent');
  const grouped = entries.length > 0 && entries[0].group !== null;
  const split = recentCount < 0 ? entries.length : recentCount;

  return <Dialog open purpose="info" origin={{ kind: 'surface-center' }} placement="top" wide="medium" flush hideTitleRow
    title="Open a file" onClose={onClose} initialFocusRef={inputRef}>
    <CommandPaletteInput ref={inputRef} hasAutoFocus={false} className={styles.input} label="File path or name"
      placeholder="File path or name" value={input} onValueChange={change} onKeyDown={onKeyDown}
      role="combobox" aria-controls={listId} aria-expanded={entries.length > 0} aria-autocomplete="list"
      aria-activedescendant={current === undefined ? undefined : optionId(selected)}
      endContent={<>
        {busy && <Spinner size="sm" aria-label="Loading files" className={styles.spinner} />}
        <IconButton label="Close" type="button" variant="ghost" size="sm" icon={<Icon name="close" size="sm" />} onClick={onClose} />
      </>} />
    <CommandPaletteList id={listId} ref={listRef} label="Files" className={styles.list} aria-hidden={entries.length === 0 || undefined}>
      {grouped ? <>
        {split > 0 && <CommandPaletteGroup heading="Recent">{rows.slice(0, split)}</CommandPaletteGroup>}
        {split < rows.length && <CommandPaletteGroup heading="Files">{rows.slice(split)}</CommandPaletteGroup>}
      </> : rows}
    </CommandPaletteList>
    {!busy && entries.length === 0 && <CommandPaletteEmpty role="status">{emptyMessage}</CommandPaletteEmpty>}
    <CommandPaletteFooter className={styles.footer}>
      <span><Kbd keys="up" /><Kbd keys="down" /> Navigate</span>
      <span><Kbd keys="enter" /> Open</span>
      <span><Kbd keys="tab" /> Complete</span>
      <span><Kbd className={styles.lineHint} keys=":line" /> Go to line</span>
      <span><Kbd keys="escape" /> Close</span>
    </CommandPaletteFooter>
  </Dialog>;
}
