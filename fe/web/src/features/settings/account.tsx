import type { ReactNode } from 'react';
import type { SessionIdentity } from '../../../../core/api/auth.ts';
import { SettingRow, SettingsList, SettingsPane } from './pane.tsx';

export function AccountPane({ user, setup }: Readonly<{ user: SessionIdentity; setup?: ReactNode }>) {
  if (user.mode === 'local') {
    return <SettingsPane category="account"
      lede="Set a password to sign in from your phone or local network when network access is enabled. Your local data stays in this workspace.">
      {setup}
    </SettingsPane>;
  }

  return <SettingsPane category="account" lede="Password protection is active.">
    <SettingsList>
      <SettingRow title="Username" control={user.username} />
      <SettingRow title="Display name" control={user.displayName} />
      <SettingRow title="Reset password" description="Run this command on the Perga host."
        control={<code>perga auth passwd</code>} />
    </SettingsList>
  </SettingsPane>;
}
