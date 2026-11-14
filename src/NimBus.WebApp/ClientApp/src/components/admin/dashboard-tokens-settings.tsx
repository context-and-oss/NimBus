import { useEffect, useRef, useState, type FormEvent } from "react";
import { Badge, type BadgeVariant } from "components/ui/badge";
import { Button } from "components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "components/ui/card";
import { Input } from "components/ui/input";
import { Label } from "components/ui/label";
import { Modal, ModalBody, ModalFooter, ModalHeader } from "components/ui/modal";
import { Select } from "components/ui/select";
import { PanelSection, useInPanel, usePanelDirty } from "components/settings/panel-frame";
import { copyToClipboard } from "lib/clipboard";
import {
  DASHBOARD_TOKENS_URL,
  EXPIRY_OPTIONS,
  NAME_MAX,
  expiryFrom,
  monitorUrl,
  problemMessage,
  validateName,
  type DashboardTokenInfo,
  type DashboardTokenIssued,
  type DashboardTokensState,
  type DashboardTokenStatus,
  type ExpiryChoice,
} from "./dashboard-tokens-model";

const STATUS: Record<DashboardTokenStatus, { label: string; variant: BadgeVariant }> = {
  active: { label: "Active", variant: "success" },
  revoked: { label: "Revoked", variant: "secondary" },
  expired: { label: "Expired", variant: "warning" },
};

const FORM_ID = "dashboard-token-issue";

type Pending = { kind: "revoke" | "remove"; token: DashboardTokenInfo };

const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : "Never");

/**
 * Settings → Dashboard tokens. A site Owner issues a token for a wall display, which then opens
 * the Monitor read-only without signing in. The token is shown once, as part of the Monitor URL;
 * the server keeps only its hash.
 */
export default function DashboardTokensSettings() {
  const [state, setState] = useState<DashboardTokensState>();
  const [error, setError] = useState<string>();
  const [message, setMessage] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [name, setName] = useState("");
  const [expiry, setExpiry] = useState<ExpiryChoice>("365");
  const [formError, setFormError] = useState<string>();
  const [issued, setIssued] = useState<DashboardTokenIssued>();
  const [copied, setCopied] = useState(false);
  const [pending, setPending] = useState<Pending>();
  const alive = useRef(true);
  const inPanel = useInPanel();
  usePanelDirty(name.trim() ? 1 : 0);

  useEffect(() => {
    alive.current = true;
    const controller = new AbortController();
    void (async () => {
      try {
        const response = await fetch(DASHBOARD_TOKENS_URL, { credentials: "same-origin", signal: controller.signal });
        if (!response.ok) throw new Error(await problemMessage(response));
        const next = (await response.json()) as DashboardTokensState;
        if (!controller.signal.aborted) setState(next);
      } catch (cause) {
        if (!controller.signal.aborted)
          setError(cause instanceof Error ? cause.message : "Dashboard tokens are unavailable.");
      }
    })();
    return () => {
      alive.current = false;
      controller.abort();
    };
  }, []);

  async function send(url: string, method: "POST" | "DELETE", body?: unknown): Promise<Response | undefined> {
    if (!state) return undefined;
    setBusy(true);
    setError(undefined);
    setMessage(undefined);
    try {
      const response = await fetch(url, {
        method,
        credentials: "same-origin",
        headers: { "Content-Type": "application/json", "X-NimBus-CSRF": state.csrfToken },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      if (!response.ok) {
        const text = await problemMessage(response);
        if (alive.current) setError(text);
        return undefined;
      }
      return response;
    } catch {
      if (alive.current) setError("The request could not be completed. Reload before retrying.");
      return undefined;
    } finally {
      if (alive.current) setBusy(false);
    }
  }

  async function issue(event: FormEvent) {
    event.preventDefault();
    const problem = validateName(name);
    if (problem) {
      setFormError(problem);
      return;
    }
    setFormError(undefined);
    const response = await send(DASHBOARD_TOKENS_URL, "POST", {
      name: name.trim(),
      expiresAtUtc: expiryFrom(expiry),
    });
    if (!response || !alive.current) return;
    const result = (await response.json()) as DashboardTokenIssued;
    setState(result.state);
    setIssued(result);
    setCopied(false);
    setName("");
    setExpiry("365");
  }

  async function confirm() {
    if (!pending) return;
    const { kind, token } = pending;
    setPending(undefined);
    const url =
      kind === "revoke"
        ? `${DASHBOARD_TOKENS_URL}/${encodeURIComponent(token.tokenId)}/revoke`
        : `${DASHBOARD_TOKENS_URL}/${encodeURIComponent(token.tokenId)}`;
    const response = await send(url, kind === "revoke" ? "POST" : "DELETE");
    if (!response || !alive.current) return;
    setState((await response.json()) as DashboardTokensState);
    setMessage(
      kind === "revoke"
        ? `"${token.name}" is revoked. Every instance refuses it within 30 seconds.`
        : `"${token.name}" is removed.`,
    );
  }

  async function copy() {
    if (!issued) return;
    try {
      await copyToClipboard(monitorUrl(issued.token));
      setCopied(true);
    } catch {
      setCopied(false);
    }
  }

  if (!state) {
    return (
      <p role={error ? "alert" : "status"} className="text-sm text-muted-foreground">
        {error ?? "Loading dashboard tokens…"}
      </p>
    );
  }

  return (
    <PanelSection tab="tokens">
      <div className="space-y-4">
        <Card>
          <CardHeader className="px-5 py-4">
            {!inPanel && <CardTitle className="text-xl">Dashboard tokens</CardTitle>}
            <CardDescription>
              A wall display opens the Monitor with a token instead of signing in. The token shows
              the whole Monitor read-only, cannot acknowledge failures, and reaches no other page or
              API. Issue one token per wall so each can be revoked on its own.
            </CardDescription>
          </CardHeader>
          <CardContent className="px-5 pb-5">
            <form id={FORM_ID} onSubmit={(e) => void issue(e)} className="grid gap-3 sm:grid-cols-[1fr_auto_auto] sm:items-end">
              <div className="space-y-1">
                <Label htmlFor="dashboard-token-name">Name</Label>
                <Input
                  id="dashboard-token-name"
                  value={name}
                  maxLength={NAME_MAX}
                  placeholder="Operations wall, 3rd floor"
                  error={!!formError}
                  aria-describedby={formError ? "dashboard-token-name-error" : undefined}
                  onChange={(e) => {
                    setName(e.target.value);
                    setFormError(undefined);
                  }}
                />
              </div>
              <div className="space-y-1">
                <Label htmlFor="dashboard-token-expiry">Expires</Label>
                <Select
                  id="dashboard-token-expiry"
                  value={expiry}
                  options={EXPIRY_OPTIONS.map((o) => ({ value: o.value, label: o.label }))}
                  onChange={(e) => setExpiry(e.target.value as ExpiryChoice)}
                />
              </div>
              <Button type="submit" disabled={busy}>
                Issue token
              </Button>
            </form>
            {formError && (
              <p id="dashboard-token-name-error" className="mt-2 text-sm text-status-error-ink">
                {formError}
              </p>
            )}
          </CardContent>
        </Card>

        {error && (
          <p role="alert" className="rounded-md border border-status-error-200 bg-status-error-50 px-3 py-2 text-sm text-status-error-ink">
            {error}
          </p>
        )}
        {message && (
          <p role="status" className="text-sm text-muted-foreground">
            {message}
          </p>
        )}

        <Card>
          <CardContent className="overflow-x-auto p-0">
            {state.tokens.length === 0 ? (
              <div className="px-5 py-8 text-center">
                <p className="font-medium">No dashboard tokens yet</p>
                <p className="text-sm text-muted-foreground">Issue one above for each wall display.</p>
              </div>
            ) : (
              <table className="w-full text-sm">
                <thead className="border-b border-border text-left text-xs text-muted-foreground">
                  <tr>
                    <th className="px-4 py-2 font-medium">Name</th>
                    <th className="px-4 py-2 font-medium">Token</th>
                    <th className="px-4 py-2 font-medium">Status</th>
                    <th className="px-4 py-2 font-medium">Issued</th>
                    <th className="px-4 py-2 font-medium">Last used</th>
                    <th className="px-4 py-2 font-medium">Expires</th>
                    <th className="px-4 py-2" aria-label="Actions" />
                  </tr>
                </thead>
                <tbody>
                  {state.tokens.map((t) => (
                    <tr key={t.tokenId} className="border-b border-border last:border-0">
                      <td className="px-4 py-2 font-medium">{t.name}</td>
                      <td className="px-4 py-2 font-mono text-xs">{t.prefix}</td>
                      <td className="px-4 py-2">
                        <Badge variant={STATUS[t.status].variant}>{STATUS[t.status].label}</Badge>
                      </td>
                      <td className="px-4 py-2">
                        <div>{when(t.createdAtUtc)}</div>
                        {t.createdBy && <div className="text-xs text-muted-foreground">by {t.createdBy}</div>}
                      </td>
                      <td className="px-4 py-2">{when(t.lastUsedAtUtc)}</td>
                      <td className="px-4 py-2">{t.expiresAtUtc ? when(t.expiresAtUtc) : "No expiry"}</td>
                      <td className="px-4 py-2 text-right">
                        <Button
                          type="button"
                          size="sm"
                          variant="outline"
                          disabled={busy}
                          aria-label={`${t.status === "active" ? "Revoke" : "Remove"} token ${t.name}`}
                          onClick={() => setPending({ kind: t.status === "active" ? "revoke" : "remove", token: t })}
                        >
                          {t.status === "active" ? "Revoke" : "Remove"}
                        </Button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </CardContent>
        </Card>
      </div>

      <Modal isOpen={!!issued} onClose={() => setIssued(undefined)} closeOnOverlayClick={false} label="New dashboard token">
        <ModalHeader>Open this link on {issued?.issued.name}</ModalHeader>
        <ModalBody className="space-y-3">
          <p className="text-sm">
            This link won't be shown again. Open it in the wall's browser and bookmark it there.
          </p>
          <pre className="overflow-x-auto whitespace-pre-wrap break-all rounded-md bg-muted px-3 py-2 font-mono text-xs">
            {issued ? monitorUrl(issued.token) : ""}
          </pre>
          <p className="text-sm text-muted-foreground">
            The link is the credential: anyone who has it, including from the browser's history,
            sees the Monitor until you revoke the token. It stays out of server logs because the
            token travels after the #.
          </p>
        </ModalBody>
        <ModalFooter>
          <Button type="button" variant="outline" onClick={() => void copy()}>
            {copied ? "Copied" : "Copy link"}
          </Button>
          <Button type="button" onClick={() => setIssued(undefined)}>
            Done
          </Button>
        </ModalFooter>
      </Modal>

      <Modal isOpen={!!pending} onClose={() => setPending(undefined)} label="Confirm">
        <ModalHeader>
          {pending?.kind === "revoke" ? "Revoke" : "Remove"} "{pending?.token.name}"?
        </ModalHeader>
        <ModalBody>
          <p className="text-sm">
            {pending?.kind === "revoke"
              ? "The wall stops updating within 30 seconds and shows that its link is no longer valid. The token stays listed until you remove it."
              : "The token disappears from this list. Its audit history stays."}
          </p>
        </ModalBody>
        <ModalFooter>
          <Button type="button" variant="outline" onClick={() => setPending(undefined)}>
            Cancel
          </Button>
          <Button type="button" colorScheme="red" onClick={() => void confirm()}>
            {pending?.kind === "revoke" ? "Revoke token" : "Remove token"}
          </Button>
        </ModalFooter>
      </Modal>
    </PanelSection>
  );
}
