import { EmptyState } from '../../../ui/empty-state/public.tsx';
import { Icon } from '../../../ui/icon/public.tsx';
// Report change evidence uses the same disclosure and document renderer as Reference.
import type { ReactNode } from 'react';
import type { ReportChange, ReportEditEntry } from '../../../../../core/domain/daily-planner.ts';
import { MESSAGES } from '../../../../../core/domain/messages.ts';
import { Button } from '@astryxdesign/core/Button';
import { ReportDetails } from '../document/details.tsx';
import { ReportDocument } from '../document/public.tsx';
import styles from './changes.module.css';

export function ReportChangeDetails({ change, onOpenTrack, onToggleEdits, children, card = false }: Readonly<{
  card?: boolean;
  change: ReportChange; onOpenTrack: (trackId: string) => void;
  onToggleEdits: (open: boolean) => void; children: ReactNode;
}>) {
  return <ReportDetails title={change.trackTitle || 'Untitled Track'}
    meta={`${change.areaName} · ${change.editCount} report ${change.editCount === 1 ? 'edit' : 'edits'}`}
    layout="grid" variant={card ? 'card' : 'entry'}>
    <div className={styles.content}>
      <Button label="Open source report" variant="ghost" size="sm" onClick={() => onOpenTrack(change.trackId)}>Open report</Button>
      {change.summaryBefore !== change.summaryAfter && <dl className={styles.fields}>
        <dt>Previous summary</dt><dd>{change.summaryBefore || '—'}</dd>
        <dt>Current summary</dt><dd>{change.summaryAfter || '—'}</dd>
      </dl>}
      <ReportDetails title="Body changes" layout="grid" variant="entry">
        {change.patch === '' ? <EmptyState variant="compact" icon={<Icon name="file" />} title={MESSAGES.report.noNetChangeTitle} description="No net body change; individual edits retain the history." />
          : <pre className={styles.patch}><code>{change.patch}</code></pre>}
        {change.patchTruncated && <p className={styles.note}>Patch shortened. Read individual edits for the full content.</p>}
      </ReportDetails>
      <ReportDetails title="Individual edits" layout="grid" variant="entry" onToggle={onToggleEdits}>{children}</ReportDetails>
    </div>
  </ReportDetails>;
}

export function ReportEditDetails({ entry, timeZone, onOpenTrack }: Readonly<{
  entry: ReportEditEntry; timeZone: string; onOpenTrack: (trackId: string) => void;
}>) {
  const time = new Date(entry.at).toLocaleTimeString('en-GB', { timeZone, hour: '2-digit', minute: '2-digit', second: '2-digit' });
  return <ReportDetails title="Report edit" meta={time} layout="grid" variant="entry">
    <div className={styles.content}>
      <ReportDetails title="After" layout="grid" variant="entry">
        <ReportDocument report={{ summary: entry.edit.summaryAfter, body: entry.edit.bodyAfter, blocks: null }} empty={null}
          onOpenLink={(target) => onOpenTrack(target.trackId)} />
      </ReportDetails>
      <ReportDetails title="Before" layout="grid" variant="entry">
        <ReportDocument report={{ summary: entry.edit.summaryBefore, body: entry.edit.bodyBefore, blocks: null }} empty={null}
          onOpenLink={(target) => onOpenTrack(target.trackId)} />
      </ReportDetails>
    </div>
  </ReportDetails>;
}
