// Authenticated source reads shared by documents, previews and the chat drawer.

import { type UseQueryResult } from '@tanstack/react-query';

import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { readFailureOf } from '../../../../core/domain/read-failure.ts';
import type { ReportSourceLinkTarget,SourceResolution } from '../../../../core/domain/report-source.ts';
import { ReportSourcePanel,reportSourcePanelTitle,ReportSourceReader,SOURCE_PANEL_COPY } from '../../features/report/source/public.tsx';
import { Drawer } from '../../ui/drawer/public.tsx';
import { type SourceRead } from '../providers/report-queries.ts';

/** Data wins while it exists; a `missing` read is data, so a dangling citation is never an error here. */
export function sourceResolutionOf(result: Pick<UseQueryResult<SourceRead>, 'data' | 'isError' | 'error'>): SourceResolution {
  if (result.data !== undefined) {
    return result.data.status === 'found' ? { status: 'ok', source: result.data.source } : { status: 'missing' };
  }
  if (result.isError) {
    return { status: 'error', failure: readFailureOf(result.error) };
  }
  return { status: 'loading' };
}

/** The Host does not publish captured-source reads. Existing citations use the missing-source presentation. */
export function ReportSourcePreview({ target }: {
  transport: ApiTransportPort; trackId: string; target: ReportSourceLinkTarget; unauthorized: UnauthorizedChannel;
}) {
  return <ReportSourcePanel target={target} resolution={{ status: 'missing' }} scrollToQuote={false} initialView="reading" onRetry={() => {}} />;
}
export function ReportSourceInDocument({ selection, onClose }: Readonly<{
  transport: ApiTransportPort; unauthorized: UnauthorizedChannel;
  selection: Readonly<{ trackId: string; target: ReportSourceLinkTarget; opener: HTMLElement | null }>;
  onClose: () => void;
}>) {
  return <ReportSourceReader selection={selection} resolution={{ status: 'missing' }} onClose={onClose} onRetry={() => {}} />;
}
export function ReportSourceDrawer({ target, onClose }: {
  transport: ApiTransportPort; trackId: string; target: ReportSourceLinkTarget | null;
  unauthorized: UnauthorizedChannel; onClose: () => void;
}) {
  return <Drawer open={target !== null} title={target === null ? '' : reportSourcePanelTitle({ status: 'missing' })}
    mobileBackLabel="Report" closeLabel={SOURCE_PANEL_COPY.closeLabel} onClose={onClose}>
    {target !== null && <ReportSourcePanel target={target} resolution={{ status: 'missing' }} onRetry={() => {}} />}
  </Drawer>;
}
