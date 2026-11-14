import type { ReactNode } from "react";
import { Link } from "react-router-dom";

/**
 * A page-header link to a feature's Settings panel (Spec 038 §9), e.g. the
 * Heartbeat page's "Configure probing". Show it only to those who can change
 * the setting; the panel's API is site-Owner only.
 */
export function SettingsLink({ to, children }: { to: string; children: ReactNode }) {
  return (
    <Link
      to={to}
      className="inline-flex h-8 items-center gap-1.5 whitespace-nowrap rounded-nb-md border border-border-strong bg-card px-3 text-sm font-semibold text-foreground no-underline transition-colors hover:bg-muted hover:no-underline"
    >
      <span aria-hidden="true">⚙</span>
      {children}
    </Link>
  );
}
