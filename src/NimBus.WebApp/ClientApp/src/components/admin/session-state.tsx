import { useState, type ReactNode } from "react";
import * as api from "api-client";
import { Badge } from "components/ui/badge";
import { Button } from "components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "components/ui/card";
import { Combobox } from "components/ui/combobox";
import { Input } from "components/ui/input";
import { Textarea } from "components/ui/textarea";
import ConfirmDestructiveAction from "./confirm-destructive-action";
import OperationErrorAlert from "./operation-error-alert";
import { describeOperationError } from "./operation-error";

interface EndpointOption {
  value: string;
  label: string;
}

type Action = api.SessionStateChangeRequestAction;
const Actions = api.SessionStateChangeRequestAction;

/** What each guided action does, for its confirmation. */
const ACTION_COPY: Record<Action, { title: string; label: string; description: string }> = {
  [Actions.ClearBlock]: {
    title: "Clear block",
    label: "Clear block",
    description:
      "Removes the blocking event from the session state, so the next message is processed. The blocking event itself stays as it is; parked messages stay parked.",
  },
  [Actions.MarkReplayed]: {
    title: "Mark parked messages replayed",
    label: "Mark replayed",
    description:
      "Records that no parked message is waiting: the deferred count becomes 0 and every assigned sequence counts as replayed. Only for a session whose parked messages are gone.",
  },
  [Actions.Reset]: {
    title: "Reset session state",
    label: "Reset state",
    description:
      "Clears the session state entirely. Use it only when nothing is queued or parked for the session.",
  },
  [Actions.Replace]: {
    title: "Replace session state",
    label: "Replace state",
    description: "Writes the edited JSON as the session state, after the server validates it.",
  },
};

/** Pretty-prints stored state for the editor; unreadable text is shown as is. */
function pretty(json: string | undefined): string {
  if (!json) return "{}";
  try {
    return JSON.stringify(JSON.parse(json), null, 2);
  } catch {
    return json;
  }
}

/**
 * Operations → Session state: look up one session's Service Bus session state on an endpoint,
 * with what is queued and parked for it, and change it with a guided action or validated JSON.
 * Every change sends the version it was based on and is refused if the state moved since.
 */
export function SessionStateCard({ endpoints, initialEndpoint }: { endpoints: EndpointOption[]; initialEndpoint?: string }) {
  const [selectedEndpoint, setSelectedEndpoint] = useState<string[]>(initialEndpoint ? [initialEndpoint] : []);
  const [sessionId, setSessionId] = useState("");
  const [view, setView] = useState<api.SessionStateView | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [pending, setPending] = useState<Action | null>(null);
  const [saving, setSaving] = useState(false);
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState("");

  const endpoint = selectedEndpoint[0];
  const session = sessionId.trim();

  function clearResult() {
    setView(null);
    setError(null);
    setNotice(null);
    setEditing(false);
  }

  async function lookup() {
    if (!endpoint || !session) return;
    setLoading(true);
    clearResult();
    try {
      const client = new api.Client(api.CookieAuth());
      setView(await client.getAdminSessionState(endpoint, session));
    } catch (err) {
      setError(describeOperationError(err));
    } finally {
      setLoading(false);
    }
  }

  async function change(action: Action) {
    if (!endpoint || !view) return;
    setPending(null);
    setSaving(true);
    setError(null);
    setNotice(null);
    try {
      const client = new api.Client(api.CookieAuth());
      const next = await client.postAdminSessionState(
        new api.SessionStateChangeRequest({
          action,
          expectedVersion: view.stateVersion ?? "",
          stateJson: action === Actions.Replace ? draft : undefined,
        }),
        endpoint,
        view.sessionId ?? session,
      );
      setView(next);
      setEditing(false);
      setNotice(`${ACTION_COPY[action].title}: done.`);
    } catch (err) {
      if (err instanceof api.SessionStateView) {
        // 409: the state moved since it was read, or a receiver holds the session.
        setView(err);
        setError(
          err.locked
            ? "A receiver holds the session right now, so nothing was changed. Try again shortly."
            : "The session state changed since you read it, so nothing was changed. Review the current state below and try again.",
        );
      } else {
        setError(describeOperationError(err));
      }
    } finally {
      setSaving(false);
    }
  }

  const parked = (view?.deferredSubscriptionCount ?? 0) + (view?.storeDeferredCount ?? 0);
  const canAct = !!view && !view.locked && !saving;
  const confirmText = `${endpoint ?? ""}/${view?.sessionId ?? session}`;

  return (
    <div className="space-y-6 w-full">
      <Card>
        <CardHeader>
          <CardTitle>Session state</CardTitle>
          <CardDescription>
            The bookkeeping an endpoint keeps per session: the blocking event, the deferred count and the replay position.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <div className="space-y-4">
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
              <Combobox
                options={endpoints}
                value={selectedEndpoint}
                onChange={(v) => {
                  setSelectedEndpoint(v);
                  clearResult();
                }}
                placeholder="Select endpoint..."
                label="Endpoint"
                multiple={false}
              />
              <div>
                <label htmlFor="session-state-id" className="block text-sm font-medium text-foreground mb-1">
                  Session ID
                </label>
                <Input
                  id="session-state-id"
                  value={sessionId}
                  onChange={(e) => {
                    setSessionId(e.target.value);
                    clearResult();
                  }}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") void lookup();
                  }}
                  placeholder="Enter session ID..."
                />
              </div>
            </div>

            <Button onClick={lookup} disabled={!endpoint || !session || loading} isLoading={loading} variant="outline">
              Look up session state
            </Button>

            <OperationErrorAlert message={error} />
            {notice && (
              <div role="status" className="bg-green-50 border border-green-200 dark:bg-green-950/30 dark:border-green-900/60 rounded-md p-3 text-sm text-green-800 dark:text-green-200">
                {notice}
              </div>
            )}

            {view && (
              <section aria-label="Session state details" className="space-y-4 rounded-md border border-border p-4">
                <div className="flex flex-wrap items-center gap-2">
                  <h4 className="m-0 font-medium text-sm">
                    Session <span className="font-mono">{view.sessionId}</span>
                  </h4>
                  {view.locked ? (
                    <Badge variant="warning">Locked by a receiver</Badge>
                  ) : view.isEmpty ? (
                    <Badge variant="default">No state</Badge>
                  ) : !view.readable ? (
                    <Badge variant="error">Unreadable</Badge>
                  ) : view.blocked ? (
                    <Badge variant="failed">Blocked</Badge>
                  ) : view.hasUnreplayedMessages ? (
                    <Badge variant="deferred">Parked messages waiting</Badge>
                  ) : (
                    <Badge variant="success">Not blocked</Badge>
                  )}
                </div>
                {view.message && <p className="m-0 text-sm text-muted-foreground">{view.message}</p>}

                {!view.locked && (
                  <dl className="grid grid-cols-2 md:grid-cols-4 gap-x-4 gap-y-2 text-sm m-0">
                    <Field label="Blocked by event" mono title={view.blockedByEventId}>
                      {view.blockedByEventId ?? "—"}
                      {view.blockingEventStatus && (
                        <span className="ml-1 font-sans text-muted-foreground">({view.blockingEventStatus})</span>
                      )}
                    </Field>
                    <Field label="Deferred count">{view.deferredCount}</Field>
                    <Field label="Next deferral sequence">{view.nextDeferralSequence}</Field>
                    <Field label="Replayed through">{view.replayedThroughSequence ?? "—"}</Field>
                    <Field label="Drain requested for">
                      {view.drainRequestedForSequence ?? "—"}
                      {view.drainRequestedAtUtc && (
                        <span className="ml-1 text-muted-foreground">at {view.drainRequestedAtUtc.toISOString()}</span>
                      )}
                    </Field>
                    <Field label="Queued on session">{view.activeMessageCount}</Field>
                    <Field label="On Deferred subscription">{view.deferredSubscriptionCount}</Field>
                    <Field label="Store pending / deferred">
                      {view.storePendingCount} / {view.storeDeferredCount}
                    </Field>
                  </dl>
                )}

                {!view.locked && view.stateJson && !editing && (
                  <pre aria-label="Stored session state" className="m-0 max-h-64 overflow-auto rounded-md bg-muted p-3 font-mono text-xs">
                    {pretty(view.stateJson)}
                  </pre>
                )}

                {!view.locked && (
                  <div className="flex flex-wrap gap-2">
                    <Button size="sm" variant="outline" onClick={lookup} disabled={loading || saving}>
                      Refresh
                    </Button>
                    {view.blocked && view.readable && (
                      <Button size="sm" onClick={() => setPending(Actions.ClearBlock)} disabled={!canAct}>
                        Clear block
                      </Button>
                    )}
                    {view.readable && ((view.deferredCount ?? 0) > 0 || view.hasUnreplayedMessages) ? (
                      <Button
                        size="sm"
                        variant="outline"
                        onClick={() => setPending(Actions.MarkReplayed)}
                        disabled={!canAct || parked > 0}
                        title={parked > 0 ? "Messages are still parked for this session" : undefined}
                      >
                        Mark parked messages replayed
                      </Button>
                    ) : null}
                    {!view.isEmpty && (
                      <Button size="sm" colorScheme="red" onClick={() => setPending(Actions.Reset)} disabled={!canAct}>
                        Reset state
                      </Button>
                    )}
                    {!editing && (
                      <Button
                        size="sm"
                        variant="ghost"
                        onClick={() => {
                          setDraft(pretty(view.stateJson));
                          setEditing(true);
                        }}
                        disabled={!canAct}
                      >
                        Edit JSON…
                      </Button>
                    )}
                  </div>
                )}

                {editing && (
                  <div className="space-y-2">
                    <label htmlFor="session-state-json" className="block text-sm font-medium text-foreground">
                      Session state JSON
                    </label>
                    <p className="m-0 text-xs text-muted-foreground">
                      Advanced. The endpoint's ordering guard relies on these values. Property names are case-sensitive;
                      the server rejects unknown properties and inconsistent values.
                    </p>
                    <Textarea
                      id="session-state-json"
                      value={draft}
                      onChange={(e) => setDraft(e.target.value)}
                      rows={12}
                      spellCheck={false}
                      className="font-mono text-xs"
                    />
                    <div className="flex gap-2">
                      <Button size="sm" colorScheme="red" onClick={() => setPending(Actions.Replace)} disabled={!canAct || !draft.trim()}>
                        Save state
                      </Button>
                      <Button size="sm" variant="outline" onClick={() => setEditing(false)} disabled={saving}>
                        Cancel
                      </Button>
                    </div>
                  </div>
                )}
              </section>
            )}
          </div>
        </CardContent>
      </Card>

      {pending && (
        <ConfirmDestructiveAction
          isOpen
          onClose={() => setPending(null)}
          onConfirm={() => change(pending)}
          title={ACTION_COPY[pending].title}
          confirmLabel={ACTION_COPY[pending].label}
          description={`${ACTION_COPY[pending].description} Session "${confirmText}".`}
          confirmText={confirmText}
          isLoading={saving}
          tone={pending === Actions.ClearBlock ? "primary" : "danger"}
        />
      )}
    </div>
  );
}

function Field({ label, mono, title, children }: { label: string; mono?: boolean; title?: string; children: ReactNode }) {
  return (
    <div className="min-w-0">
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className={`m-0 truncate ${mono ? "font-mono text-xs" : ""}`} title={title}>{children}</dd>
    </div>
  );
}
