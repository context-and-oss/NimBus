import { useEffect, useState } from "react";

export interface CurrentUser {
  isAuthenticated: boolean;
  email?: string;
  displayName?: string;
}

// Module-level cache: the signed-in user doesn't change during a page's
// lifetime, so every consumer shares one /api/auth/me round trip (the same
// plain-fetch endpoint the sidebar footer uses — not part of the NSwag
// contract). Errors and 404s (identity not wired in) resolve to null.
let cachedUser: CurrentUser | null | undefined;
let pendingRequest: Promise<CurrentUser | null> | null = null;

const fetchCurrentUser = (): Promise<CurrentUser | null> => {
  if (cachedUser !== undefined) return Promise.resolve(cachedUser);
  if (pendingRequest) return pendingRequest;

  pendingRequest = fetch("/api/v1/auth/me", {
    credentials: "include",
    headers: { Accept: "application/json" },
  })
    .then((res) => (res.ok ? res.json() : null))
    .then((data: CurrentUser | null) => {
      cachedUser = data;
      return data;
    })
    .catch(() => null)
    .finally(() => {
      pendingRequest = null;
    });

  return pendingRequest;
};

/** The signed-in user, or null while loading / when identity is not wired in. */
export const useCurrentUser = (): { user: CurrentUser | null } => {
  const [user, setUser] = useState<CurrentUser | null>(cachedUser ?? null);

  useEffect(() => {
    let cancelled = false;
    fetchCurrentUser().then((data) => {
      if (!cancelled && data) setUser(data);
    });
    return () => {
      cancelled = true;
    };
  }, []);

  return { user };
};
