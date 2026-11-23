import { useCallback, useEffect, useRef, useState } from "react";
import moment from "moment";
import * as api from "api-client";
import { errorMessage } from "api-client/problem";
import { Badge, Button, Modal, ModalBody, ModalFooter, ModalHeader, useToast } from "components/ui";
import {
  RowActionsCell,
  RowActionsHeader,
  WORKSPACE_HEAD_ROW,
  WORKSPACE_TH,
  WorkspaceTableFrame,
  type RowAction,
} from "components/workspace/selectable-row";
import { WorkspaceSection } from "components/workspace/workspace";
import AlertRuleFormModal from "./alert-rule-form-modal";
import {
  availableKindsOf,
  describeCondition,
  evaluatorFreshness,
  lastValueText,
  requestFromRule,
  resolveText,
  ruleStatus,
  targetLabel,
  targetsSummary,
  valueLabel,
} from "./alert-rules-model";

type Form = { mode: "new" } | { mode: "edit"; rule: api.AlertRuleModel };

/** How often the section re-reads its rules, so a rule's state follows the evaluator's minute tick. */
const REFRESH_MS = 60_000;

function Targets({ targets }: { targets: api.AlertRuleTargetModel[] | undefined }) {
  const labels = (targets ?? []).map((t) => ({ target: t, label: targetLabel(t) }));
  // A Reader sees no address: only the number and kind of targets.
  if (labels.length === 0 || labels.every((l) => l.label === undefined)) {
    return <span className="text-muted-foreground">{targetsSummary(targets)}</span>;
  }
  return (
    <ul className="m-0 list-none space-y-0.5 p-0">
      {labels.map(({ target, label }, i) => (
        <li key={target.id ?? i} className="truncate" title={target.type === "webhook" ? target.url : target.address}>
          <span className="text-muted-foreground">{target.type}</span> {label ?? "–"}
        </li>
      ))}
    </ul>
  );
}

/**
 * The Rules section of an endpoint's Alerts tab (Spec 053): rules that fire on a condition of the
 * endpoint, such as its failure backlog, failures in a time window, a blocked session or silence.
 * Everyone who can read the endpoint sees the rules and their state; Owners also see the targets'
 * addresses and can create, change and delete rules.
 */
export default function AlertRulesSection({ endpointId }: { endpointId: string }) {
  const { addToast } = useToast();
  const [client] = useState(() => new api.Client(api.CookieAuth()));
  const [list, setList] = useState<api.AlertRuleList>();
  const [loadError, setLoadError] = useState<string>();
  const [form, setForm] = useState<Form>();
  const [pendingDelete, setPendingDelete] = useState<api.AlertRuleModel>();
  const [busy, setBusy] = useState(false);
  const [now, setNow] = useState(() => moment());
  const alive = useRef(true);

  const load = useCallback(async () => {
    try {
      const next = await client.getEndpointAlertRules(endpointId);
      if (!alive.current) return;
      setList(next);
      setLoadError(undefined);
      setNow(moment());
    } catch (error) {
      if (alive.current) setLoadError(errorMessage(error) || "Alert rules could not be loaded.");
    }
  }, [client, endpointId]);

  useEffect(() => {
    alive.current = true;
    void load();
    const timer = setInterval(() => void load(), REFRESH_MS);
    return () => {
      alive.current = false;
      clearInterval(timer);
    };
  }, [load]);

  async function setEnabled(rule: api.AlertRuleModel, enabled: boolean) {
    if (!rule.id) return;
    setBusy(true);
    try {
      await client.putEndpointAlertRule(endpointId, rule.id, requestFromRule(rule, { enabled }));
      addToast({ variant: "success", title: `${enabled ? "Enabled" : "Disabled"} "${rule.name}".`, duration: 4000 });
      await load();
    } catch (error) {
      addToast({
        variant: "error",
        title: `Could not ${enabled ? "enable" : "disable"} "${rule.name}".`,
        description: errorMessage(error) || "The request was rejected.",
        duration: 6000,
      });
    } finally {
      if (alive.current) setBusy(false);
    }
  }

  async function confirmDelete() {
    const rule = pendingDelete;
    setPendingDelete(undefined);
    if (!rule?.id) return;
    setBusy(true);
    try {
      await client.deleteEndpointAlertRule(endpointId, rule.id);
      addToast({ variant: "success", title: `Deleted "${rule.name}".`, duration: 4000 });
      await load();
    } catch (error) {
      addToast({
        variant: "error",
        title: `Could not delete "${rule.name}".`,
        description: errorMessage(error) || "The request was rejected.",
        duration: 6000,
      });
    } finally {
      if (alive.current) setBusy(false);
    }
  }

  if (!list) {
    return (
      <WorkspaceSection title="Rules">
        <p role={loadError ? "alert" : "status"} className="m-0 text-sm text-muted-foreground">
          {loadError ?? "Loading alert rules…"}
        </p>
      </WorkspaceSection>
    );
  }

  const rules = list.rules ?? [];
  const canManage = !!list.canManage;
  const freshness = evaluatorFreshness(list.evaluatorLastCompletedAtUtc, now);

  const actions = (rule: api.AlertRuleModel): RowAction[] => [
    { label: "Edit…", disabled: busy, onSelect: () => setForm({ mode: "edit", rule }) },
    rule.enabled
      ? { label: "Disable", disabled: busy, onSelect: () => void setEnabled(rule, false) }
      : { label: "Enable", disabled: busy, onSelect: () => void setEnabled(rule, true) },
    { label: "Delete…", destructive: true, disabled: busy, onSelect: () => setPendingDelete(rule) },
  ];

  return (
    <WorkspaceSection
      title="Rules"
      count={rules.length}
      intro={
        <>
          A rule fires when its condition holds on the endpoint, for example when failed events pile up or a session
          stays blocked, and resolves when the condition has stayed clear for the resolve-after time. Each target
          gets one notice when the rule starts firing and one when it resolves.
        </>
      }
      action={
        canManage ? (
          <Button type="button" size="sm" variant="outline" onClick={() => setForm({ mode: "new" })}>
            New rule
          </Button>
        ) : undefined
      }
    >
      {freshness.kind === "stale" ? (
        <p
          role="status"
          className="m-0 rounded-md bg-status-warning-50 p-2.5 text-[12.5px] text-status-warning-ink"
          title={list.evaluatorLastCompletedAtUtc?.clone().local().format("LLL")}
        >
          {freshness.text}
        </p>
      ) : (
        <p
          className="m-0 text-[12.5px] text-muted-foreground"
          title={list.evaluatorLastCompletedAtUtc?.clone().local().format("LLL")}
        >
          {freshness.text}
        </p>
      )}
      {loadError && (
        <p role="alert" className="m-0 text-[12.5px] text-status-error-ink">
          {loadError}
        </p>
      )}

      <WorkspaceTableFrame>
        {rules.length === 0 ? (
          <div className="px-5 py-8 text-center">
            <p className="m-0 font-medium">No alert rules</p>
            {canManage && (
              <p className="m-0 text-sm text-muted-foreground">
                Create a rule to be told when something goes wrong on this endpoint, and again when it clears.
              </p>
            )}
          </div>
        ) : (
          <table className="w-full min-w-[48rem] border-collapse text-[13px]" aria-label="Alert rules">
            <thead>
              <tr className={WORKSPACE_HEAD_ROW}>
                <th scope="col" className={WORKSPACE_TH}>Name</th>
                <th scope="col" className={WORKSPACE_TH}>Condition</th>
                <th scope="col" className={WORKSPACE_TH}>Status</th>
                <th scope="col" className={WORKSPACE_TH}>Last value</th>
                <th scope="col" className={WORKSPACE_TH}>Targets</th>
                {canManage && <RowActionsHeader />}
              </tr>
            </thead>
            <tbody>
              {rules.map((rule) => {
                const status = ruleStatus(rule, now);
                return (
                  <tr key={rule.id} className="border-b border-border last:border-b-0">
                    <td className="max-w-[14rem] truncate px-4 py-2.5 text-[13.5px] font-semibold" title={rule.name}>
                      {rule.name}
                    </td>
                    <td className="px-4 py-2">
                      {describeCondition(rule.condition)}
                      <div className="text-xs text-muted-foreground">{resolveText(rule)}</div>
                    </td>
                    <td className="px-4 py-2">
                      <Badge variant={status.variant} title={status.title}>
                        {status.label}
                      </Badge>
                    </td>
                    <td className="px-4 py-2 tabular-nums">
                      <span title={valueLabel(rule.condition)}>{lastValueText(rule)}</span>
                    </td>
                    <td className="max-w-[16rem] px-4 py-2">
                      <Targets targets={rule.targets} />
                    </td>
                    {canManage && <RowActionsCell label={`Actions for ${rule.name}`} actions={actions(rule)} />}
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </WorkspaceTableFrame>

      {form && (
        <AlertRuleFormModal
          endpointId={endpointId}
          rule={form.mode === "edit" ? form.rule : undefined}
          mailEnabled={!!list.mailEnabled}
          webhookEnabled={!!list.webhookEnabled}
          availableKinds={availableKindsOf(list.availableKinds)}
          onClose={() => setForm(undefined)}
          onSaved={() => void load()}
        />
      )}

      <Modal isOpen={!!pendingDelete} onClose={() => setPendingDelete(undefined)} label="Delete alert rule">
        <ModalHeader>Delete "{pendingDelete?.name}"?</ModalHeader>
        <ModalBody>
          <p className="m-0 text-sm">
            {pendingDelete?.state?.status === "Firing"
              ? "The rule is firing: its targets get a resolved notice saying the rule was deleted. "
              : ""}
            The rule and its targets' signing secrets are removed. Its audit history stays.
          </p>
        </ModalBody>
        <ModalFooter>
          <Button type="button" variant="outline" onClick={() => setPendingDelete(undefined)}>
            Cancel
          </Button>
          <Button type="button" colorScheme="red" onClick={() => void confirmDelete()}>
            Delete rule
          </Button>
        </ModalFooter>
      </Modal>
    </WorkspaceSection>
  );
}
