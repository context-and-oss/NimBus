// Sample parameters for a production deployment of the NimBus core infrastructure.
// Deploy with (Azure CLI >= 2.53.0, Bicep CLI >= 0.22.6):
//   az deployment group create \
//     --resource-group rg-nimbus-prod \
//     --template-file ../deploy.core.bicep \
//     --parameters deploy.core.prod.bicepparam
// See docs/deployment.md ("Raw Bicep") for the full self-service walkthrough.
using '../deploy.core.bicep'

param solutionId = 'nimbus'
param environment = 'prod'

// Endpoint id of the central resolver. Must match the compiled-in constant
// (src/NimBus.Core/Constants.cs: ResolverId = 'Resolver').
param resolverId = 'Resolver'

// Salts the Elastic Premium content-share name; pass a fresh value (e.g. a
// build id) per deployment when using the ElasticPremium plan.
param uniqueDeploy = 'manual-prod'

param locationParam = 'westeurope'

// Production pins its choices explicitly instead of relying on defaults.
// NOTE: Azure cannot convert a plan between ElasticPremium (Windows) and
// FlexConsumption (Linux) in place — pick once, or delete the resolver
// Function App AND the core plan before switching.
param resolverPlan = 'FlexConsumption' // or 'ElasticPremium' (EP1, Windows)
param managementPlanSku = 'S1'
param storageProvider = 'cosmos' // or 'sqlserver' (+ sqlMode, then sqlAdmin* for provision or sqlConnectionString for external)

// Resolver capacity. The session count is a template-owned host override and
// wins over host.json. The instance ceiling is plan-specific: only the
// parameter matching resolverPlan above takes effect.
param resolverMaxConcurrentSessions = 16 // 1-200 Service Bus sessions per instance
param resolverFlexMaximumInstanceCount = 100 // FlexConsumption: 1-1000
// param resolverMaxInstances = 4            // ElasticPremium: 0 = no per-app cap, max 10
