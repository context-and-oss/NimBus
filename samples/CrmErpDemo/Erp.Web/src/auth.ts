// Sign-in support. In the Azure deployment the API requires a NimBus sign-in (the nimbus-ops
// accounts); locally it doesn't, and /api/auth/me does not exist.

// fetch for the API: a 401 means the session is missing or has expired, so go to the sign-in
// page and come back here afterwards.
export async function apiFetch(input: string, init?: RequestInit): Promise<Response> {
  const res = await fetch(input, init);
  if (res.status === 401) {
    const returnUrl = window.location.pathname + window.location.search;
    window.location.assign(`/account/login?returnUrl=${encodeURIComponent(returnUrl)}`);
  }
  return res;
}

export interface CurrentUser {
  isAuthenticated: boolean;
  email?: string | null;
  displayName?: string | null;
}

// The signed-in user, or null when sign-in is off (local runs).
export async function getCurrentUser(): Promise<CurrentUser | null> {
  const res = await fetch('/api/auth/me');
  if (!res.ok || !res.headers.get('content-type')?.includes('application/json')) return null;
  return res.json() as Promise<CurrentUser>;
}

export async function signOut(): Promise<void> {
  await fetch('/api/auth/logout', { method: 'POST' });
  window.location.assign('/account/login');
}
