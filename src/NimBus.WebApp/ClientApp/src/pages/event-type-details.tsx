import { useState, useEffect } from "react";
import { useParams } from "react-router-dom";
import * as api from "api-client";
import Page from "components/page";
import { Spinner } from "components/ui/spinner";
import { NamespacePill } from "components/ui/namespace-pill";
import { TopologyMiniMap } from "components/ui/topology-mini-map";
import EventTypePropertiesTable from "components/event-types/event-type-properties-table";
import EventTypeExamplePayload from "components/event-types/event-type-example-payload";

const EventTypeDetails: React.FC = () => {
  const params = useParams<{ id: string }>();
  const [details, setDetails] = useState<api.EventTypeDetails | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const fetchData = async () => {
      const client = new api.Client(api.CookieAuth());
      try {
        const result = await client.getEventtypesEventtypeid(params.id!);
        setDetails(result);
      } catch (err) {
        setError("Failed to load event type details");
        console.error(err);
      } finally {
        setLoading(false);
      }
    };
    fetchData();
  }, [params.id]);

  if (loading) {
    return (
      <Page title="Event Type Details" backbutton backUrl="/EventTypes">
        <div className="flex items-center justify-center w-full h-[200px]">
          <Spinner size="xl" color="primary" />
        </div>
      </Page>
    );
  }

  if (error || !details) {
    return (
      <Page title="Event Type Details" backbutton backUrl="/EventTypes">
        <div className="flex items-center justify-center w-full h-[200px]">
          <p className="text-status-danger">{error || "Event type not found"}</p>
        </div>
      </Page>
    );
  }

  const eventType = details.eventType;
  const producers = details.producers ?? [];
  const consumers = details.consumers ?? [];
  const fieldCount = (eventType?.properties ?? []).filter(
    (p) => p.name !== "MessageMetadata",
  ).length;

  return (
    <Page
      title={eventType?.name || "Event Type Details"}
      subtitle={eventType?.description || undefined}
      backbutton
      backUrl="/EventTypes"
    >
      <div className="w-full flex flex-col gap-5">
        {eventType?.namespace && (
          <div>
            <NamespacePill>{eventType.namespace}</NamespacePill>
          </div>
        )}

        <section>
          <SectionTitle>Topology</SectionTitle>
          <TopologyMiniMap
            producers={producers}
            consumers={consumers}
            centerLabel={eventType?.name || params.id!}
            centerMeta={`${fieldCount} field${fieldCount === 1 ? "" : "s"}`}
          />
        </section>

        <section>
          <SectionTitle>Properties</SectionTitle>
          <EventTypePropertiesTable properties={eventType?.properties || []} />
        </section>

        {eventType && (
          <section>
            <SectionTitle>Example Payload</SectionTitle>
            <EventTypeExamplePayload eventType={eventType} consumers={consumers} />
          </section>
        )}
      </div>
    </Page>
  );
};

const SectionTitle: React.FC<{ children: React.ReactNode }> = ({ children }) => (
  <h2 className="m-0 mb-2.5 font-bold text-[11px] text-muted-foreground tracking-[0.12em] uppercase">
    {children}
  </h2>
);

export default EventTypeDetails;
