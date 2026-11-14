import { Navigate, useNavigate, useParams } from "react-router-dom";
import Page from "components/page";
import { Tabs, TabList, Tab, TabPanels, TabPanel } from "components/ui/tabs";
import SubscriptionManager from "components/admin/subscription-manager";
import TopologyAudit from "components/admin/topology-audit";
import AsyncApiExport from "components/admin/asyncapi-export";
import CosmosContainerManager from "components/admin/cosmos-container-manager";
import { useStorageProvider } from "hooks/app-status";
import { availableTopologyViews, TOPOLOGY_VIEWS } from "models/manage-pages";

/**
 * The live Service Bus namespace and storage, compared with the declared catalog.
 * Replaces Admin → Topology, Subscriptions and Storage; the AsyncAPI export is a
 * header action. Site Owner only, like every /api/admin/* call it makes.
 */
export default function Topology() {
  const { view } = useParams();
  const navigate = useNavigate();
  const storageProvider = useStorageProvider();
  const views = availableTopologyViews(storageProvider);
  const requested = (view ?? TOPOLOGY_VIEWS[0].id).toLowerCase();
  const index = views.findIndex((v) => v.id === requested);

  if (index < 0) {
    // A Storage deep link can't be judged until the provider has loaded.
    const waitForProvider = requested === "storage" && storageProvider === undefined;
    if (!waitForProvider) return <Navigate to="/Topology" replace />;
  }

  return (
    <Page
      title="Topology"
      subtitle="The live Service Bus namespace and storage, compared with the declared catalog."
      actions={<AsyncApiExport />}
    >
      {index >= 0 && (
        <Tabs
          index={index}
          onTabChange={(i) => navigate(`/Topology/${views[i].id}`)}
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
                {v.id === "subscriptions" && <SubscriptionManager />}
                {v.id === "drift" && (
                  <div className="w-full space-y-4">
                    <p className="m-0 text-[13px] text-muted-foreground">
                      Compare an endpoint's live Service Bus subscriptions and rules
                      against the declared catalog. Removing deprecated items changes
                      the namespace and requires typed confirmation.
                    </p>
                    <TopologyAudit />
                  </div>
                )}
                {v.id === "storage" && <CosmosContainerManager />}
              </TabPanel>
            ))}
          </TabPanels>
        </Tabs>
      )}
    </Page>
  );
}
