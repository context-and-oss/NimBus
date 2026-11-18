import { useCallback, useEffect, useMemo, useState } from "react";
import { Navigate, useNavigate, useParams, useSearchParams } from "react-router-dom";
import * as api from "api-client";
import { Input } from "components/ui/input";
import {
  Workspace,
  WorkspaceChip,
  WorkspaceSection,
  WorkspaceStat,
  WorkspaceTabs,
  type ChipTone,
} from "components/workspace/workspace";
import SubscriptionManager from "components/admin/subscription-manager";
import TopologyAudit from "components/admin/topology-audit";
import AsyncApiExport from "components/admin/asyncapi-export";
import CosmosContainerManager from "components/admin/cosmos-container-manager";
import { useStorageProvider } from "hooks/app-status";
import { availableTopologyViews, TOPOLOGY_VIEWS } from "models/manage-pages";

/** Above this many topics the chips get a filter box. */
const TOPIC_FILTER_FROM = 8;

const byName = (a: api.ServiceBusTopicOverview, b: api.ServiceBusTopicOverview) =>
  (a.name ?? "").localeCompare(b.name ?? "", undefined, { numeric: true, sensitivity: "base" });

/** What a topic's chip says about it: its status, and whether the platform knows it. */
function topicNote(t: api.ServiceBusTopicOverview): { tone: ChipTone; title: string } {
  const status = t.status ?? "";
  if (status && status !== "Active") return { tone: "warn", title: `${t.name}: ${status}` };
  if (t.isKnownToPlatform === false) return { tone: "warn", title: `${t.name}: not in platform` };
  if (t.isSystemTopic) return { tone: "info", title: `${t.name}: system topic` };
  return { tone: "ok", title: t.name ?? "" };
}

/**
 * The live Service Bus namespace and storage, compared with the declared catalog.
 * Replaces Admin → Topology, Subscriptions and Storage; the AsyncAPI export is a
 * header action. Site Owner only, like every /api/admin/* call it makes.
 *
 * A workspace: the topics are chips, the view's table takes the width, and the
 * selected row shows in the inspector with its actions, at
 * /Topology/:view?topic=:name. Each view tab keeps the topic, so Subscriptions
 * and Catalog drift show the same endpoint.
 */
export default function Topology() {
  const { view } = useParams();
  const [searchParams] = useSearchParams();
  const navigate = useNavigate();
  const storageProvider = useStorageProvider();
  const client = useMemo(() => new api.Client(api.CookieAuth()), []);
  const [topics, setTopics] = useState<api.ServiceBusTopicOverview[]>();
  const [filter, setFilter] = useState("");
  // Endpoints drifting from the catalog; undefined until all have been audited.
  const [drift, setDrift] = useState<number>();

  const views = availableTopologyViews(storageProvider);
  const requested = (view ?? TOPOLOGY_VIEWS[0].id).toLowerCase();
  const index = views.findIndex((v) => v.id === requested);
  const topic = searchParams.get("topic") || null;

  // The chips' own load, for the Catalog drift and Storage views; Subscriptions
  // keeps it fresh through onTopicsLoaded. Fail soft: the chip bar says so.
  useEffect(() => {
    let cancelled = false;
    client
      .getAdminServicebusTopics()
      .then((next) => {
        if (!cancelled && Array.isArray(next)) setTopics(next);
      })
      .catch(() => {
        if (!cancelled) setTopics((was) => was ?? []);
      });
    return () => {
      cancelled = true;
    };
  }, [client]);

  const urlFor = useCallback(
    (viewId: string, topicName: string | null) => {
      const path = viewId === TOPOLOGY_VIEWS[0].id && !view ? "/Topology" : `/Topology/${viewId}`;
      return topicName ? `${path}?topic=${encodeURIComponent(topicName)}` : path;
    },
    [view],
  );

  const currentView = index >= 0 ? views[index].id : TOPOLOGY_VIEWS[0].id;
  const selectTopic = useCallback(
    (next: string | null) => navigate(urlFor(currentView, next)),
    [navigate, urlFor, currentView],
  );

  if (index < 0) {
    // A Storage deep link can't be judged until the provider has loaded.
    const waitForProvider = requested === "storage" && storageProvider === undefined;
    if (!waitForProvider) return <Navigate to="/Topology" replace />;
  }

  const sorted = [...(topics ?? [])].sort(byName);
  const query = filter.trim().toLowerCase();
  const visible = query ? sorted.filter((t) => (t.name ?? "").toLowerCase().includes(query)) : sorted;
  const subscriptions = sorted.reduce((total, t) => total + (t.subscriptionCount ?? 0), 0);

  return (
    <Workspace
      title="Topology"
      subtitle="The live Service Bus namespace and storage, compared with the declared catalog."
      chipsLabel="Topic"
      chips={
        <>
          <WorkspaceChip selected={topic === null} onSelect={() => selectTopic(null)}>
            All topics
          </WorkspaceChip>
          {visible.map((t) => {
            const name = t.name ?? "";
            const note = topicNote(t);
            return (
              <WorkspaceChip
                key={name}
                mono
                tone={note.tone}
                title={note.title}
                selected={name === topic}
                onSelect={() => selectTopic(name)}
              >
                {name}
              </WorkspaceChip>
            );
          })}
          {topics === undefined && <span className="text-[12.5px] text-muted-foreground">Loading topics…</span>}
          {topics !== undefined && visible.length === 0 && (
            <span className="text-[12.5px] text-muted-foreground">
              {sorted.length === 0 ? "No topics found." : "No topics match."}
            </span>
          )}
          {sorted.length > TOPIC_FILTER_FROM && (
            <Input
              type="search"
              aria-label="Filter topics"
              placeholder="Filter topics"
              value={filter}
              onChange={(e) => setFilter(e.target.value)}
              className="ml-auto h-8 w-44"
            />
          )}
        </>
      }
      stats={
        <>
          <WorkspaceStat
            label="Topics"
            value={topics ? topics.length : "—"}
            onClick={() => navigate(urlFor(TOPOLOGY_VIEWS[0].id, null))}
          />
          <WorkspaceStat
            label="Subscriptions"
            value={topics ? subscriptions : "—"}
            onClick={() => navigate(urlFor(TOPOLOGY_VIEWS[0].id, topic))}
          />
          <WorkspaceStat
            label="Drift"
            value={drift ?? "—"}
            tone={drift === undefined ? "default" : drift > 0 ? "warn" : "ok"}
            title={
              drift === undefined
                ? "Audit all endpoints under Catalog drift to count the ones that drift"
                : `${drift} endpoint(s) drift from the catalog`
            }
            onClick={() => navigate(urlFor("drift", topic))}
          />
        </>
      }
      actions={<AsyncApiExport />}
      tabs={
        index >= 0 && (
          <WorkspaceTabs
            label="Topology views"
            tabs={views}
            active={currentView}
            onChange={(id) => navigate(urlFor(id, topic))}
          />
        )
      }
      inspectorSlot={index >= 0 && currentView !== "storage"}
    >
      {index >= 0 && currentView === "subscriptions" && (
        <SubscriptionManager topic={topic} onTopicChange={selectTopic} onTopicsLoaded={setTopics} />
      )}
      {index >= 0 && currentView === "drift" && (
        <WorkspaceSection
          title="Catalog drift"
          intro={
            <>
              {topic
                ? "This endpoint's live Service Bus subscriptions and rules, compared with the declared catalog."
                : "Compare every endpoint's live Service Bus subscriptions and rules with the declared catalog, or pick a topic above."}{" "}
              Removing deprecated items changes the namespace and requires typed confirmation.
            </>
          }
        >
          <TopologyAudit endpoint={topic} onEndpointChange={selectTopic} onDriftCount={setDrift} />
        </WorkspaceSection>
      )}
      {index >= 0 && currentView === "storage" && <CosmosContainerManager />}
    </Workspace>
  );
}
