import { useCallback, useEffect, useState } from "react";
import * as api from "api-client";
import { Modal, ModalBody, ModalHeader, useToast } from "components/ui";
import { RoleCard } from "components/access-control/role-card";
import { invalidateAccess } from "hooks/use-access";

interface IEndpointAccessModalProps {
  endpointId: string;
  isOpen: boolean;
  onClose: () => void;
}

// Endpoint role lists, in ladder order. The copy mirrors the endpoint section
// of the Access Control page — this modal is the same editor, reachable from
// the row without leaving the list.
const CARDS: Array<{
  role: api.RoleEntryRole;
  title: string;
  description: string;
  tone: string;
  pick: (set: api.AccessControlSet) => string[];
}> = [
  {
    role: api.RoleEntryRole.Reader,
    title: "Readers",
    description: "View this endpoint's events, metrics and audits.",
    tone: "bg-status-info",
    pick: (s) => s.readers ?? [],
  },
  {
    role: api.RoleEntryRole.Contributor,
    title: "Contributors",
    description:
      "Everything Readers can, plus resubmit, skip, handoff and compose.",
    tone: "bg-status-success",
    pick: (s) => s.contributors ?? [],
  },
  {
    role: api.RoleEntryRole.Owner,
    title: "Owners",
    description:
      "Manage this endpoint: enable/disable, purge, subscriptions and these role grants.",
    tone: "bg-status-danger",
    pick: (s) => s.owners ?? [],
  },
];

export default function EndpointAccessModal(props: IEndpointAccessModalProps) {
  const { addToast } = useToast();
  const [client] = useState(() => new api.Client(api.CookieAuth()));
  const [set, setSet] = useState<api.AccessControlSet | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!props.isOpen) return;
    let cancelled = false;
    setSet(null);
    client
      .getEndpointAccessControl(props.endpointId)
      .then((loaded) => {
        if (!cancelled) setSet(loaded);
      })
      .catch(() => {
        if (cancelled) return;
        addToast({
          variant: "error",
          title: `Access control for ${props.endpointId} could not be loaded.`,
          duration: 5000,
        });
        props.onClose();
      });
    return () => {
      cancelled = true;
    };
    // addToast/onClose are stable enough for this one-shot load; re-running on
    // their identity would refetch on every parent render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [props.isOpen, props.endpointId, client]);

  const mutate = useCallback(
    async (fn: () => Promise<api.AccessControlSet>) => {
      setBusy(true);
      try {
        setSet(await fn());
        // The current user may have just changed their own grants — drop the
        // cached /accesscontrol/me answer so the next read reflects it.
        invalidateAccess();
      } catch (e) {
        addToast({
          variant: "error",
          title: "Access-control change failed",
          description: e instanceof Error ? e.message : String(e),
          duration: 6000,
        });
      } finally {
        setBusy(false);
      }
    },
    [addToast],
  );

  return (
    // 2xl (max-w-5xl) — three role cards side by side need the width, or the
    // descriptions wrap to five lines and granted entries truncate.
    <Modal isOpen={props.isOpen} onClose={props.onClose} size="2xl">
      <ModalHeader onClose={props.onClose}>
        Access on {props.endpointId}
      </ModalHeader>
      <ModalBody>
        <p className="text-sm text-muted-foreground m-0 mb-4">
          Storage-backed roles: Reader &lt; Contributor &lt; Owner. Entries are
          email addresses or Entra object ids, or <code>agent:&lt;agentId&gt;</code> for
          the agent of an MCP agent key, as Reader or Contributor. Site-wide grants
          apply here too but are managed on the Access Control page.
        </p>
        <div className="grid gap-4 md:grid-cols-3">
          {CARDS.map((card) => (
            <RoleCard
              key={card.role}
              title={card.title}
              description={card.description}
              tone={card.tone}
              entries={set ? card.pick(set) : []}
              busy={busy || !set}
              onAdd={(entry) =>
                mutate(() =>
                  client.postEndpointAccessControlRole(
                    props.endpointId,
                    new api.RoleEntry({ role: card.role, entry }),
                  ),
                )
              }
              onRemove={(entry) =>
                mutate(() =>
                  client.deleteEndpointAccessControlRole(
                    props.endpointId,
                    new api.RoleEntry({ role: card.role, entry }),
                  ),
                )
              }
            />
          ))}
        </div>
      </ModalBody>
    </Modal>
  );
}
