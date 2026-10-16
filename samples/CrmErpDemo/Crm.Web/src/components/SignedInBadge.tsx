import { useEffect, useState } from 'react';
import { getCurrentUser, signOut, type CurrentUser } from '../auth';

// "Signed in as … · Sign out". Renders nothing when sign-in is off (local runs).
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
  return (
    <span className="text-xs text-slate-500">
      Signed in as <span className="font-medium text-slate-700">{user.displayName || user.email}</span>
      {' · '}
      <button type="button" onClick={() => void signOut()} className="underline hover:text-slate-700">
        Sign out
      </button>
    </span>
  );
}
