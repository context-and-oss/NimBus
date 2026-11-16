import { useEffect, useRef, useState, type FormEvent } from "react";
import { Badge, type BadgeVariant } from "components/ui/badge";
import { Button } from "components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "components/ui/card";
import { Checkbox } from "components/ui/checkbox";
import { Input } from "components/ui/input";
import { Label } from "components/ui/label";
import { Modal, ModalBody, ModalFooter, ModalHeader } from "components/ui/modal";
import { Select } from "components/ui/select";
import { PanelSection, useInPanel, usePanelDirty } from "components/settings/panel-frame";
import { copyToClipboard } from "lib/clipboard";
import {
  AGENT_KEYS_URL,
  EXPIRY_OPTIONS,
  MCP_DEFAULT_EXPIRY,
  MCP_EXPIRY_OPTIONS,
  MCP_PERMISSIONS,
  MCP_SETTINGS_URL,
  NAME_MAX,
  expiresSoon,
  expiryFrom,
  grantSummary,
  mcpKeyBlocker,
  problemMessage,
  validateAgentId,
  type AgentKeyInfo,
  type AgentKeyIssued,
  type AgentKeyMcpGrant,
  type AgentKeysState,
  type AgentKeyStatus,
  type AgentKeyUse,
  type ExpiryChoice,
  type McpKeyReadiness,
} from "./agent-keys-model";

const STATUS: Record<AgentKeyStatus, { label: string; variant: BadgeVariant }> = {
  active: { label: "Active", variant: "success" },
  revoked: { label: "Revoked", variant: "secondary" },
  expired: { label: "Expired", variant: "warning" },
};

const USE_OPTIONS = [
  { value: "agentApi", label: "Agent API (/api/agent/*)" },
  { value: "mcp", label: "MCP (/mcp, as a workload)" },
];

const FORM_ID = "agent-key-issue";

type Pending = { kind: "revoke" | "remove"; key: AgentKeyInfo };

type Grant = Pick<AgentKeyMcpGrant, "classify" | "annotate" | "resubmit">;

const NO_GRANT: Grant = { classify: false, annotate: false, resubmit: false };

const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : "Never");

/**
 * Settings → Agent keys (Spec 039, Spec 052). A site Owner issues each agent its own key, for the
 * agent API (/api/agent/*) or for the operator MCP endpoint (/mcp, as an unattended workload),
 * sees when it was last used, and rotates, revokes or removes it. A key is shown once, when it is
 * issued; nimbus-ops keeps only its hash.
 */
export default function AgentKeysSettings() {
  const [state, setState] = useState<AgentKeysState>();
  const [error, setError] = useState<string>();
  const [message, setMessage] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [agentId, setAgentId] = useState("");
  const [name, setName] = useState("");
  const [use, setUse] = useState<AgentKeyUse>("agentApi");
  const [grant, setGrant] = useState<Grant>(NO_GRANT);
  const [requestLimit, setRequestLimit] = useState("");
  const [expiry, setExpiry] = useState<ExpiryChoice>("never");
  const [formError, setFormError] = useState<string>();
  const [issued, setIssued] = useState<AgentKeyIssued>();
  const [replaced, setReplaced] = useState<AgentKeyInfo>();
  const [copied, setCopied] = useState(false);
  const [pending, setPending] = useState<Pending>();
  const [readiness, setReadiness] = useState<McpKeyReadiness>();
  const readinessRequested = useRef(false);
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

  // Once there is an MCP key, read whether the deployment and the MCP access policy let it work.
  const hasMcpKey = state?.keys.some((k) => k.use === "mcp") ?? false;
  useEffect(() => {
    if (!hasMcpKey || readinessRequested.current) return;
    readinessRequested.current = true;
    void (async () => {
      try {
        const response = await fetch(MCP_SETTINGS_URL, { credentials: "same-origin" });
        if (response.ok && alive.current) setReadiness((await response.json()) as McpKeyReadiness);
      } catch {
        // The rows then just don't say whether MCP keys can work yet.
      }
    })();
  }, [hasMcpKey]);

  const blocker = mcpKeyBlocker(readiness);

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

  function chooseUse(next: AgentKeyUse) {
    setUse(next);
    if (next === "mcp" && expiry === "never") setExpiry(MCP_DEFAULT_EXPIRY);
  }

  async function issue(event: FormEvent) {
    event.preventDefault();
    const id = agentId.trim();
    const problem = validateAgentId(id);
    if (problem) {
      setFormError(problem);
      return;
    }
    const limit = requestLimit.trim() ? Number(requestLimit) : null;
    if (use === "mcp" && limit !== null && (!Number.isInteger(limit) || limit < 1)) {
      setFormError("The request limit is a whole number of at least 1, or empty for the deployment's limit.");
      return;
    }
    setFormError(undefined);
    const body =
      use === "mcp"
        ? { agentId: id, name: name.trim() || null, expiresAtUtc: expiryFrom(expiry), use, mcp: { ...grant, requestsPerWindow: limit } }
        : { agentId: id, name: name.trim() || null, expiresAtUtc: expiryFrom(expiry) };
    const response = await send(AGENT_KEYS_URL, "POST", body);
    if (!response || !alive.current) return;
    const result = (await response.json()) as AgentKeyIssued;
    setState(result.state);
    setIssued(result);
    setReplaced(undefined);
    setCopied(false);
    setAgentId("");
    setName("");
    setUse("agentApi");
    setGrant(NO_GRANT);
    setRequestLimit("");
    setExpiry("never");
  }

  async function rotate(key: AgentKeyInfo) {
    const response = await send(`${AGENT_KEYS_URL}/${encodeURIComponent(key.keyId)}/rotate`, "POST", {});
    if (!response || !alive.current) return;
    const result = (await response.json()) as AgentKeyIssued;
    setState(result.state);
    setIssued(result);
    setReplaced(key);
    setCopied(false);
  }

  async function revokeReplaced() {
    if (!replaced) return;
    const old = replaced;
    const response = await send(`${AGENT_KEYS_URL}/${encodeURIComponent(old.keyId)}/revoke`, "POST");
    if (!response || !alive.current) return;
    setState((await response.json()) as AgentKeysState);
    setIssued(undefined);
    setReplaced(undefined);
    setMessage(`The old key for ${old.agentId} is revoked. Every instance refuses it within 30 seconds.`);
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

  const issuedIsMcp = issued?.issued.use === "mcp";
  const endpointUrl = readiness?.deployment.endpointUrl ?? `${window.location.origin}/mcp`;

  return (
    <PanelSection tab="keys">
      <div className="space-y-4">
        <Card>
          <CardHeader className="px-5 py-4">
            {!inPanel && <CardTitle className="text-xl">Agent keys</CardTitle>}
            <CardDescription>
              An Agent API key lets an agent built on NimBus.Agents call /api/agent/*; give it to the
              agent as <code className="font-mono">NimBus__Agent__ApiKey</code>. An MCP key lets a hosted
              agent that can only send a fixed secret use the operator MCP endpoint as an unattended
              workload: it never skips messages or reads payloads. Every key names its agent in the audit
              log and can be revoked at any time.
            </CardDescription>
          </CardHeader>
          <CardContent className="space-y-3 px-5 pb-5">
            <form id={FORM_ID} onSubmit={(e) => void issue(e)} className="grid gap-3 sm:grid-cols-[1fr_1fr_auto_auto_auto] sm:items-end">
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
                <Label htmlFor="agent-key-use">Use</Label>
                <Select
                  id="agent-key-use"
                  value={use}
                  options={USE_OPTIONS}
                  onChange={(e) => chooseUse(e.target.value as AgentKeyUse)}
                />
              </div>
              <div className="space-y-1">
                <Label htmlFor="agent-key-expiry">Expires</Label>
                <Select
                  id="agent-key-expiry"
                  value={expiry}
                  options={(use === "mcp" ? MCP_EXPIRY_OPTIONS : EXPIRY_OPTIONS).map((o) => ({ value: o.value, label: o.label }))}
                  onChange={(e) => setExpiry(e.target.value as ExpiryChoice)}
                />
              </div>
              <Button type="submit" disabled={busy}>
                Issue key
              </Button>
            </form>
            {use === "mcp" && (
              <fieldset className="space-y-2 rounded-md border border-border p-3">
                <legend className="px-1 text-xs font-medium text-muted-foreground">What the key may do besides reading</legend>
                {MCP_PERMISSIONS.map((p) => (
                  <div key={p.key}>
                    <Checkbox
                      id={`agent-key-${p.key}`}
                      label={p.label}
                      checked={grant[p.key]}
                      onChange={(e) => setGrant({ ...grant, [p.key]: e.target.checked })}
                    />
                    <p className="ml-6 text-xs text-muted-foreground">Also needs “{p.needs}” in Settings → MCP access.</p>
                  </div>
                ))}
                <div className="max-w-xs space-y-1 pt-1">
                  <Label htmlFor="agent-key-limit">Requests per window (optional)</Label>
                  <Input
                    id="agent-key-limit"
                    inputMode="numeric"
                    value={requestLimit}
                    placeholder="The deployment's limit"
                    onChange={(e) => setRequestLimit(e.target.value)}
                  />
                </div>
              </fieldset>
            )}
            {formError && (
              <p id="agent-key-agent-error" className="text-sm text-status-error-ink">
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
            {state.keys.length === 0 ? (
              <div className="px-5 py-8 text-center">
                <p className="font-medium">No agent keys yet</p>
                <p className="text-sm text-muted-foreground">
                  Issue one above for each agent that calls the agent API or the MCP endpoint.
                </p>
              </div>
            ) : (
              <table className="w-full text-sm">
                <thead className="border-b border-border text-left text-xs text-muted-foreground">
                  <tr>
                    <th className="px-4 py-2 font-medium">Agent and key</th>
                    <th className="px-4 py-2 font-medium">Use</th>
                    <th className="px-4 py-2 font-medium">Status</th>
                    <th className="px-4 py-2 font-medium">Issued</th>
                    <th className="px-4 py-2 font-medium">Last used</th>
                    <th className="px-4 py-2 font-medium">Expires</th>
                    <th className="px-4 py-2" aria-label="Actions" />
                  </tr>
                </thead>
                <tbody>
                  {state.keys.map((k) => (
                    <tr key={k.keyId} className="border-b border-border align-top last:border-0">
                      <td className="px-4 py-2">
                        <div className="whitespace-nowrap font-medium">{k.agentId}</div>
                        {k.name && <div className="text-xs text-muted-foreground">{k.name}</div>}
                        <div className="font-mono text-xs text-muted-foreground">{k.prefix}</div>
                      </td>
                      <td className="px-4 py-2">
                        {k.use === "mcp" ? (
                          <>
                            <div>MCP</div>
                            <div className="text-xs text-muted-foreground">{grantSummary(k.mcp)}</div>
                            {k.status === "active" && blocker && (
                              <div className="mt-1 max-w-xs text-xs text-status-warning-ink">Won't work yet: {blocker}</div>
                            )}
                          </>
                        ) : (
                          <div>Agent API</div>
                        )}
                      </td>
                      <td className="px-4 py-2">
                        <div className="flex flex-wrap gap-1">
                          <Badge variant={STATUS[k.status].variant}>{STATUS[k.status].label}</Badge>
                          {expiresSoon(k) && <Badge variant="warning">Expires soon</Badge>}
                        </div>
                      </td>
                      <td className="px-4 py-2">
                        <div>{when(k.createdAtUtc)}</div>
                        {k.createdBy && <div className="text-xs text-muted-foreground">by {k.createdBy}</div>}
                      </td>
                      <td className="px-4 py-2">{when(k.lastUsedAtUtc)}</td>
                      <td className="px-4 py-2">{k.expiresAtUtc ? when(k.expiresAtUtc) : "No expiry"}</td>
                      <td className="px-4 py-2 text-right">
                        {k.status === "active" ? (
                          <div className="flex flex-col items-end gap-2">
                            <Button
                              type="button"
                              size="sm"
                              variant="outline"
                              disabled={busy}
                              aria-label={`Rotate key for ${k.agentId}`}
                              onClick={() => void rotate(k)}
                            >
                              Rotate
                            </Button>
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
                          </div>
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

      <Modal
        isOpen={!!issued}
        onClose={() => {
          setIssued(undefined);
          setReplaced(undefined);
        }}
        closeOnOverlayClick={false}
        label="New agent key"
      >
        <ModalHeader>Copy the key for {issued?.issued.agentId}</ModalHeader>
        <ModalBody className="space-y-3">
          <p className="text-sm">
            This key won't be shown again.{" "}
            {issuedIsMcp ? (
              <>Give it to the agent's MCP connector for <code className="font-mono">{endpointUrl}</code>, as either header:</>
            ) : (
              <>Store it where the agent reads its configuration, for example as <code className="font-mono">NimBus__Agent__ApiKey</code>.</>
            )}
          </p>
          <pre className="overflow-x-auto rounded-md bg-muted px-3 py-2 font-mono text-xs">{issued?.key}</pre>
          {issuedIsMcp && (
            <>
              <pre className="overflow-x-auto rounded-md bg-muted px-3 py-2 font-mono text-xs">
                {"Authorization: Bearer <key>\nX-NimBus-Agent-Key: <key>"}
              </pre>
              <p className="text-sm">
                The agent also needs a Reader or Contributor grant to{" "}
                <code className="font-mono">agent:{issued?.issued.agentId}</code> in{" "}
                <a href="/AccessControl" className="underline">Access Control</a>, site-wide or per endpoint.
              </p>
              {blocker && <p className="text-sm text-status-warning-ink">It won't work yet: {blocker}</p>}
            </>
          )}
          {replaced && (
            <p className="text-sm">
              The old key keeps working until you revoke it or it expires. Revoke it once the agent uses the new one.
            </p>
          )}
        </ModalBody>
        <ModalFooter>
          {replaced && (
            <Button type="button" variant="outline" colorScheme="red" disabled={busy} onClick={() => void revokeReplaced()}>
              Revoke the old key
            </Button>
          )}
          <Button type="button" variant="outline" onClick={() => void copy()}>
            {copied ? "Copied" : "Copy key"}
          </Button>
          <Button
            type="button"
            onClick={() => {
              setIssued(undefined);
              setReplaced(undefined);
            }}
          >
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
