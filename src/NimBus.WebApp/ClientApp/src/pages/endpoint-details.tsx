import * as React from "react";
const { useEffect, useMemo, useState } = React;

import * as api from "api-client";
import { Link, useParams, useSearchParams } from "react-router-dom";
import Page from "components/page";
import EventsPanel from "components/endpoint-details/events-panel";
import EventTypesPanel from "components/endpoint-details/event-types-panel";
import SubscriptionsTab from "components/endpoint-details/tabs/subscriptions-tab";
import AlertRulesSection from "components/endpoint-details/alert-rules/alert-rules-section";
import { WorkspaceSection } from "components/workspace/workspace";
import AuditTab from "components/endpoint-details/tabs/audit-tab";
import MetadataTab from "components/endpoint-details/tabs/metadata-tab";
import NotFoundPage from "components/not-found-page";
import { cn } from "lib/utils";

export interface ComposeNewResponse {
  hasError: boolean;
  responseString: string;
}

type EndpointDetailsProps = {
  endpointState?: api.EndpointStatus;
};

const DETAIL_TABS = ["messages", "event-types", "alerts", "ownership", "audit"] as const;
type DetailTab = (typeof DETAIL_TABS)[number];

/** The tab a `?tab=` value names; Messages for none or one the page doesn't have. */
const tabFromParam = (value: string | null): DetailTab =>
  (DETAIL_TABS as readonly string[]).includes(value ?? "") ? (value as DetailTab) : "messages";

const EndpointDetails = (props: EndpointDetailsProps) => {
  const { id } = useParams();
  return <EndpointDetailsContent key={id} {...props} endpointId={id!} />;
};

// Scope panel state and pending lookups to one endpoint, including route changes.
const EndpointDetailsContent = (
  props: EndpointDetailsProps & { endpointId: string },
) => {
  const { endpointId } = props;
  const [client] = useState(() => new api.Client(api.CookieAuth()));

  const [endpointIsInvalid, setEndpointIsInvalid] = useState<boolean>(false);
  // The open tab lives in the URL (`?tab=alerts`), so notices and links can open a tab directly.
  const [searchParams, setSearchParams] = useSearchParams();
  const activeTab = tabFromParam(searchParams.get("tab"));
  const [visitedTabs, setVisitedTabs] = useState<Set<DetailTab>>(
    () => new Set(["messages", activeTab]),
  );
  // A tab opened from the URL counts as visited, so it stays mounted after the user moves on.
  if (!visitedTabs.has(activeTab)) {
    setVisitedTabs(new Set([...visitedTabs, activeTab]));
  }
  const [alertSubscriptions, setAlertSubscriptions] = useState<
    api.EndpointSubscription[]
  >([]);
  const [alertProbe, setAlertProbe] = useState<"loading" | "loaded" | "failed">(
    "loading",
  );
  const [hasSubscriptions, setHasSubscriptions] = useState(false);

  // Load the subscriptions without mounting the Alerts panel. Reuse these rows
  // when it is first opened so opening it does not cause a second request.
  useEffect(() => {
    let active = true;
    client
      .getEndpointSubscribe(endpointId)
      .then((subscriptions) => {
        if (!active) return;
        setAlertProbe("loaded");
        setAlertSubscriptions(subscriptions);
        setHasSubscriptions(subscriptions.length > 0);
      })
      .catch(() => {
        if (active) setAlertProbe("failed");
      });
    return () => {
      active = false;
    };
  }, [client, endpointId]);

  // Replace, not push: switching tabs shouldn't fill the Back button's history.
  const selectTab = (tab: DetailTab) => {
    setVisitedTabs((visited) =>
      visited.has(tab) ? visited : new Set([...visited, tab]),
    );
    setSearchParams(
      (params) => {
        const next = new URLSearchParams(params);
        if (tab === "messages") next.delete("tab");
        else next.set("tab", tab);
        return next;
      },
      { replace: true },
    );
  };

  // Validate endpoint exists
  useEffect(() => {
    let active = true;
    const fetchData = async () => {
      try {
        if (!props.endpointState) {
          await client.getApiEndpointstatusStatusEndpointName(endpointId);
        }
      } catch (e) {
        console.log("Failed to load endpoint details");
        if (active && e instanceof api.SwaggerException) {
          if (e.status === 404) {
            setEndpointIsInvalid(true);
          }
        }
      }
    };

    void fetchData();
    return () => {
      active = false;
    };
  }, [client, endpointId, props.endpointState]);

  const tabs = useMemo<{ id: DetailTab; label: string }[]>(
    () => [
      { id: "messages", label: "Messages" },
      { id: "event-types", label: "Event Types" },
      { id: "alerts", label: "Alerts" },
      { id: "ownership", label: "Ownership" },
      { id: "audit", label: "Audit" },
    ],
    [],
  );

  if (endpointIsInvalid) {
    return (
      <NotFoundPage errMsg={"No endpoint found with name: " + endpointId} />
    );
  }

  return (
    <Page title={endpointId} subtitle="Endpoint details">
      <div className="flex flex-col gap-4 w-full">
        <TabStrip tabs={tabs} activeTab={activeTab} onChange={selectTab} />
        {/* Mount on first visit; retain visited panels to preserve filters and pages. */}
        <div className={activeTab === "messages" ? "" : "hidden"}>
          <EventsPanel endpointId={endpointId} />
        </div>
        {visitedTabs.has("event-types") && (
          <div className={activeTab === "event-types" ? "" : "hidden"}>
            <EventTypesPanel endpointId={endpointId} />
          </div>
        )}
        {visitedTabs.has("alerts") && (
          <div
            className={cn(
              "flex flex-col gap-8",
              activeTab === "alerts" ? "" : "hidden",
            )}
          >
            <AlertRulesSection endpointId={endpointId} />
            <WorkspaceSection
              title="Every failure"
              intro="Subscriptions get a notice about each failed or dead-lettered event, no more often than their frequency."
            >
              {hasSubscriptions ? (
                <SubscriptionsTab
                  initialSubscriptions={alertSubscriptions}
                  setIsTabEnabled={setHasSubscriptions}
                />
              ) : alertProbe === "loading" ? (
                <p role="status" className="m-0 text-sm text-muted-foreground">
                  Loading alert subscriptions…
                </p>
              ) : (
                <p id="alerts-help" className="m-0 text-sm text-muted-foreground">
                  {alertProbe === "failed"
                    ? "Alert subscriptions could not be loaded."
                    : "No alert subscriptions are configured for this endpoint."}{" "}
                  Configure alerts from this endpoint's menu on the{" "}
                  <Link to="/Endpoints" className="text-primary hover:underline">
                    endpoint list
                  </Link>
                  .
                </p>
              )}
            </WorkspaceSection>
          </div>
        )}
        {visitedTabs.has("ownership") && (
          <div className={activeTab === "ownership" ? "" : "hidden"}>
            <MetadataTab />
          </div>
        )}
        {visitedTabs.has("audit") && (
          <div className={activeTab === "audit" ? "" : "hidden"}>
            <AuditTab endpointId={endpointId} />
          </div>
        )}
      </div>
    </Page>
  );
};

interface TabStripProps {
  tabs: { id: DetailTab; label: string }[];
  activeTab: DetailTab;
  onChange: (tab: DetailTab) => void;
}

const TabStrip: React.FC<TabStripProps> = ({ tabs, activeTab, onChange }) => (
  <div role="tablist" className="flex gap-1 border-b border-border -mb-1">
    {tabs.map((t) => {
      const isActive = activeTab === t.id;
      return (
        <button
          key={t.id}
          role="tab"
          type="button"
          aria-selected={isActive}
          onClick={() => onChange(t.id)}
          className={cn(
            "bg-transparent border-0 px-[18px] py-[11px] text-[13.5px] font-semibold cursor-pointer",
            "border-b-2 border-transparent -mb-px inline-flex items-center gap-2",
            "transition-colors focus:outline-hidden focus:ring-2 focus:ring-primary focus:ring-inset",
            isActive
              ? "text-primary-600 border-b-primary bg-linear-to-b from-transparent to-primary-tint"
              : "text-ink-2 hover:text-ink",
          )}
        >
          {t.label}
        </button>
      );
    })}
  </div>
);

export default EndpointDetails;
