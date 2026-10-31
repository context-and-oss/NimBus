// Messages for a refused operator command (resubmit, skip, resubmit with changes).
// The server answers 409 when the message changed since the page loaded it —
// someone (another operator, or an agent through the MCP server) already
// resubmitted or skipped it, or it failed again — and 503 when the audit log
// could not record the command. In both cases nothing was sent.

type CommandAction = "resubmit" | "skip";

export const STALE_COMMAND_MESSAGE =
  "This message changed since it was loaded: it was resubmitted, skipped or failed again. Refresh and try again.";

const statusOf = (caught: unknown): number | undefined =>
  (caught as { status?: number } | null)?.status;

/** True when the server refused the command because the message had changed. */
export const isStaleCommand = (caught: unknown): boolean =>
  statusOf(caught) === 409;

/** A toast-ready explanation of why deleting an event failed. */
export const describeDeleteError = (caught: unknown): string => {
  switch (statusOf(caught)) {
    case 409:
      return "This event blocks its session, so it was not deleted. Skip or resubmit it first.";
    case 404:
      return "The event was not found. Refresh and try again.";
    default:
      return "The delete failed.";
  }
};

/** A toast-ready explanation of why an operator command was refused. */
export const describeCommandError = (
  caught: unknown,
  action: CommandAction,
): string => {
  switch (statusOf(caught)) {
    case 409:
      return STALE_COMMAND_MESSAGE;
    case 503:
      return `The audit log is unavailable, so the ${action} was not run. Try again later.`;
    case 403:
      return `You do not have permission to ${action} this message.`;
    case 404:
      return "The message was not found. Refresh and try again.";
    default:
      return `The ${action} failed.`;
  }
};
