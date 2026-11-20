import { Banner } from '@astryxdesign/core/Banner';
import { Button } from '@astryxdesign/core/Button';
import { TextInput } from '@astryxdesign/core/TextInput';
import { VStack } from '@astryxdesign/core/VStack';
import type { PermissionModeOptionDto } from '../../../../core/api/perga-v1.ts';
import { PermissionModePill } from '../../features/chat/thread/model-pill.tsx';
import { Dialog } from '../../ui/dialog/public.tsx';
import { useState } from '../../ui/state/public.ts';

/** The menu is shared; a protected approval change asks for proof only on selection. */
export function ConversationApprovalControl({ mode, options, disabled, authorizationMode, unavailableMode, change }: {
  mode: string;
  options: readonly PermissionModeOptionDto[];
  disabled: boolean;
  authorizationMode: string | null;
  unavailableMode: string | null;
  change: (mode: string, authorization?: string) => Promise<void>;
}) {
  const [requested, setRequested] = useState<string | null>(null);
  const [proof, setProof] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const close = () => {
    setRequested(null);
    setProof('');
    setError(null);
  };
  const valid = /^[a-fA-F0-9]{64}$/.test(proof);
  return <>
    <PermissionModePill mode={mode} options={options} isDisabled={disabled} unavailableMode={unavailableMode ?? undefined}
      onChange={next => {
        if (next === authorizationMode) setRequested(next);
        else if (next !== unavailableMode) void change(next);
      }} />
    <Dialog purpose="form" origin={{ kind: 'surface-center' }} title="Authorize approval requests" open={requested !== null}
      onClose={() => { if (!saving) close(); }}>
      {requested !== null && <VStack as="form" gap={2} onSubmit={event => {
        event.preventDefault();
        if (saving || disabled || !valid || authorizationMode !== requested) return;
        setSaving(true);
        setError(null);
        void change(requested, proof).then(close, (failure: unknown) => {
          setError(failure instanceof Error ? failure.message : 'Could not restore approval requests.');
        }).finally(() => setSaving(false));
        setProof('');
      }}>
        {error !== null && <Banner status="error" title={error} />}
        <TextInput label="Owner authorization key" type="password" value={proof} onChange={setProof} />
        <Button type="submit" label={saving ? 'Authorizing…' : 'Authorize'}
          isDisabled={saving || disabled || !valid || authorizationMode !== requested} />
      </VStack>}
    </Dialog>
  </>;
}
