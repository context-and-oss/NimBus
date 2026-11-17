import { useCallback, useEffect, useMemo, useState, type ReactNode } from "react";
import { Link, Navigate, useNavigate, useParams, useSearchParams } from "react-router-dom";
import * as api from "api-client";
import Page from "components/page";
import { Badge } from "components/ui/badge";
import { Input } from "components/ui/input";
import { Tabs, TabList, Tab, TabPanels, TabPanel } from "components/ui/tabs";
import SubscriptionManager from "components/admin/subscription-manager";
import TopologyAudit from "components/admin/topology-audit";
import AsyncApiExport from "components/admin/asyncapi-export";
import CosmosContainerManager from "components/admin/cosmos-container-manager";
import { useStorageProvider } from "hooks/app-status";
import { availableTopologyViews, TOPOLOGY_VIEWS } from "models/manage-pages";
import { cn } from "lib/utils";

/** Above this many topics the list gets a filter box. */
const TOPIC_FILTER_FROM = 8;

const byName = (a: api.ServiceBusTopicOverview, b: api.ServiceBusTopicOverview) =>
  (a.name ?? "").localeCompare(b.name ?? "", undefined, { numeric: true, sensitivity: "base" });

/**
 * The live Service Bus namespace and storage, compared with the declared catalog.
 * Replaces Admin → Topology, Subscriptions and Storage; the AsyncAPI export is a
 * header action. Site Owner only, like every /api/admin/* call it makes.
 *
 * A split view: the topics stay listed on the left and the chosen one, or all
 * of them, fills the right, at /Topology/:view?topic=:name. Each view tab keeps
 * the topic, so Subscriptions and Catalog drift show the same endpoint.
 */
export default function Topology() {
  const { view } = useParams();
  const [searchParams] = useSearchParams();
  const navigate = useNavigate();
  const storageProvider = useStorageProvider();
  const client = useMemo(() => new api.Client(api.CookieAuth()), []);
  const [topics, setTopics] = useState<api.ServiceBusTopicOverview[]>();
  const [filter, setFilter] = useState("");

  const views = availableTopologyViews(storageProvider);
  const requested = (view ?? TOPOLOGY_VIEWS[0].id).toLowerCase();
  const index = views.findIndex((v) => v.id === requested);
  const topic = searchParams.get("topic") || null;

  // The list's own load, for the Catalog drift and Storage views; Subscriptions
  // keeps it fresh through onTopicsLoaded. Fail soft: the list says so.
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
  const active = sorted.find((t) => t.name === topic);

  const topicItem = (key: string, name: string, current: boolean, to: string, badges?: ReactNode, mono = true) => (
    <li key={key}>
      <Link
        to={to}
        aria-current={current ? "page" : undefined}
        className={cn(
          "block rounded-nb-md border px-3 py-2 text-foreground no-underline hover:no-underline",
          current ? "border-border-strong bg-card" : "border-transparent hover:bg-muted/60",
        )}
      >
        <span className={cn("block truncate text-[13px] font-semibold", mono && "font-mono")} title={name}>
          {name}
        </span>
        {badges && <span className="mt-1 flex flex-wrap gap-1">{badges}</span>}
      </Link>
    </li>
  );

  return (
    <Page
      title="Topology"
      subtitle="The live Service Bus namespace and storage, compared with the declared catalog."
      actions={<AsyncApiExport />}
    >
      <div className="grid w-full min-w-0 items-start gap-5 lg:grid-cols-[17rem_minmax(0,1fr)]">
        <nav aria-label="Topics" className="flex min-w-0 flex-col gap-4">
          <ul className="m-0 list-none p-0">
            {topicItem("all", "All topics", topic === null, urlFor(currentView, null), undefined, false)}
          </ul>
          <div>
            <h2 className="m-0 mb-1.5 ml-2 font-mono text-[10.5px] font-medium uppercase tracking-[0.14em] text-muted-foreground">
              Topics {topics && <span className="normal-case tracking-normal">· {topics.length}</span>}
            </h2>
            {sorted.length > TOPIC_FILTER_FROM && (
              <Input
                type="search"
                aria-label="Filter topics"
                placeholder="Filter topics"
                value={filter}
                onChange={(e) => setFilter(e.target.value)}
                className="mb-2"
              />
            )}
            <ul className="m-0 list-none space-y-0.5 p-0">
              {topics === undefined && <li className="px-3 py-2 text-[12.5px] text-muted-foreground">Loading topics…</li>}
              {topics !== undefined && visible.length === 0 && (
                <li className="px-3 py-2 text-[12.5px] text-muted-foreground">
                  {sorted.length === 0 ? "No topics found." : "No topics match."}
                </li>
              )}
              {visible.map((t) => {
                const name = t.name ?? "";
                const status = t.status ?? "";
                const badges = (
                  <>
                    {status && status !== "Active" && (
                      <Badge variant="warning" size="sm">
                        {status}
                      </Badge>
                    )}
                    {t.isSystemTopic && (
                      <Badge variant="info" size="sm">
                        system
                      </Badge>
                    )}
                    {t.isKnownToPlatform === false && (
                      <Badge variant="secondary" size="sm">
                        not in platform
                      </Badge>
                    )}
                  </>
                );
                const hasBadges = (status && status !== "Active") || t.isSystemTopic || t.isKnownToPlatform === false;
                return topicItem(name, name, name === topic, urlFor(currentView, name), hasBadges ? badges : undefined);
              })}
            </ul>
          </div>
        </nav>

        <section aria-label={topic ?? "All topics"} className="min-w-0 space-y-3">
          <div className="flex flex-wrap items-center gap-2.5">
            <h2 className={cn("m-0 text-[19px] font-bold", topic && "font-mono")}>{topic ?? "All topics"}</h2>
            {active?.isSystemTopic && <Badge variant="info">system topic</Badge>}
            {active?.status && active.status !== "Active" && <Badge variant="warning">{active.status}</Badge>}
          </div>
          {index >= 0 && (
            <Tabs
              index={index}
              onTabChange={(i) => navigate(urlFor(views[i].id, topic))}
              isLazy={true}
              className="w-full"
            >
              <TabList>
                {views.map((v, i) => (
                  <Tab key={v.id} index={i}>
                    {v.label}
                  </Tab>
                ))}
              </TabList>
              <TabPanels>
                {views.map((v, i) => (
                  <TabPanel key={v.id} index={i} className="p-6">
                    {v.id === "subscriptions" && (
                      <SubscriptionManager topic={topic} onTopicChange={selectTopic} onTopicsLoaded={setTopics} />
                    )}
                    {v.id === "drift" && (
                      <div className="w-full space-y-4">
                        <p className="m-0 text-[13px] text-muted-foreground">
                          {topic
                            ? "Compare this endpoint's live Service Bus subscriptions and rules against the declared catalog."
                            : "Compare every endpoint's live Service Bus subscriptions and rules against the declared catalog, or pick a topic on the left."}{" "}
                          Removing deprecated items changes the namespace and requires typed confirmation.
                        </p>
                        <TopologyAudit endpoint={topic} onEndpointChange={selectTopic} />
                      </div>
                    )}
                    {v.id === "storage" && <CosmosContainerManager />}
                  </TabPanel>
                ))}
              </TabPanels>
            </Tabs>
          )}
        </section>
      </div>
    </Page>
  );
}
