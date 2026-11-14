import { useState } from "react";
import * as api from "api-client";
import {
  Button,
  DropdownItem,
  DropdownMenu,
  Modal,
  ModalBody,
  ModalFooter,
  ModalHeader,
  Toggle,
  useToast,
} from "components/ui";
import { isOwnerRole, useAccess } from "hooks/use-access";
import EndpointAlertsModal from "./endpoint-alerts-modal";
import EndpointAccessModal from "./endpoint-access-modal";
import {
  BellIcon,
  MoreHorizontalIcon,
  PowerIcon,
  ShieldCheckIcon,
} from "./icons";

interface IEndpointRowActionsProps {
  endpointId: string;
  /** "active" | "disabled" | "not-found" — as returned with the status counts. */
  subscriptionStatus?: string;
  failed: number;
  deferred: number;
  pending: number;
  storageAvailable: boolean;
  refreshEndpoint: (endpointId: string) => unknown;
  startLoading: () => void;
  stopLoading: () => void;
}

// Per-row endpoint controls: an enable/disable switch plus the overflow menu
// (alerts, access). Every action here is Owner-gated server side, so the
// row only offers what the current user may actually do.
export default function EndpointRowActions(props: IEndpointRowActionsProps) {
  const [client] = useState(() => new api.Client(api.CookieAuth()));
  const { addToast } = useToast();
  const { access } = useAccess();

  const [disableOpen, setDisableOpen] = useState(false);
  const [alertsOpen, setAlertsOpen] = useState(false);
  const [accessOpen, setAccessOpen] = useState(false);

  const { endpointId, subscriptionStatus, storageAvailable } = props;
  const subscriptionMissing = subscriptionStatus === "not-found";
  const enabled = subscriptionStatus !== "disabled" && !subscriptionMissing;

  // PostEndpointSubscriptionstatus / Subscribe both require Owner on the
  // endpoint (site Owners hold it implicitly), so mirror that rule rather than
  // offering controls that would come back 403.
  const isSiteOwner = isOwnerRole(access?.siteRole);
  // Endpoint ids are matched case-insensitively everywhere server side (the ACL
  // snapshot is an OrdinalIgnoreCase dictionary, as is the platform lookup), so
  // compare the same way — otherwise a grant stored as "billing" authorizes the
  // request but hides every control for endpoint "Billing".
  const canManage =
    isSiteOwner ||
    (access?.endpointRoles ?? []).some(
      (role) =>
        role.endpointId?.toLowerCase() === endpointId.toLowerCase() &&
        isOwnerRole(role.role),
    );

  const fmt = (n: number) => (storageAvailable ? n.toLocaleString() : "—");

  // The mutation has already landed by the time this runs, so a failed read-back
  // is only a stale row — but it must still clear the spinner. Left unawaited the
  // rejection escapes the chain, stopLoading never runs, and the table sits in its
  // loading state for good.
  const refreshRow = async () => {
    try {
      await Promise.resolve(props.refreshEndpoint(endpointId));
    } catch {
      props.stopLoading();
      addToast({
        variant: "error",
        title: `${endpointId} was updated, but the list could not be refreshed.`,
        description: "Reload the page to see its current state.",
        duration: 6000,
      });
    }
  };

  const setSubscriptionStatus = (action: "enable" | "disable") => {
    props.startLoading();
    return client
      .postEndpointSubscriptionstatus(endpointId, action)
      .then(async () => {
        await refreshRow();
        return true;
      })
      .catch(() => {
        props.stopLoading();
        addToast({
          variant: "error",
          title: `Could not ${action} ${endpointId}.`,
          duration: 6000,
        });
        return false;
      });
  };

  const enableEndpoint = (title: string) =>
    setSubscriptionStatus("enable").then((ok) => {
      if (ok) {
        addToast({ variant: "success", title, duration: 3000 });
      }
    });

  const handleToggle = (next: boolean) => {
    if (next) {
      // Enabling — no friction, just turn it back on.
      void enableEndpoint(`${endpointId} enabled.`);
    } else {
      setDisableOpen(true);
    }
  };

  const confirmDisable = () => {
    setDisableOpen(false);
    void setSubscriptionStatus("disable").then((ok) => {
      if (!ok) return;
      addToast({
        variant: "warning",
        title: `${endpointId} disabled — it has stopped processing.`,
        duration: 6000,
        action: {
          label: "Undo",
          onClick: () => {
            void enableEndpoint(`${endpointId} re-enabled.`);
          },
        },
      });
    });
  };


  const impactRow = (label: string, value: string, danger = false) => (
    <div className="flex justify-between font-mono text-[11.5px]">
      <span className="text-muted-foreground">{label}</span>
      <b className={danger ? "font-semibold text-status-danger" : "font-semibold"}>
        {value}
      </b>
    </div>
  );

  return (
    <span
      className="inline-flex items-center justify-end gap-2.5"
      onClick={(e) => e.stopPropagation()}
    >
      <Toggle
        checked={enabled}
        disabled={!canManage || subscriptionMissing}
        onChange={handleToggle}
        aria-label={`${enabled ? "Disable" : "Enable"} ${endpointId}`}
      />

      {/* Every item here is Owner-only, so a reader gets no menu at all rather
          than an empty one. Opening the endpoint isn't offered — the row itself
          is the link. */}
      {canManage && (
        <DropdownMenu
          trigger={<MoreHorizontalIcon className="h-4 w-4" />}
          triggerLabel={`More actions for ${endpointId}`}
        >
          <DropdownItem icon={<BellIcon />} onSelect={() => setAlertsOpen(true)}>
            Configure alerts…
          </DropdownItem>
          <DropdownItem
            icon={<ShieldCheckIcon />}
            onSelect={() => setAccessOpen(true)}
          >
            Manage access…
          </DropdownItem>
        </DropdownMenu>
      )}

      {/* Disable confirmation */}
      <Modal isOpen={disableOpen} onClose={() => setDisableOpen(false)}>
        <ModalHeader onClose={() => setDisableOpen(false)}>
          <span className="inline-flex items-center gap-2">
            <span className="inline-flex h-7 w-7 items-center justify-center rounded-nb-sm bg-status-warning-50 text-status-warning">
              <PowerIcon className="h-4 w-4" />
            </span>
            Disable {endpointId}?
          </span>
        </ModalHeader>
        <ModalBody>
          <p className="text-sm text-muted-foreground m-0">
            The subscription goes to{" "}
            <span className="font-mono">ReceiveDisabled</span> and the endpoint
            stops processing. Pending messages stay queued; configuration is
            preserved.
          </p>
          <div className="mt-3 flex flex-col gap-1 rounded-nb-md border border-border bg-background px-3 py-2.5">
            {impactRow("Pending messages", fmt(props.pending))}
            {impactRow("Deferred messages", fmt(props.deferred))}
            {impactRow("Failed messages", fmt(props.failed))}
          </div>
        </ModalBody>
        <ModalFooter>
          <Button
            variant="ghost"
            colorScheme="gray"
            onClick={() => setDisableOpen(false)}
          >
            Cancel
          </Button>
          <Button colorScheme="primary" onClick={confirmDisable}>
            Disable endpoint
          </Button>
        </ModalFooter>
      </Modal>

      {canManage && (
        <EndpointAlertsModal
          endpointId={endpointId}
          isOpen={alertsOpen}
          onClose={() => setAlertsOpen(false)}
        />
      )}

      {canManage && (
        <EndpointAccessModal
          endpointId={endpointId}
          isOpen={accessOpen}
          onClose={() => setAccessOpen(false)}
        />
      )}
    </span>
  );
}
