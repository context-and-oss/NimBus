using CrmErpDemo.Contracts.Endpoints;
using NimBus.Core;
using NimBus.Core.Endpoints;

namespace CrmErpDemo.Contracts;

public class CrmErpPlatformConfiguration : Platform
{
    // Dynamic event forwards: events with a string EventTypeId but no compiled IEvent class.
    // Consumed by ServiceBusTopologyProvisioner (spec 022 D5), which the demo's provisioner runs
    // against both the NimBus emulator and real Azure.
    private static readonly IReadOnlyList<DynamicForward> _dynamicForwards =
    [
        // Spec 022: enriched CRM contacts flow from Agent Zone to DataPlatform.
        new DynamicForward("AgentZoneEndpoint", "crm.contact.enriched.v1", "DataPlatformEndpoint"),
    ];

    public override IReadOnlyList<DynamicForward> DynamicForwards => _dynamicForwards;

    public CrmErpPlatformConfiguration()
    {
        AddEndpoint(new CrmEndpoint());
        AddEndpoint(new ErpEndpoint());
        AddEndpoint(new DataPlatformEndpoint());
        AddEndpoint(new AgentZoneEndpoint());

        // Routing-keys showcase (Spec 045): one hub per region, plus the unmatched consumer.
        AddEndpoint(new NordicHubEndpoint());
        AddEndpoint(new DachHubEndpoint());
        AddEndpoint(new UnroutedHubEndpoint());
    }
}
