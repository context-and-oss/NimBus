import { useEffect, useState } from 'react';
import { getCurrentUser, signOut, type CurrentUser } from '../auth';
import { initials } from './shell/ui';

// Avatar + "Sign out" for the dark app bar. Renders nothing when sign-in is off (local runs).
export default function SignedInBadge() {
  const [user, setUser] = useState<CurrentUser | null>(null);

  useEffect(() => {
    let active = true;
    getCurrentUser()
      .then((u) => { if (active) setUser(u); })
      .catch(() => { /* sign-in is off or the API is unreachable */ });
    return () => { active = false; };
  }, []);

  if (!user?.isAuthenticated) return null;
  const name = user.displayName || user.email || '';
  return (
    <span className="flex items-center gap-2 text-xs text-white/80">
      <span title={`Signed in as ${name}`} className="grid h-7 w-7 place-items-center rounded-full bg-white/20 text-[11px] font-semibold text-white">
        {initials(name.replace(/@.*/, '').replace(/[._-]+/g, ' '))}
      </span>
      <button type="button" onClick={() => void signOut()} className="underline-offset-2 hover:underline">
        Sign out
      </button>
    </span>
  );
}
