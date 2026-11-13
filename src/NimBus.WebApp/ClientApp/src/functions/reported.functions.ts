// Pure logic for the "Reported" column. Kept free of React so the branching
// (which cell treatment to show, how to build the deep-link, how to normalize
// a typed ticket id) can be unit-tested directly.

// Server-side mirror: TicketIdPattern in EventImplementation.cs and the
// EventReports TicketId column width (64).
const TICKET_ID_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;

// Normalize a free-typed ticket reference. Empty input → null, which callers
// treat as "reported without a ticket"; invalid input → undefined, which
// callers surface as a validation error.
export const normalizeTicketId = (raw: string): string | null | undefined => {
  const trimmed = raw.trim();
  if (!trimmed) return null;
  return TICKET_ID_PATTERN.test(trimmed) ? trimmed : undefined;
};

// Build a ticket-system deep-link from the configured template (which contains
// a "{ticket}" placeholder). Returns undefined when no template is configured —
// callers then render the ticket as plain, non-clickable text.
// The site template uses {ticket}; an endpoint's own template (Spec 044 Phase 3) uses {ticketId}.
export const ticketUrl = (
  template: string | undefined | null,
  ticketId: string,
): string | undefined =>
  template
    ? template
        .replace("{ticketId}", encodeURIComponent(ticketId))
        .replace("{ticket}", encodeURIComponent(ticketId))
    : undefined;

export type ReportedCellState =
  | { kind: "report" }
  | { kind: "ticket"; ticketId: string; href?: string; tooltip: string }
  | { kind: "done"; tooltip: string };

// Decide how the Reported cell should render for an event. `reportedAtFormatted`
// is pre-formatted by the caller (formatMoment) so this stays pure/locale-free.
export const reportedCellState = (args: {
  isReported?: boolean;
  ticketId?: string;
  reportedBy?: string;
  reportedAtFormatted?: string;
  ticketLinkTemplate?: string;
}): ReportedCellState => {
  if (!args.isReported) return { kind: "report" };

  const who = args.reportedBy ? `by ${args.reportedBy}` : "";
  const when = args.reportedAtFormatted ? ` · ${args.reportedAtFormatted}` : "";

  if (args.ticketId) {
    return {
      kind: "ticket",
      ticketId: args.ticketId,
      href: ticketUrl(args.ticketLinkTemplate, args.ticketId),
      tooltip: `Reported ${who}${when}`.trim(),
    };
  }

  return { kind: "done", tooltip: `Reported ${who}${when} · no ticket`.trim() };
};
