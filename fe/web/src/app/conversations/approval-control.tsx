import type { PermissionModeOptionDto } from '../../../../core/api/perga-v1.ts';
import { PermissionModePill } from '../../features/chat/thread/model-pill.tsx';

export function ConversationApprovalControl({ mode, options, disabled, change }: {
  mode: string;
  options: readonly PermissionModeOptionDto[];
  disabled: boolean;
  change: (mode: string) => Promise<void>;
}) {
  return <PermissionModePill mode={mode} options={options} isDisabled={disabled} onChange={next => { void change(next); }} />;
}
