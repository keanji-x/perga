import type { ReactNode } from 'react';
import { isUntitledTrackTitle, trackDisplayTitle } from '../../../../core/domain/track.ts';
import { CenteredComposerIntroduction } from '../../ui/drawer/centered-composer-introduction.tsx';

export type CenteredStagePresentation = Readonly<{
  title: string;
  presented: boolean;
  onOpenAssistant: () => void;
  footerPrefix: ReactNode;
  emptyIntroduction?: ReactNode;
  render: (assistant: ReactNode) => ReactNode;
}>;

export function deriveEmptyTrackCenter(title: string): CenteredStagePresentation {
  return {
    title: 'Lead',
    presented: true,
    onOpenAssistant: () => {},
    footerPrefix: null,
    emptyIntroduction: <CenteredComposerIntroduction title={isUntitledTrackTitle(title) ? 'What should this track focus on?' : trackDisplayTitle(title)} />,
    render: assistant => assistant,
  };
}
