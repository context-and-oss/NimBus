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
  AGENT_KEYS_URL,
  EXPIRY_OPTIONS,
  NAME_MAX,
  expiryFrom,
  problemMessage,
  validateAgentId,
  type AgentKeyInfo,
  type AgentKeyIssued,
  type AgentKeysState,
  type AgentKeyStatus,
  type ExpiryChoice,
} from "./agent-keys-model";

const STATUS: Record<AgentKeyStatus, { label: string; variant: BadgeVariant }> = {
  active: { label: "Active", variant: "success" },
  revoked: { label: "Revoked", variant: "secondary" },
  expired: { label: "Expired", variant: "warning" },
};

const FORM_ID = "agent-key-issue";

type Pending = { kind: "revoke" | "remove"; key: AgentKeyInfo };

const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : "Never");

/**
 * Settings → Agent keys (Spec 039). A site Owner issues each agent its own key for the agent
 * API (/api/agent/*), sees when it was last used, and revokes or removes it. A key is shown
 * once, when it is issued; nimbus-ops keeps only its hash.
 */
export default function AgentKeysSettings() {
  const [state, setState] = useState<AgentKeysState>();
  const [error, setError] = useState<string>();
  const [message, setMessage] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [agentId, setAgentId] = useState("");
  const [name, setName] = useState("");
  const [expiry, setExpiry] = useState<ExpiryChoice>("never");
  const [formError, setFormError] = useState<string>();
  const [issued, setIssued] = useState<AgentKeyIssued>();
  const [copied, setCopied] = useState(false);
  const [pending, setPending] = useState<Pending>();
  const alive = useRef(true);
  const inPanel = useInPanel();
  usePanelDirty(agentId.trim() || name.trim() ? 1 : 0);

  useEffect(() => {
    alive.current = true;
    const controller = new AbortController();
    void (async () => {
      try {
        const response = await fetch(AGENT_KEYS_URL, { credentials: "same-origin", signal: controller.signal });
        if (!response.ok) throw new Error(await problemMessage(response));
        const next = (await response.json()) as AgentKeysState;
        if (!controller.signal.aborted) setState(next);
      } catch (cause) {
        if (!controller.signal.aborted)
          setError(cause instanceof Error ? cause.message : "Agent keys are unavailable.");
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
    const id = agentId.trim();
    const problem = validateAgentId(id);
    if (problem) {
      setFormError(problem);
      return;
    }
    setFormError(undefined);
    const response = await send(AGENT_KEYS_URL, "POST", {
      agentId: id,
      name: name.trim() || null,
      expiresAtUtc: expiryFrom(expiry),
    });
    if (!response || !alive.current) return;
    const result = (await response.json()) as AgentKeyIssued;
    setState(result.state);
    setIssued(result);
    setCopied(false);
    setAgentId("");
    setName("");
    setExpiry("never");
  }

  async function confirm() {
    if (!pending) return;
    const { kind, key } = pending;
    setPending(undefined);
    const url =
      kind === "revoke"
        ? `${AGENT_KEYS_URL}/${encodeURIComponent(key.keyId)}/revoke`
        : `${AGENT_KEYS_URL}/${encodeURIComponent(key.keyId)}`;
    const response = await send(url, kind === "revoke" ? "POST" : "DELETE");
    if (!response || !alive.current) return;
    setState((await response.json()) as AgentKeysState);
    setMessage(
      kind === "revoke"
        ? `The key for ${key.agentId} is revoked. Every instance refuses it within 30 seconds.`
        : `The key for ${key.agentId} is removed.`,
    );
  }

  async function copy() {
    if (!issued) return;
    try {
      await copyToClipboard(issued.key);
      setCopied(true);
    } catch {
      setCopied(false);
    }
  }

  if (!state) {
    return (
      <p role={error ? "alert" : "status"} className="text-sm text-muted-foreground">
        {error ?? "Loading agent keys…"}
      </p>
    );
  }

  return (
    <PanelSection tab="keys">
      <div className="space-y-4">
        <Card>
          <CardHeader className="px-5 py-4">
            {!inPanel && <CardTitle className="text-xl">Agent keys</CardTitle>}
            <CardDescription>
              Agents built on NimBus.Agents sign in to the agent API with a key issued here. A key
              works only on /api/agent/*, names the agent in the audit log, and can be revoked at any
              time. Give it to the agent as <code className="font-mono">NimBus__Agent__ApiKey</code>{" "}
              (AgentOptions.ApiKey).
            </CardDescription>
          </CardHeader>
          <CardContent className="px-5 pb-5">
            <form id={FORM_ID} onSubmit={(e) => void issue(e)} className="grid gap-3 sm:grid-cols-[1fr_1fr_auto_auto] sm:items-end">
              <div className="space-y-1">
                <Label htmlFor="agent-key-agent">Agent id</Label>
                <Input
                  id="agent-key-agent"
                  value={agentId}
                  placeholder="enrichment-agent"
                  error={!!formError}
                  aria-describedby={formError ? "agent-key-agent-error" : undefined}
                  onChange={(e) => {
                    setAgentId(e.target.value);
                    setFormError(undefined);
                  }}
                />
              </div>
              <div className="space-y-1">
                <Label htmlFor="agent-key-name">Name (optional)</Label>
                <Input
                  id="agent-key-name"
                  value={name}
                  maxLength={NAME_MAX}
                  placeholder="Production enrichment"
                  onChange={(e) => setName(e.target.value)}
                />
              </div>
              <div className="space-y-1">
                <Label htmlFor="agent-key-expiry">Expires</Label>
                <Select
                  id="agent-key-expiry"
                  value={expiry}
                  options={EXPIRY_OPTIONS.map((o) => ({ value: o.value, label: o.label }))}
                  onChange={(e) => setExpiry(e.target.value as ExpiryChoice)}
                />
              </div>
              <Button type="submit" disabled={busy}>
                Issue key
              </Button>
            </form>
            {formError && (
              <p id="agent-key-agent-error" className="mt-2 text-sm text-status-error-ink">
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
          <CardContent className="p-0">
            {state.keys.length === 0 ? (
              <div className="px-5 py-8 text-center">
                <p className="font-medium">No agent keys yet</p>
                <p className="text-sm text-muted-foreground">
                  Issue one above for each agent that calls the agent API.
                </p>
              </div>
            ) : (
              <table className="w-full text-sm">
                <thead className="border-b border-border text-left text-xs text-muted-foreground">
                  <tr>
                    <th className="px-4 py-2 font-medium">Agent</th>
                    <th className="px-4 py-2 font-medium">Key</th>
                    <th className="px-4 py-2 font-medium">Status</th>
                    <th className="px-4 py-2 font-medium">Issued</th>
                    <th className="px-4 py-2 font-medium">Last used</th>
                    <th className="px-4 py-2 font-medium">Expires</th>
                    <th className="px-4 py-2" aria-label="Actions" />
                  </tr>
                </thead>
                <tbody>
                  {state.keys.map((k) => (
                    <tr key={k.keyId} className="border-b border-border last:border-0">
                      <td className="px-4 py-2">
                        <div className="font-medium">{k.agentId}</div>
                        {k.name && <div className="text-xs text-muted-foreground">{k.name}</div>}
                      </td>
                      <td className="px-4 py-2 font-mono text-xs">{k.prefix}</td>
                      <td className="px-4 py-2">
                        <Badge variant={STATUS[k.status].variant}>{STATUS[k.status].label}</Badge>
                      </td>
                      <td className="px-4 py-2">
                        <div>{when(k.createdAtUtc)}</div>
                        {k.createdBy && <div className="text-xs text-muted-foreground">by {k.createdBy}</div>}
                      </td>
                      <td className="px-4 py-2">{when(k.lastUsedAtUtc)}</td>
                      <td className="px-4 py-2">{k.expiresAtUtc ? when(k.expiresAtUtc) : "No expiry"}</td>
                      <td className="px-4 py-2 text-right">
                        {k.status === "active" ? (
                          <Button
                            type="button"
                            size="sm"
                            variant="outline"
                            disabled={busy}
                            aria-label={`Revoke key for ${k.agentId}`}
                            onClick={() => setPending({ kind: "revoke", key: k })}
                          >
                            Revoke
                          </Button>
                        ) : (
                          <Button
                            type="button"
                            size="sm"
                            variant="outline"
                            disabled={busy}
                            aria-label={`Remove key for ${k.agentId}`}
                            onClick={() => setPending({ kind: "remove", key: k })}
                          >
                            Remove
                          </Button>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </CardContent>
        </Card>
      </div>

      <Modal isOpen={!!issued} onClose={() => setIssued(undefined)} closeOnOverlayClick={false} label="New agent key">
        <ModalHeader>Copy the key for {issued?.issued.agentId}</ModalHeader>
        <ModalBody className="space-y-3">
          <p className="text-sm">
            This key won't be shown again. Store it where the agent reads its configuration, for
            example as <code className="font-mono">NimBus__Agent__ApiKey</code>.
          </p>
          <pre className="overflow-x-auto rounded-md bg-muted px-3 py-2 font-mono text-xs">{issued?.key}</pre>
        </ModalBody>
        <ModalFooter>
          <Button type="button" variant="outline" onClick={() => void copy()}>
            {copied ? "Copied" : "Copy key"}
          </Button>
          <Button type="button" onClick={() => setIssued(undefined)}>
            Done
          </Button>
        </ModalFooter>
      </Modal>

      <Modal isOpen={!!pending} onClose={() => setPending(undefined)} label="Confirm">
        <ModalHeader>
          {pending?.kind === "revoke" ? "Revoke" : "Remove"} the key for {pending?.key.agentId}?
        </ModalHeader>
        <ModalBody>
          <p className="text-sm">
            {pending?.kind === "revoke"
              ? "The agent stops working on every instance within 30 seconds. The key stays listed until you remove it."
              : "The key disappears from this list. Its audit history stays."}
          </p>
        </ModalBody>
        <ModalFooter>
          <Button type="button" variant="outline" onClick={() => setPending(undefined)}>
            Cancel
          </Button>
          <Button type="button" colorScheme="red" onClick={() => void confirm()}>
            {pending?.kind === "revoke" ? "Revoke key" : "Remove key"}
          </Button>
        </ModalFooter>
      </Modal>
    </PanelSection>
  );
}
