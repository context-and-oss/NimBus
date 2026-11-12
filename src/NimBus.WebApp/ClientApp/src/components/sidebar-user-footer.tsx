import * as React from "react";
const { useEffect, useState } = React;
import { cn } from "lib/utils";
import { useEnv } from "hooks/app-status";

interface CurrentUser {
  isAuthenticated: boolean;
  email?: string;
  displayName?: string;
}

const initialsFor = (email: string | undefined, displayName: string | undefined): string => {
  const source = (displayName ?? email ?? "").trim();
  if (!source) return "·";
  const parts = source.split(/[\s.@]+/).filter(Boolean);
  if (parts.length >= 2) {
    return (parts[0][0] + parts[1][0]).toUpperCase();
  }
  return source.slice(0, 2).toUpperCase();
};

const SignOutIcon = () => (
  <svg className="w-3.5 h-3.5" viewBox="0 0 16 16" fill="none" aria-hidden="true">
    <path
      d="M9 2H4a1 1 0 0 0-1 1v10a1 1 0 0 0 1 1h5"
      stroke="currentColor"
      strokeWidth="1.4"
      strokeLinecap="round"
    />
    <path
      d="M7 8h7m0 0-2.5-2.5M14 8l-2.5 2.5"
      stroke="currentColor"
      strokeWidth="1.4"
      strokeLinecap="round"
      strokeLinejoin="round"
    />
  </svg>
);

const SidebarUserFooter: React.FC = () => {
  const [user, setUser] = useState<CurrentUser | null>(null);
  const [endpointMissing, setEndpointMissing] = useState(false);
  const env = useEnv();

  useEffect(() => {
    let cancelled = false;
    fetch("/api/v1/auth/me", {
      credentials: "include",
      headers: { Accept: "application/json" },
    })
      .then((res) => {
        if (cancelled) return;
        if (res.status === 404) {
          // Identity not wired in (e.g. Entra-only deployment). Hide quietly.
          setEndpointMissing(true);
          return null;
        }
        if (!res.ok) return null;
        return res.json();
      })
      .then((data: CurrentUser | null) => {
        if (cancelled) return;
        if (data) setUser(data);
      })
      .catch(() => {
        // Network blip or unauthenticated — leave footer in initial state.
      });
    return () => {
      cancelled = true;
    };
  }, []);

  if (endpointMissing) return null;
  if (!user || !user.isAuthenticated) return null;

  const initials = initialsFor(user.email, user.displayName);
  const primary = user.displayName?.trim() || user.email?.trim() || "Signed in";
  const secondary = user.displayName?.trim() && user.email?.trim()
    ? user.email
    : `Operator${env ? ` · ${env}` : ""}`;

  const handleSignOut = async () => {
    try {
      await fetch("/api/v1/auth/logout", {
        method: "POST",
        credentials: "include",
      });
    } catch {
      // Even if the call fails, the cookie may have already cleared. Best effort.
    }
    // Full navigation so the cookie state on the next page is clean.
    window.location.href = "/account/login";
  };

  return (
    <div
      className={cn(
        "mt-auto pt-2.5 px-2 border-t border-[#2A2620]",
        "flex items-center gap-2.5",
      )}
    >
      <span
        className={cn(
          "w-7 h-7 rounded-full inline-flex items-center justify-center text-[11px] font-bold text-white shrink-0",
          "bg-linear-to-br from-[#E8743C] to-[#C2412E]",
        )}
        aria-hidden="true"
      >
        {initials}
      </span>
      <div className="min-w-0 flex-1 leading-tight">
        <div className="text-[12px] text-[#F4F2EA] truncate" title={primary}>
          {primary}
        </div>
        <div
          className="font-mono text-[10px] text-[#6F6A5C] truncate"
          title={secondary}
        >
          {secondary}
        </div>
      </div>
      <button
        type="button"
        onClick={handleSignOut}
        title="Sign out"
        aria-label="Sign out"
        className={cn(
          "shrink-0 p-1.5 rounded-md text-[#C9C1AB]",
          "hover:bg-white/4 hover:text-[#F4F2EA] transition-colors",
          "focus:outline-hidden focus:ring-2 focus:ring-primary",
        )}
      >
        <SignOutIcon />
      </button>
    </div>
  );
};

export default SidebarUserFooter;
