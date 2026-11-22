import { createContext, useContext, type ReactNode } from 'react';
import type { SessionIdentity } from '../../../../core/api/auth.ts';

const CurrentUser = createContext<Readonly<SessionIdentity> | null>(null);
const SignOut = createContext<((sessionId: string) => void) | null>(null);

export function CurrentUserScope({ value, children }: { value: SessionIdentity; children: ReactNode }) {
  return <CurrentUser.Provider value={value}>{children}</CurrentUser.Provider>;
}

export function useCurrentUser(): Readonly<SessionIdentity> | null { return useContext(CurrentUser); }

export function SignOutScope({ value, children }: { value: (sessionId: string) => void; children: ReactNode }) {
  return <SignOut.Provider value={value}>{children}</SignOut.Provider>;
}

export function useSignOut() { return useContext(SignOut); }
