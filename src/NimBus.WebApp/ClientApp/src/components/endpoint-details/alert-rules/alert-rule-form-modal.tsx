import { useEffect, useState, type FormEvent, type ReactNode } from "react";
import * as api from "api-client";
import { errorMessage } from "api-client/problem";
import {
  Button,
  Checkbox,
  Input,
  Label,
  Modal,
  ModalBody,
  ModalFooter,
  ModalHeader,
  Select,
  Toggle,
  useToast,
} from "components/ui";
import { Combobox, type ComboboxOption } from "components/ui/combobox";
import { getEventTypesByEndpoint } from "hooks/event-types";
import { copyToClipboard } from "lib/clipboard";
import AlertRulePreviewPanel from "./alert-rule-preview";
import {
  BLOCKED_MAX,
  BLOCKED_MIN,
  CATEGORIES_MAX,
  CONDITION_KINDS,
  DEFERRED_MAX,
  DEFERRED_MIN,
  ERROR_TEXT_MAX,
  FAILURE_STATUSES,
  KIND_LABELS,
  NAME_MAX,
  RESOLVE_MAX,
  RESOLVE_MIN,
  SILENCE_MAX,
  SILENCE_MIN,
  TARGETS_MAX,
  THRESHOLD_MAX,
  THRESHOLD_MIN,
  WINDOW_MAX,
  WINDOW_MIN,
  categoryLabel,
  draftFromRule,
  emptyDraft,
  kindOf,
  newTargetKey,
  requestFromDraft,
  validateDraft,
  type ConditionKind,
  type RuleDraft,
  type TargetType,
} from "./alert-rules-model";

export interface AlertRuleFormModalProps {
  endpointId: string;
  /** The rule to edit; none creates a rule. */
  rule?: api.AlertRuleModel;
  mailEnabled: boolean;
  webhookEnabled: boolean;
  /** The condition kinds the deployment can evaluate; an edited rule's own kind is offered too. */
  availableKinds: ConditionKind[];
  /** Whether rules can filter by classification category on this deployment. */
  classificationAvailable: boolean;
  /** The categories a rule can filter by, in the server's order. */
  categories: string[];
  onClose: () => void;
  /** Called after a successful save, before any secret is shown. */
  onSaved: (saved: api.AlertRuleSaved) => void;
}

const FORM_ID = "alert-rule-form";

const FieldError = ({ id, text }: { id: string; text?: string }) =>
  text ? (
    <p id={id} className="m-0 mt-1 text-xs text-status-error-ink">
      {text}
    </p>
  ) : null;

const Hint = ({ children }: { children: ReactNode }) => (
  <p className="m-0 mt-1 text-xs text-muted-foreground">{children}</p>
);

/** What each kind watches, under the kind picker. */
function kindHelp(kind: ConditionKind, endpointId: string): string {
  switch (kind) {
    case "failure_occurrences":
      return `Counts the failed attempts on ${endpointId} in the window, from message history, even when the events were resolved since.`;
    case "blocked_session":
      return `Watches for sessions on ${endpointId} held up by a failure: the oldest Failed or handed-off event has not changed for the given minutes, or at least the given number of events are deferred.`;
    case "silence":
      return `Fires when no message on ${endpointId} has completed for the given minutes, for example when a producer stops sending.`;
    default:
      return `Counts the events on ${endpointId} that are failed now (Failed, DeadLettered or Unsupported, narrowed by the filters below), however long ago they failed. Resolved means the failures were cleared by a resubmit, skip or delete.`;
  }
}

interface NumberFieldProps {
  id: string;
  label: string;
  value: string;
  min: number;
  max: number;
  error?: string;
  /** The id of an error this field shares with another, which also marks this field. */
  sharedErrorId?: string;
  hint: ReactNode;
  onChange: (value: string) => void;
}

const NumberField = ({ id, label, value, min, max, error, sharedErrorId, hint, onChange }: NumberFieldProps) => {
  const errorId = `${id}-error`;
  const describedBy = [error ? errorId : undefined, sharedErrorId].filter(Boolean).join(" ") || undefined;
  return (
    <div>
      <Label htmlFor={id}>{label}</Label>
      <Input
        id={id}
        className="mt-1"
        type="number"
        inputMode="numeric"
        min={min}
        max={max}
        value={value}
        error={!!error || !!sharedErrorId}
        aria-describedby={describedBy}
        onChange={(e) => onChange(e.currentTarget.value)}
      />
      <FieldError id={errorId} text={error} />
      <Hint>{hint}</Hint>
    </div>
  );
};

// New rule / Edit rule. The endpoint details page is a Page, not a Workspace, so the form lives
// in a modal instead of the Manage pages' inspector drawer. A new webhook target's signing secret
// comes back only in the save response, so the modal then stays open on the secrets until Done.
export default function AlertRuleFormModal(props: AlertRuleFormModalProps) {
  const {
    endpointId,
    rule,
    mailEnabled,
    webhookEnabled,
    availableKinds,
    classificationAvailable,
    categories,
    onClose,
    onSaved,
  } = props;
  const editing = !!rule;
  const { addToast } = useToast();
  const [client] = useState(() => new api.Client(api.CookieAuth()));
  const [draft, setDraft] = useState<RuleDraft>(() => (rule ? draftFromRule(rule) : emptyDraft()));
  const [submitted, setSubmitted] = useState(false);
  const [saving, setSaving] = useState(false);
  const [serverError, setServerError] = useState<string>();
  const [catalog, setCatalog] = useState<string[]>([]);
  const [saved, setSaved] = useState<api.AlertRuleSaved>();

  useEffect(() => {
    let active = true;
    getEventTypesByEndpoint(endpointId)
      .then((result) => {
        const names = (groups: typeof result.consumes) =>
          (groups ?? [])
            .flatMap((group) => group.events ?? [])
            .map((event) => event.name)
            .filter((name): name is string => Boolean(name));
        if (active) setCatalog([...names(result.consumes), ...names(result.produces)]);
      })
      .catch(() => {
        // The catalog only offers names; a rule without event types counts every type.
        if (active) setCatalog([]);
      });
    return () => {
      active = false;
    };
  }, [endpointId]);

  // The rule's own types stay pickable even when the catalog no longer lists them.
  const eventTypeOptions: ComboboxOption[] = [...new Set([...catalog, ...draft.eventTypes])].map((name) => ({
    value: name,
    label: name,
  }));

  const errors = submitted ? validateDraft(draft, { mailEnabled, webhookEnabled }) : {};
  const update = (changes: Partial<RuleDraft>) => {
    setDraft((d) => ({ ...d, ...changes }));
    setServerError(undefined);
  };

  const addTarget = (type: TargetType) =>
    update({ targets: [...draft.targets, { key: newTargetKey(), type, value: "" }] });
  const setTarget = (key: string, value: string) =>
    update({ targets: draft.targets.map((t) => (t.key === key ? { ...t, value } : t)) });
  const removeTarget = (key: string) => update({ targets: draft.targets.filter((t) => t.key !== key) });
  const toggleStatus = (status: string, on: boolean) =>
    update({
      statuses: on
        ? FAILURE_STATUSES.filter((s) => s === status || draft.statuses.includes(s))
        : draft.statuses.filter((s) => s !== status),
    });

  // A rule saved with categories keeps them on show when classifications can't be read now, so they
  // can be removed; otherwise the server refuses the save with nothing in the form to fix.
  const categoryOptions = classificationAvailable
    ? [...new Set([...categories, ...draft.categories])]
    : draft.categories;
  const toggleCategory = (category: string, on: boolean) =>
    update({
      categories: on
        ? categoryOptions.filter((c) => c === category || draft.categories.includes(c))
        : draft.categories.filter((c) => c !== category),
    });

  async function save(event: FormEvent) {
    event.preventDefault();
    setSubmitted(true);
    if (Object.keys(validateDraft(draft, { mailEnabled, webhookEnabled })).length > 0) return;
    setSaving(true);
    setServerError(undefined);
    try {
      const body = requestFromDraft(draft);
      const result =
        editing && rule?.id
          ? await client.putEndpointAlertRule(endpointId, rule.id, body)
          : await client.postEndpointAlertRule(endpointId, body);
      onSaved(result);
      addToast({
        variant: "success",
        title: editing ? `Saved "${body.name}".` : `Created "${body.name}".`,
        duration: 4000,
      });
      if ((result.secrets ?? []).length > 0) setSaved(result);
      else onClose();
    } catch (error) {
      setServerError(errorMessage(error) || "The rule could not be saved.");
    } finally {
      setSaving(false);
    }
  }

  const copySecret = (secret: string) => {
    copyToClipboard(secret)
      .then(() => addToast({ variant: "success", title: "Secret copied.", duration: 3000 }))
      .catch(() =>
        addToast({
          variant: "error",
          title: "Could not copy the secret.",
          description: "Select it and copy it by hand.",
          duration: 6000,
        }),
      );
  };

  if (saved) {
    const secrets = saved.secrets ?? [];
    const urlOf = (targetId?: string) => saved.rule?.targets?.find((t) => t.id === targetId)?.url ?? "the webhook";
    return (
      <Modal isOpen onClose={onClose} size="lg" closeOnOverlayClick={false} label="Webhook signing secrets">
        <ModalHeader>Copy the signing {secrets.length === 1 ? "secret" : "secrets"}</ModalHeader>
        <ModalBody className="space-y-4">
          <p className="m-0 text-sm">
            {secrets.length === 1 ? "This secret won't" : "These secrets won't"} be shown again. Give each to its
            receiver so it can check that a notice comes from NimBus.
          </p>
          {secrets.map((s) => (
            <div key={s.targetId} className="space-y-1.5">
              <p className="m-0 break-all font-mono text-xs text-muted-foreground">{urlOf(s.targetId)}</p>
              <div className="flex items-start gap-2">
                <pre className="m-0 min-w-0 flex-1 overflow-x-auto rounded-md bg-muted px-3 py-2 font-mono text-xs">
                  {s.signingSecret}
                </pre>
                <Button
                  type="button"
                  size="sm"
                  variant="outline"
                  aria-label={`Copy secret for ${urlOf(s.targetId)}`}
                  onClick={() => s.signingSecret && copySecret(s.signingSecret)}
                >
                  Copy
                </Button>
              </div>
            </div>
          ))}
          <p className="m-0 text-sm text-muted-foreground">
            Each request carries <code className="font-mono">X-NimBus-Timestamp</code> and{" "}
            <code className="font-mono">X-NimBus-Signature</code>: <code className="font-mono">v1=</code> followed by
            the HMAC-SHA256 of the timestamp, a dot and the request body, keyed with the secret. To replace a secret,
            remove the target, save, and add it again.
          </p>
        </ModalBody>
        <ModalFooter>
          <Button type="button" onClick={onClose}>
            Done
          </Button>
        </ModalFooter>
      </Modal>
    );
  }

  const atTargetLimit = draft.targets.length >= TARGETS_MAX;
  const title = editing ? `Edit rule ${rule?.name ?? ""}`.trim() : "New rule";
  // A rule keeps its own kind selectable even if the deployment no longer offers it.
  const kindOptions = CONDITION_KINDS.filter(
    (k) => availableKinds.includes(k) || (rule !== undefined && kindOf(rule.condition) === k),
  ).map((k) => ({ value: k, label: KIND_LABELS[k] }));
  const counted = draft.kind === "failure_backlog" || draft.kind === "failure_occurrences";
  const resolveField = (
    <NumberField
      id="alert-rule-resolve"
      label="Resolve after (minutes)"
      value={draft.resolveAfterMinutes}
      min={RESOLVE_MIN}
      max={RESOLVE_MAX}
      error={errors.resolveAfterMinutes}
      hint={
        <>
          How long the {counted ? "count must stay below the threshold" : "condition must stay clear"} before the rule
          resolves ({RESOLVE_MIN} to {RESOLVE_MAX}).
        </>
      }
      onChange={(resolveAfterMinutes) => update({ resolveAfterMinutes })}
    />
  );

  return (
    <Modal isOpen onClose={onClose} size="xl" closeOnOverlayClick={false} label={title}>
      <ModalHeader onClose={onClose}>{title}</ModalHeader>
      <ModalBody>
        <form id={FORM_ID} noValidate onSubmit={(e) => void save(e)} className="space-y-4">
          <p className="m-0 text-sm text-muted-foreground">
            The rule fires when its condition holds on {endpointId}, and resolves when the condition has stayed clear for
            the resolve-after time. Each target gets one notice when the rule starts firing and one when it resolves.
          </p>

          <div>
            <Label htmlFor="alert-rule-name">Name</Label>
            <Input
              id="alert-rule-name"
              className="mt-1"
              value={draft.name}
              maxLength={NAME_MAX}
              placeholder="ERP timeouts"
              error={!!errors.name}
              aria-describedby={errors.name ? "alert-rule-name-error" : undefined}
              onChange={(e) => update({ name: e.currentTarget.value })}
            />
            <FieldError id="alert-rule-name-error" text={errors.name} />
          </div>

          <div>
            <Label htmlFor="alert-rule-kind">Condition</Label>
            <Select
              id="alert-rule-kind"
              className="mt-1"
              value={draft.kind}
              options={kindOptions}
              aria-describedby="alert-rule-kind-help"
              onChange={(e) => update({ kind: e.currentTarget.value as ConditionKind })}
            />
            <p id="alert-rule-kind-help" className="m-0 mt-1 text-xs text-muted-foreground">
              {kindHelp(draft.kind, endpointId)}
            </p>
          </div>

          <div className="grid gap-4 sm:grid-cols-2">
            {counted && (
              <NumberField
                id="alert-rule-threshold"
                label="Threshold"
                value={draft.threshold}
                min={THRESHOLD_MIN}
                max={THRESHOLD_MAX}
                error={errors.threshold}
                hint={
                  draft.kind === "failure_occurrences" ? (
                    <>
                      Fire when at least this many attempts fail in the window ({THRESHOLD_MIN} to {THRESHOLD_MAX}).
                    </>
                  ) : (
                    <>
                      Fire when at least this many events are failed at once ({THRESHOLD_MIN} to {THRESHOLD_MAX}).
                    </>
                  )
                }
                onChange={(threshold) => update({ threshold })}
              />
            )}
            {draft.kind === "failure_occurrences" && (
              <NumberField
                id="alert-rule-window"
                label="Window (minutes)"
                value={draft.windowMinutes}
                min={WINDOW_MIN}
                max={WINDOW_MAX}
                error={errors.windowMinutes}
                hint={
                  <>
                    How far back failed attempts are counted ({WINDOW_MIN} to {WINDOW_MAX}).
                  </>
                }
                onChange={(windowMinutes) => update({ windowMinutes })}
              />
            )}
            {draft.kind === "blocked_session" && (
              <>
                <NumberField
                  id="alert-rule-blocked-minutes"
                  label="Blocked for (minutes)"
                  value={draft.blockedMinutes}
                  min={BLOCKED_MIN}
                  max={BLOCKED_MAX}
                  error={errors.blockedMinutes}
                  sharedErrorId={errors.blocked ? "alert-rule-blocked-error" : undefined}
                  hint={
                    <>
                      Optional. Fire when the oldest blocking event has not changed for this long ({BLOCKED_MIN} to{" "}
                      {BLOCKED_MAX}).
                    </>
                  }
                  onChange={(blockedMinutes) => update({ blockedMinutes })}
                />
                <NumberField
                  id="alert-rule-deferred"
                  label="Or deferred events at least"
                  value={draft.deferredThreshold}
                  min={DEFERRED_MIN}
                  max={DEFERRED_MAX}
                  error={errors.deferredThreshold}
                  sharedErrorId={errors.blocked ? "alert-rule-blocked-error" : undefined}
                  hint={
                    <>
                      Optional. Fire when at least this many events wait behind failures ({DEFERRED_MIN} to{" "}
                      {DEFERRED_MAX}).
                    </>
                  }
                  onChange={(deferredThreshold) => update({ deferredThreshold })}
                />
              </>
            )}
            {draft.kind === "silence" && (
              <NumberField
                id="alert-rule-silence"
                label="Silent for (minutes)"
                value={draft.silenceMinutes}
                min={SILENCE_MIN}
                max={SILENCE_MAX}
                error={errors.silenceMinutes}
                hint={
                  <>
                    Fire when no message has completed for this long ({SILENCE_MIN} to {SILENCE_MAX}).
                  </>
                }
                onChange={(silenceMinutes) => update({ silenceMinutes })}
              />
            )}
            {resolveField}
          </div>
          {draft.kind === "blocked_session" && <FieldError id="alert-rule-blocked-error" text={errors.blocked} />}

          {draft.kind === "failure_backlog" && (
            <fieldset className="m-0 border-0 p-0">
              <legend className="mb-2 text-sm font-medium text-foreground">Statuses</legend>
              <div className="flex flex-wrap gap-4">
                {FAILURE_STATUSES.map((status) => (
                  <Checkbox
                    key={status}
                    id={`alert-rule-status-${status}`}
                    label={status}
                    checked={draft.statuses.includes(status)}
                    onChange={(e) => toggleStatus(status, e.currentTarget.checked)}
                  />
                ))}
              </div>
              <Hint>None picked counts all three.</Hint>
            </fieldset>
          )}

          {counted && (
            <>
              <div>
                <p className="m-0 mb-1 text-sm font-medium text-foreground">Event types</p>
                <Combobox
                  options={eventTypeOptions}
                  value={draft.eventTypes}
                  onChange={(eventTypes) => update({ eventTypes })}
                  placeholder="Type an event"
                />
                <FieldError id="alert-rule-event-types-error" text={errors.eventTypes} />
                <Hint>Leave empty to count every event type on this endpoint.</Hint>
              </div>

              <div>
                <Label htmlFor="alert-rule-error-text">Error text contains</Label>
                <Input
                  id="alert-rule-error-text"
                  className="mt-1"
                  value={draft.errorTextContains}
                  maxLength={ERROR_TEXT_MAX}
                  placeholder="timeout"
                  error={!!errors.errorTextContains}
                  onChange={(e) => update({ errorTextContains: e.currentTarget.value })}
                />
                <FieldError id="alert-rule-error-text-error" text={errors.errorTextContains} />
                <Hint>Optional. Counts only failures whose error text contains this, ignoring case.</Hint>
              </div>

              {categoryOptions.length > 0 && (
                <fieldset className="m-0 border-0 p-0" aria-describedby="alert-rule-categories-help">
                  <legend className="mb-2 text-sm font-medium text-foreground">Categories</legend>
                  <div className="grid gap-x-4 gap-y-2 sm:grid-cols-2">
                    {categoryOptions.map((category) => {
                      const checked = draft.categories.includes(category);
                      return (
                        <Checkbox
                          key={category}
                          id={`alert-rule-category-${category}`}
                          label={categoryLabel(category)}
                          checked={checked}
                          disabled={!checked && draft.categories.length >= CATEGORIES_MAX}
                          onChange={(e) => toggleCategory(category, e.currentTarget.checked)}
                        />
                      );
                    })}
                  </div>
                  <FieldError id="alert-rule-categories-error" text={errors.categories} />
                  <p id="alert-rule-categories-help" className="m-0 mt-1 text-xs text-muted-foreground">
                    Counts only failures an agent has classified into one of these. A retry is a new, unclassified
                    attempt.
                  </p>
                  <Hint>None picked counts every failure. Up to {CATEGORIES_MAX}.</Hint>
                  {!classificationAvailable && (
                    <p className="m-0 mt-1 text-xs text-status-warning-ink">
                      This WebApp can't read failure classifications now, so a rule with categories can't be saved.
                      Untick them to save the rule.
                    </p>
                  )}
                </fieldset>
              )}
            </>
          )}

          <AlertRulePreviewPanel endpointId={endpointId} draft={draft} />

          <div className="flex items-center gap-3">
            <Toggle aria-label="Enabled" checked={draft.enabled} onChange={(enabled) => update({ enabled })} />
            <span className="text-sm text-foreground">Enabled</span>
          </div>

          <fieldset className="m-0 border-0 p-0">
            <legend className="mb-2 text-sm font-medium text-foreground">Targets</legend>
            {draft.targets.length === 0 ? (
              <p className="m-0 text-sm text-muted-foreground">No targets yet. Add a mail address or a webhook.</p>
            ) : (
              <ul className="m-0 list-none space-y-2 p-0">
                {draft.targets.map((target, index) => {
                  const kind = target.type === "mail" ? "Mail address" : "Webhook URL";
                  const errorId = `alert-rule-target-${target.key}-error`;
                  const error = errors[`target:${target.key}`];
                  return (
                    <li key={target.key}>
                      <div className="flex items-center gap-2">
                        <span className="w-16 shrink-0 text-xs font-semibold uppercase text-muted-foreground">
                          {target.type}
                        </span>
                        <Input
                          type={target.type === "mail" ? "email" : "url"}
                          autoComplete="off"
                          aria-label={`${kind} ${index + 1}`}
                          placeholder={target.type === "mail" ? "team@example.com" : "https://example.com/hooks/nimbus"}
                          value={target.value}
                          error={!!error}
                          aria-describedby={error ? errorId : undefined}
                          onChange={(e) => setTarget(target.key, e.currentTarget.value)}
                        />
                        <Button
                          type="button"
                          size="sm"
                          variant="ghost"
                          colorScheme="gray"
                          aria-label={`Remove target ${index + 1}`}
                          onClick={() => removeTarget(target.key)}
                        >
                          Remove
                        </Button>
                      </div>
                      <div className="pl-[4.5rem]">
                        <FieldError id={errorId} text={error} />
                      </div>
                    </li>
                  );
                })}
              </ul>
            )}
            <FieldError id="alert-rule-targets-error" text={errors.targets} />
            <div className="mt-2 flex flex-wrap gap-2">
              <Button
                type="button"
                size="sm"
                variant="outline"
                disabled={!mailEnabled || atTargetLimit}
                title={mailEnabled ? undefined : "Alert mail is not configured on this WebApp."}
                onClick={() => addTarget("mail")}
              >
                Add mail
              </Button>
              <Button
                type="button"
                size="sm"
                variant="outline"
                disabled={!webhookEnabled || atTargetLimit}
                title={webhookEnabled ? undefined : "Alert webhooks are not configured on this WebApp."}
                onClick={() => addTarget("webhook")}
              >
                Add webhook
              </Button>
            </div>
            {(!mailEnabled || !webhookEnabled) && (
              <Hint>
                {!mailEnabled && "Alert mail is not configured on this WebApp (NimBus:Alerts:Mail). "}
                {!webhookEnabled &&
                  "Alert webhooks are not configured on this WebApp (NimBus:Alerts:Webhook:SigningKey)."}
              </Hint>
            )}
            <Hint>
              Up to {TARGETS_MAX} targets. A webhook needs an https URL; it receives a signed POST, and its signing
              secret is shown once, after saving.
            </Hint>
          </fieldset>

          {serverError && (
            <p
              role="alert"
              className="m-0 rounded-md border border-status-error-200 bg-status-error-50 px-3 py-2 text-sm text-status-error-ink"
            >
              {serverError}
            </p>
          )}
        </form>
      </ModalBody>
      <ModalFooter>
        <Button type="button" variant="ghost" colorScheme="gray" onClick={onClose}>
          Cancel
        </Button>
        <Button type="submit" form={FORM_ID} disabled={saving}>
          {editing ? "Save rule" : "Create rule"}
        </Button>
      </ModalFooter>
    </Modal>
  );
}
