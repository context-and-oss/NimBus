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
  API_KEY_HEADER,
  EXPIRY_OPTIONS,
  INTEGRATOR_KEYS_URL,
  NAME_MAX,
  ROLE_LABEL,
  ROLE_OPTIONS,
  exampleCall,
  expiryFrom,
  problemMessage,
  scopeLabel,
  validateName,
  validateScope,
  type ExpiryChoice,
  type IntegratorKeyInfo,
  type IntegratorKeyIssued,
  type IntegratorKeyRole,
  type IntegratorKeysState,
  type IntegratorKeyStatus,
} from "./integrator-keys-model";

const STATUS: Record<IntegratorKeyStatus, { label: string; variant: BadgeVariant }> = {
  active: { label: "Active", variant: "success" },
  revoked: { label: "Revoked", variant: "secondary" },
  expired: { label: "Expired", variant: "warning" },
};

const FORM_ID = "integrator-key-issue";

type Pending = { kind: "revoke" | "remove"; key: IntegratorKeyInfo };

const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : "Never");

/**
 * Settings → Integrator keys (Spec 045). A site Owner issues a key for a script or partner system
 * that calls the REST API. Each key carries a role (Reader or Contributor, never Owner), an
 * optional endpoint scope and PII access. The key is shown once; the server keeps only its hash.
 */
export default function IntegratorKeysSettings() {
  const [state, setState] = useState<IntegratorKeysState>();
  const [error, setError] = useState<string>();
  const [message, setMessage] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [name, setName] = useState("");
  const [role, setRole] = useState<IntegratorKeyRole>("reader");
  const [allEndpoints, setAllEndpoints] = useState(true);
  const [chosen, setChosen] = useState<string[]>([]);
  const [canReadPii, setCanReadPii] = useState(false);
  const [expiry, setExpiry] = useState<ExpiryChoice>("90");
  const [formError, setFormError] = useState<string>();
  const [issued, setIssued] = useState<IntegratorKeyIssued>();
  const [copied, setCopied] = useState<"key" | "call">();
  const [pending, setPending] = useState<Pending>();
  const alive = useRef(true);
  const inPanel = useInPanel();
  usePanelDirty(name.trim() ? 1 : 0);

  useEffect(() => {
    alive.current = true;
    const controller = new AbortController();
    void (async () => {
      try {
        const response = await fetch(INTEGRATOR_KEYS_URL, { credentials: "same-origin", signal: controller.signal });
        if (!response.ok) throw new Error(await problemMessage(response));
        const next = (await response.json()) as IntegratorKeysState;
        if (!controller.signal.aborted) setState(next);
      } catch (cause) {
        if (!controller.signal.aborted)
          setError(cause instanceof Error ? cause.message : "Integrator keys are unavailable.");
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

  function toggleEndpoint(endpoint: string, on: boolean) {
    setChosen((current) => (on ? [...current, endpoint] : current.filter((e) => e !== endpoint)));
    setFormError(undefined);
  }

  async function issue(event: FormEvent) {
    event.preventDefault();
    const problem = validateName(name) ?? validateScope(allEndpoints, chosen);
    if (problem) {
      setFormError(problem);
      return;
    }
    setFormError(undefined);
    const response = await send(INTEGRATOR_KEYS_URL, "POST", {
      name: name.trim(),
      role,
      endpoints: allEndpoints ? null : chosen,
      canReadPii,
      expiresAtUtc: expiryFrom(expiry),
    });
    if (!response || !alive.current) return;
    const result = (await response.json()) as IntegratorKeyIssued;
    setState(result.state);
    setIssued(result);
    setCopied(undefined);
    setName("");
    setRole("reader");
    setAllEndpoints(true);
    setChosen([]);
    setCanReadPii(false);
    setExpiry("90");
  }

  async function confirm() {
    if (!pending) return;
    const { kind, key } = pending;
    setPending(undefined);
    const url =
      kind === "revoke"
        ? `${INTEGRATOR_KEYS_URL}/${encodeURIComponent(key.keyId)}/revoke`
        : `${INTEGRATOR_KEYS_URL}/${encodeURIComponent(key.keyId)}`;
    const response = await send(url, kind === "revoke" ? "POST" : "DELETE");
    if (!response || !alive.current) return;
    setState((await response.json()) as IntegratorKeysState);
    setMessage(
      kind === "revoke"
        ? `"${key.name}" is revoked. Every instance refuses it within 30 seconds.`
        : `"${key.name}" is removed.`,
    );
  }

  async function copy(what: "key" | "call") {
    if (!issued) return;
    try {
      await copyToClipboard(what === "key" ? issued.key : exampleCall(issued.key));
      setCopied(what);
    } catch {
      setCopied(undefined);
    }
  }

  if (!state) {
    return (
      <p role={error ? "alert" : "status"} className="text-sm text-muted-foreground">
        {error ?? "Loading integrator keys…"}
      </p>
    );
  }

  return (
    <PanelSection tab="keys">
      <div className="space-y-4">
        <Card>
          <CardHeader className="px-5 py-4">
            {!inPanel && <CardTitle className="text-xl">Integrator keys</CardTitle>}
            <CardDescription>
              Scripts and partner systems call the REST API with a key in the{" "}
              <code className="font-mono text-xs">{API_KEY_HEADER}</code> header instead of signing in.
              A key can read, or read and act (resubmit, skip, settle), on every endpoint or only the
              ones you choose. It can never change settings. Issue one key per system so each can be
              revoked on its own.
            </CardDescription>
          </CardHeader>
          <CardContent className="px-5 pb-5">
            <form id={FORM_ID} onSubmit={(e) => void issue(e)} className="space-y-4">
              <div className="grid gap-3 sm:grid-cols-[1fr_auto_auto] sm:items-end">
                <div className="space-y-1">
                  <Label htmlFor="integrator-key-name">Name</Label>
                  <Input
                    id="integrator-key-name"
                    value={name}
                    maxLength={NAME_MAX}
                    placeholder="ERP reconciliation job"
                    error={!!formError}
                    aria-describedby={formError ? "integrator-key-form-error" : undefined}
                    onChange={(e) => {
                      setName(e.target.value);
                      setFormError(undefined);
                    }}
                  />
                </div>
                <div className="space-y-1">
                  <Label htmlFor="integrator-key-role">Role</Label>
                  <Select
                    id="integrator-key-role"
                    value={role}
                    options={ROLE_OPTIONS.map((o) => ({ value: o.value, label: o.label }))}
                    onChange={(e) => setRole(e.target.value as IntegratorKeyRole)}
                  />
                </div>
                <div className="space-y-1">
                  <Label htmlFor="integrator-key-expiry">Expires</Label>
                  <Select
                    id="integrator-key-expiry"
                    value={expiry}
                    options={EXPIRY_OPTIONS.map((o) => ({ value: o.value, label: o.label }))}
                    onChange={(e) => setExpiry(e.target.value as ExpiryChoice)}
                  />
                </div>
              </div>

              <fieldset className="space-y-2">
                <legend className="text-sm font-medium">Endpoints</legend>
                <Checkbox
                  id="integrator-key-all-endpoints"
                  label="Every endpoint, including ones added later"
                  checked={allEndpoints}
                  onChange={(e) => {
                    setAllEndpoints(e.target.checked);
                    setFormError(undefined);
                  }}
                />
                {!allEndpoints && (
                  <div className="grid gap-1 pl-6 sm:grid-cols-2 lg:grid-cols-3">
                    {state.endpoints.length === 0 ? (
                      <p className="text-sm text-muted-foreground">This platform defines no endpoints.</p>
                    ) : (
                      state.endpoints.map((endpoint) => (
                        <Checkbox
                          key={endpoint}
                          id={`integrator-key-endpoint-${endpoint}`}
                          label={endpoint}
                          checked={chosen.includes(endpoint)}
                          onChange={(e) => toggleEndpoint(endpoint, e.target.checked)}
                        />
                      ))
                    )}
                  </div>
                )}
              </fieldset>

              <Checkbox
                id="integrator-key-pii"
                label="May read event payloads (PII)"
                checked={canReadPii}
                onChange={(e) => setCanReadPii(e.target.checked)}
              />

              <Button type="submit" disabled={busy}>
                Issue key
              </Button>
            </form>
            {formError && (
              <p id="integrator-key-form-error" className="mt-2 text-sm text-status-error-ink">
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
                <p className="font-medium">No integrator keys yet</p>
                <p className="text-sm text-muted-foreground">Issue one above for each system that calls the API.</p>
              </div>
            ) : (
              <table className="w-full text-sm">
                <thead className="border-b border-border text-left text-xs text-muted-foreground">
                  <tr>
                    <th className="px-4 py-2 font-medium">Name</th>
                    <th className="px-4 py-2 font-medium">Access</th>
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
                        <div className="font-medium">{k.name}</div>
                        <div className="font-mono text-xs text-muted-foreground">{k.prefix}</div>
                      </td>
                      <td className="px-4 py-2">
                        <div>
                          {ROLE_LABEL[k.role]}
                          {k.canReadPii && <span className="text-muted-foreground"> · PII</span>}
                        </div>
                        <div className="text-xs text-muted-foreground">{scopeLabel(k)}</div>
                      </td>
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
                        <Button
                          type="button"
                          size="sm"
                          variant="outline"
                          disabled={busy}
                          aria-label={`${k.status === "active" ? "Revoke" : "Remove"} key ${k.name}`}
                          onClick={() => setPending({ kind: k.status === "active" ? "revoke" : "remove", key: k })}
                        >
                          {k.status === "active" ? "Revoke" : "Remove"}
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

      <Modal isOpen={!!issued} onClose={() => setIssued(undefined)} closeOnOverlayClick={false} label="New integrator key">
        <ModalHeader>Copy the key for {issued?.issued.name}</ModalHeader>
        <ModalBody className="space-y-3">
          <p className="text-sm">
            This key won't be shown again. Store it in the calling system's secret store.
          </p>
          <pre className="overflow-x-auto whitespace-pre-wrap break-all rounded-md bg-muted px-3 py-2 font-mono text-xs">
            {issued?.key ?? ""}
          </pre>
          <p className="text-sm text-muted-foreground">Try it:</p>
          <pre className="overflow-x-auto whitespace-pre-wrap break-all rounded-md bg-muted px-3 py-2 font-mono text-xs">
            {issued ? exampleCall(issued.key) : ""}
          </pre>
          <p className="text-sm text-muted-foreground">
            {issued &&
              `${ROLE_LABEL[issued.issued.role]} on ${issued.issued.endpoints === null ? "every endpoint" : issued.issued.endpoints.join(", ")}` +
                `${issued.issued.canReadPii ? ", with payload access" : ""}.`}
          </p>
        </ModalBody>
        <ModalFooter>
          <Button type="button" variant="outline" onClick={() => void copy("call")}>
            {copied === "call" ? "Copied" : "Copy example"}
          </Button>
          <Button type="button" variant="outline" onClick={() => void copy("key")}>
            {copied === "key" ? "Copied" : "Copy key"}
          </Button>
          <Button type="button" onClick={() => setIssued(undefined)}>
            Done
          </Button>
        </ModalFooter>
      </Modal>

      <Modal isOpen={!!pending} onClose={() => setPending(undefined)} label="Confirm">
        <ModalHeader>
          {pending?.kind === "revoke" ? "Revoke" : "Remove"} "{pending?.key.name}"?
        </ModalHeader>
        <ModalBody>
          <p className="text-sm">
            {pending?.kind === "revoke"
              ? "Calls with this key are refused within 30 seconds. The key stays listed until you remove it."
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
