param solutionId string
param environment string = 'dev'
// Region for new resources. Defaults to the resource group's region; existing resources
// keep theirs through the per-resource overrides below.
param locationParam string = resourceGroup().location
param resolverId string

// Deprecated and ignored. It used to seed the Elastic Premium content share name, which
// then changed on every deployment and left the Resolver on a new, empty share. Still
// accepted so existing parameter files and pipelines keep deploying.
@description('Deprecated and ignored. The Elastic Premium content share name is now stable.')
#disable-next-line no-unused-params
param uniqueDeploy string = ''

// The Resolver's current WEBSITE_CONTENTSHARE on Elastic Premium. The CLI passes it for an
// existing Resolver so a redeployment keeps the share its code lives on. Empty means a new
// deployment, which gets a stable name derived from the resource group and app.
param existingResolverContentShare string = ''

// Per-resource location overrides. Empty means "use the global locationParam".
// The CLI populates these when it finds an existing resource in the resource
// group so we never try to move a resource into a different region — Azure
// refuses to recreate a named resource in a new location.
param serviceBusLocation string = ''
param appInsightsLocation string = ''
param cosmosLocation string = ''
param sqlLocation string = ''
param funcStorageLocation string = ''
param managementAppServicePlanLocation string = ''
param coreAppServicePlanLocation string = ''
param resolverFunctionAppLocation string = ''

// Storage provider selection. 'cosmos' (default, backwards-compatible) provisions a
// Cosmos DB account. 'sqlserver' skips Cosmos and may optionally provision Azure SQL
// when sqlMode is 'provision'; 'external' expects a customer-supplied connection string.
@allowed([
  'cosmos'
  'sqlserver'
])
param storageProvider string = 'cosmos'

// Enables provisioning of the optional failure-classification Cosmos container.
// The WebApp feature remains disabled unless its application configuration is
// also enabled and valid.
param integrationIntelligenceEnabled bool = false

@allowed([
  'provision'
  'external'
])
param sqlMode string = 'provision'

param sqlAdminLogin string = ''
@secure()
param sqlAdminPassword string = ''

// Optional override for the SQL server name. Azure SQL server names are globally
// unique across all of Azure and are held in the DNS namespace for 24-72 hours
// after a delete, so users sometimes need to pick a fresh name to redeploy.
// Empty means "use the default sql-{solutionId}-{environment}".
param sqlServerName string = ''

// Resolver Function App hosting plan. 'FlexConsumption' (default, FC1, Linux)
// scales to zero and is significantly cheaper. 'ElasticPremium' (EP1, Windows)
// remains available for workloads that need it. The CLI pins the plan type of an
// existing deployment because Azure cannot convert between the two in place.
@allowed([
  'ElasticPremium'
  'FlexConsumption'
])
param resolverPlan string = 'FlexConsumption'

// SKU for the management App Service Plan hosting the WebApp. Empty means
// "environment default": B1 for dev/development, S1 otherwise.
param managementPlanSku string = ''

// Resolver Service Bus session concurrency per instance. Applied as a
// template-owned host override (AzureFunctionsJobHost__...), so it takes
// precedence over host.json. The Resolver is bounded by its Cosmos RU budget,
// not by the bus; 200 sessions per instance produced a 429 storm in production.
@minValue(1)
@maxValue(200)
param resolverMaxConcurrentSessions int = 16

// Elastic Premium per-app scale ceiling (siteConfig.functionAppScaleLimit).
// 0 means no per-app cap (today's behaviour) and is written explicitly, so a cap
// applied by an earlier deployment is cleared rather than left in place. The EP
// plan template allows at most 10 workers (maximumElasticWorkerCount), so 10 is
// the useful maximum.
@minValue(0)
@maxValue(10)
param resolverMaxInstances int = 0

// Flex Consumption maximumInstanceCount (platform range 1-1000; template default
// 100). Kept separate from the Elastic Premium parameter above because 0 has no
// "no cap" meaning on Flex.
@minValue(1)
@maxValue(1000)
param resolverFlexMaximumInstanceCount int = 100

//##############################################
// Private networking (spec 034). 'public' deploys what NimBus always deployed.
//##############################################

@allowed([
  'public'
  'private'
])
param networkMode string = 'public'

// With networkMode 'private': add private endpoints, DNS and VNet integration but keep
// every public path open (the private-transition state, spec 034 §5.13). Existing
// deployments pass through this state so nothing is locked before the apps reach it
// privately.
param allowPublicAccess bool = false

// Customer-owned subnets. Private endpoints take their addresses from the first; the
// resolver's outbound traffic uses the second (Flex: delegated to
// Microsoft.App/environments; Elastic Premium: Microsoft.Web/serverFarms).
param privateEndpointSubnetId string = ''
param resolverSubnetId string = ''

// Region of the private-endpoint subnet's virtual network. Empty means locationParam.
param privateEndpointLocation string = ''

// 'create': NimBus creates the privatelink zones and links them to
// privateDnsLinkVnetIds. 'existing': the zones live in the resource group given by
// privateDnsZoneScope. 'external': no zone groups; customer policy or DNS writes records.
@allowed([
  ''
  'create'
  'existing'
  'external'
])
param privateDnsMode string = ''
param privateDnsZoneScope string = ''
param privateDnsLinkVnetIds array = []

// Service Bus tier. Empty means Standard in public mode and Premium in private mode;
// the CLI passes the existing namespace's tier so a Premium namespace is never sent
// Standard. Premium is required for private endpoints.
@allowed([
  ''
  'Standard'
  'Premium'
])
param serviceBusSku string = ''

@allowed([
  1
  2
  4
  8
  16
])
param serviceBusCapacity int = 1

// Override for the namespace name, e.g. a new Premium namespace next to the Standard
// one it replaces (spec 034 §6, option A). Empty means sb-{solutionId}-{environment}.
param serviceBusNamespaceName string = ''

// Endpoint alert mail: the Resolver mails the subscribers on an endpoint's Alerts tab over
// SMTP (Azure Communication Services, Microsoft 365, a SendGrid relay). Off unless both
// alertMailSmtpHost and alertMailFrom are set. The Resolver's app settings are replaced on
// every deploy, so pass these on every deploy; settings added by hand are removed.
param alertMailSmtpHost string = ''
@minValue(1)
@maxValue(65535)
param alertMailSmtpPort int = 587
param alertMailSmtpUseSsl bool = true
param alertMailSmtpUsername string = ''
@secure()
param alertMailSmtpPassword string = ''
param alertMailFrom string = ''
// The management WebApp's public URL, linked from each mail. Empty means the default
// https://webapp-{solutionId}-{environment}-management.azurewebsites.net.
param alertMailWebAppUrl string = ''

//##############################################
// Define names Azure resource names
//##############################################
var location = locationParam

// Resolve a per-resource location, falling back to the global locationParam.
// The resolver Function App MUST live in the same region as its App Service
// Plan (Azure rejects cross-region plan-to-app references with a NotFound
// error on the serverFarm). So its fallback is the core plan's location, not
// the global default.
var effectiveServiceBusLocation = empty(serviceBusLocation) ? location : serviceBusLocation
var effectiveAppInsightsLocation = empty(appInsightsLocation) ? location : appInsightsLocation
var effectiveCosmosLocation = empty(cosmosLocation) ? location : cosmosLocation
var effectiveSqlLocation = empty(sqlLocation) ? location : sqlLocation
var effectiveFuncStorageLocation = empty(funcStorageLocation) ? location : funcStorageLocation
var effectiveManagementAppServicePlanLocation = empty(managementAppServicePlanLocation) ? location : managementAppServicePlanLocation
var effectiveCoreAppServicePlanLocation = empty(coreAppServicePlanLocation) ? location : coreAppServicePlanLocation
var effectiveResolverFunctionAppLocation = empty(resolverFunctionAppLocation) ? effectiveCoreAppServicePlanLocation : resolverFunctionAppLocation

var sbNamespace = empty(serviceBusNamespaceName) ? 'sb-${toLower(solutionId)}-${toLower(environment)}' : serviceBusNamespaceName

var managementAppServicePlanName = 'asp-${toLower(solutionId)}-${toLower(environment)}-management'

var coreAppServicePlanName = 'asp-${toLower(solutionId)}-${toLower(environment)}-core'

var cosmosAccountName = 'cosmos-${toLower(solutionId)}-${toLower(environment)}'

var cosmosDbName = 'MessageDatabase'

var defaultSqlServerName = 'sql-${toLower(solutionId)}-${toLower(environment)}'
var effectiveSqlServerName = empty(sqlServerName) ? defaultSqlServerName : sqlServerName

var sqlDbName = 'MessageDatabase'

var resolverFunctionAppName = 'func-${toLower(solutionId)}-${toLower(environment)}-resolver'

var funcStorageAccountName = 'st${toLower(solutionId)}${toLower(environment)}func'

var appInsightsName = 'ai-${toLower(solutionId)}-${toLower(environment)}-global-tracelog'

var isDevelopmentEnvironment = contains([
  'dev'
  'development'
], toLower(environment))

var effectiveManagementPlanSku = empty(managementPlanSku)
  ? (isDevelopmentEnvironment ? 'B1' : 'S1')
  : managementPlanSku

//##############################################
// Private networking: network state, DNS zones, validation
//##############################################

// public, private-transition (endpoints and VNet integration, public still open) or
// private (public access off everywhere). Spec 034 §5.13 has the full state table.
var isPrivate = networkMode == 'private'
var publicAccess = isPrivate && !allowPublicAccess ? 'Disabled' : 'Enabled'
var effectiveServiceBusSku = serviceBusSku == 'Premium' || (empty(serviceBusSku) && isPrivate) ? 'Premium' : 'Standard'
var effectivePrivateEndpointLocation = empty(privateEndpointLocation) ? location : privateEndpointLocation

// Every network resource NimBus creates carries this tag; the rollback cleanup deletes
// only resources that do (spec 034 §5.13).
var networkTags = {
  'nimbus-deployment': '${toLower(solutionId)}-${toLower(environment)}'
}

var dnsZonePrefix = privateDnsMode == 'create'
  ? '${resourceGroup().id}/providers/Microsoft.Network/privateDnsZones/'
  : '${privateDnsZoneScope}/providers/Microsoft.Network/privateDnsZones/'
var registersDns = isPrivate && privateDnsMode != 'external'
var dnsZoneNames = {
  serviceBus: 'privatelink.servicebus.windows.net'
  cosmos: 'privatelink.documents.azure.com'
  sql: 'privatelink${az.environment().suffixes.sqlServerHostname}'
  blob: 'privatelink.blob.${az.environment().suffixes.storage}'
  queue: 'privatelink.queue.${az.environment().suffixes.storage}'
  table: 'privatelink.table.${az.environment().suffixes.storage}'
  file: 'privatelink.file.${az.environment().suffixes.storage}'
  sites: 'privatelink.azurewebsites.net'
}

// Zones this deployment's endpoints need; the WebApp template reuses the sites zone.
var requiredDnsZoneNames = concat(
  [
    dnsZoneNames.serviceBus
    dnsZoneNames.blob
    dnsZoneNames.queue
    dnsZoneNames.table
    dnsZoneNames.sites
  ],
  storageProvider == 'cosmos' ? [dnsZoneNames.cosmos] : [],
  storageProvider == 'sqlserver' && sqlMode == 'provision' ? [dnsZoneNames.sql] : [],
  resolverPlan == 'ElasticPremium' ? [dnsZoneNames.file] : [])

// Evaluated in a module name below, so invalid input fails at template validation
// instead of halfway through the deployment.
var networkValidation = !isPrivate
  ? ''
  : empty(privateEndpointSubnetId)
      ? fail('privateEndpointSubnetId is required when networkMode is private.')
      : empty(resolverSubnetId)
          ? fail('resolverSubnetId is required when networkMode is private.')
          : empty(privateDnsMode)
              ? fail('privateDnsMode (create, existing or external) is required when networkMode is private.')
              : privateDnsMode == 'existing' && empty(privateDnsZoneScope)
                  ? fail('privateDnsZoneScope is required when privateDnsMode is existing.')
                  : effectiveServiceBusSku != 'Premium'
                      ? fail('Private networking needs a Premium Service Bus namespace; Standard has no private endpoints.')
                      : ''

//##############################################
//# Create Service Bus namespace
//##############################################

module serviceBusNamespace 'templates/servicebusNamespace.bicep' = {
  name : 'ServicebusNamespaceDeploy${networkValidation}'
  params : {
    name: sbNamespace
    location: effectiveServiceBusLocation
    sku: effectiveServiceBusSku
    capacity: serviceBusCapacity
    publicNetworkAccess: publicAccess
  }
}

//##############################################
//# Private DNS zones (privateDnsMode 'create' only)
//##############################################

module privateDnsZones 'templates/privateDnsZones.bicep' = if (isPrivate && privateDnsMode == 'create') {
  name: 'privateDnsZonesDeploy'
  params: {
    zoneNames: requiredDnsZoneNames
    vnetIds: empty(privateDnsLinkVnetIds)
      ? [substring(privateEndpointSubnetId, 0, indexOf(privateEndpointSubnetId, '/subnets/'))]
      : privateDnsLinkVnetIds
    tags: networkTags
  }
}

//##############################################
//# Create Application Insights (for global trace log)
//##############################################

module applicationinsights 'templates/applicationInsights.bicep' = {
  name: 'AppinsightsDeploy'
  params: {
    name: appInsightsName
    location: effectiveAppInsightsLocation
  }
}

//##############################################
//# Cosmos DB: Create Account and Database
//##############################################

module cosmosAccount 'templates/cosmosDB.bicep' = if (storageProvider == 'cosmos') {
  name: 'cosmosDBDeploy'
  params: {
    name: cosmosAccountName
    dbname: cosmosDbName
    createIntelligenceContainer: integrationIntelligenceEnabled
    publicNetworkAccess: publicAccess
    location: effectiveCosmosLocation
  }
}

//##############################################
//# Azure SQL: Create server + database (only when storageProvider=sqlserver and sqlMode=provision)
//##############################################

module azureSql 'templates/azureSql.bicep' = if (storageProvider == 'sqlserver' && sqlMode == 'provision') {
  name: 'azureSqlDeploy'
  params: {
    serverName: effectiveSqlServerName
    databaseName: sqlDbName
    location: effectiveSqlLocation
    administratorLogin: sqlAdminLogin
    administratorPassword: sqlAdminPassword
    publicNetworkAccess: publicAccess
  }
}

//##############################################
//# Function Apps: Create storage account
//##############################################

module funcstorageaccount 'templates/storageaccount.bicep' = {
  name : 'FuncStorageAccountDeploy'
  params : {
    name: funcStorageAccountName
    location: effectiveFuncStorageLocation
    // Flex Consumption needs a blob container holding the app package, referenced
    // from the Function App via SystemAssignedIdentity. Provision it inline so
    // the container exists before resolverFunctionFlex tries to bind to it.
    createDeploymentContainer: resolverPlan == 'FlexConsumption'
    publicNetworkAccess: publicAccess
    // Once the account is private, the platform can no longer create the Elastic
    // Premium content share itself.
    contentShareName: isPrivate && resolverPlan == 'ElasticPremium' ? resolverContentShareName : ''
  }
}

//##############################################
//# Create App Service Plan for management app
//##############################################

module appserviceplan 'templates/appServicePlan.bicep' = {
  name: 'ManagementPlanDeploy'
  params: {
    name: managementAppServicePlanName
    skuName: effectiveManagementPlanSku
    location: effectiveManagementAppServicePlanLocation
  }
}

//##############################################
//# Resolver: Function App settings shared by both plan types
//##############################################

// Settings every resolver host needs regardless of plan type.
var sharedResolverSettings = [
  {
    name: 'ServiceBusNamespace'
    value: sbNamespace
  }
  {
    name: 'ResolverId'
    value: resolverId
  }
  {
    name: 'AzureWebJobsServiceBus__fullyQualifiedNamespace'
    value: serviceBusNamespace.outputs.fullyQualifiedNamespace
  }
  // The one tunable consumer bound. The Resolver's app settings are template-owned
  // and fully replaced on each deploy, so this override lives here and takes
  // precedence over host.json. The fixed bounds (prefetch 0, 1 s session idle
  // timeout, dynamic concurrency off) ship in host.json with the binary and are
  // pinned by ResolverHostConfigurationTests; keeping them out of the template
  // leaves each value with exactly one owner.
  {
    name: 'AzureFunctionsJobHost__extensions__serviceBus__maxConcurrentSessions'
    value: string(resolverMaxConcurrentSessions)
  }
]

// Elastic Premium needs the Windows host to know where its content share lives.
// Flex Consumption rejects these settings.
// A share name must stay the same across deployments: pointing WEBSITE_CONTENTSHARE at a
// new share leaves the app without its deployed code.
var resolverContentShareName = empty(existingResolverContentShare)
  ? '${toLower(resolverFunctionAppName)}${uniqueString(resourceGroup().id, resolverFunctionAppName)}'
  : existingResolverContentShare

var elasticPremiumExtraSettings = resolverPlan == 'ElasticPremium' ? [
  {
    name: 'WEBSITE_CONTENTSHARE'
    value: resolverContentShareName
  }
] : []

var cosmosResolverSetting = storageProvider == 'cosmos' ? [
  {
    name: 'CosmosAccountEndpoint'
    value: cosmosAccount.outputs.accountEndpoint
  }
] : []

var alertMailEnabled = !empty(alertMailSmtpHost) && !empty(alertMailFrom)
var effectiveAlertMailWebAppUrl = empty(alertMailWebAppUrl)
  ? 'https://webapp-${toLower(solutionId)}-${toLower(environment)}-management.azurewebsites.net'
  : alertMailWebAppUrl

var alertMailResolverSettings = alertMailEnabled ? concat([
  {
    name: 'NimBus__Alerts__Mail__Smtp__Host'
    value: alertMailSmtpHost
  }
  {
    name: 'NimBus__Alerts__Mail__Smtp__Port'
    value: string(alertMailSmtpPort)
  }
  {
    name: 'NimBus__Alerts__Mail__Smtp__UseSsl'
    value: string(alertMailSmtpUseSsl)
  }
  {
    name: 'NimBus__Alerts__Mail__From'
    value: alertMailFrom
  }
  {
    name: 'NimBus__Alerts__Mail__WebAppUrl'
    value: effectiveAlertMailWebAppUrl
  }
], empty(alertMailSmtpUsername) ? [] : [
  {
    name: 'NimBus__Alerts__Mail__Smtp__Username'
    value: alertMailSmtpUsername
  }
]) : []

var resolverappsettings = concat(sharedResolverSettings, elasticPremiumExtraSettings, cosmosResolverSetting, alertMailResolverSettings)

// Secrets must remain secure across the nested module boundary. Passing them in
// the ordinary settings array would retain their values in nested deployment
// history even though sqlAdminPassword is a secure top-level parameter.
var sharedResolverSecretSettings = {
  GlobalTraceLogInstrKey: applicationinsights.outputs.instrumentationKey
  // NimBus.ServiceDefaults registers the Azure Monitor exporter only when
  // APPLICATIONINSIGHTS_CONNECTION_STRING is present, so both plan branches need
  // it. The Elastic Premium branch previously received only
  // APPINSIGHTS_INSTRUMENTATIONKEY (injected by templates/functionApp.bicep) and
  // therefore exported no telemetry.
  APPLICATIONINSIGHTS_CONNECTION_STRING: applicationinsights.outputs.connectionString
}

var elasticPremiumSecretSettings = resolverPlan == 'ElasticPremium' ? {
  WEBSITE_CONTENTAZUREFILECONNECTIONSTRING: funcstorageaccount.outputs.connectionString
} : {}

var sqlResolverSecretSettings = storageProvider == 'sqlserver' && sqlMode == 'provision' ? {
  SqlConnection: 'Server=tcp:${azureSql.outputs.serverFqdn},1433;Initial Catalog=${sqlDbName};User ID=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=true;'
} : {}

var alertMailResolverSecretSettings = alertMailEnabled && !empty(alertMailSmtpPassword) ? {
  NimBus__Alerts__Mail__Smtp__Password: alertMailSmtpPassword
} : {}

var resolverSecretSettings = union(
  sharedResolverSecretSettings,
  elasticPremiumSecretSettings,
  sqlResolverSecretSettings,
  alertMailResolverSecretSettings)

//##############################################
//# Private endpoints for the data services (private mode only)
//##############################################

// Created after the services and before the Resolver joins the VNet (the dependsOn on
// both Resolver modules), so the app never starts in a network where its dependencies
// don't resolve (spec 034 §5.13).
module peServiceBus 'templates/privateEndpoint.bicep' = if (isPrivate) {
  name: 'peServiceBusDeploy'
  params: {
    name: 'pe-${sbNamespace}-namespace'
    location: effectivePrivateEndpointLocation
    subnetId: privateEndpointSubnetId
    privateLinkServiceId: serviceBusNamespace.outputs.id
    groupId: 'namespace'
    privateDnsZoneIds: registersDns ? ['${dnsZonePrefix}${dnsZoneNames.serviceBus}'] : []
    tags: networkTags
  }
  dependsOn: [
    privateDnsZones
  ]
}

module peCosmos 'templates/privateEndpoint.bicep' = if (isPrivate && storageProvider == 'cosmos') {
  name: 'peCosmosDeploy'
  params: {
    name: 'pe-${cosmosAccountName}-sql'
    location: effectivePrivateEndpointLocation
    subnetId: privateEndpointSubnetId
    privateLinkServiceId: cosmosAccount!.outputs.id
    groupId: 'Sql'
    privateDnsZoneIds: registersDns ? ['${dnsZonePrefix}${dnsZoneNames.cosmos}'] : []
    tags: networkTags
  }
  dependsOn: [
    privateDnsZones
  ]
}

// An 'external' SQL server is the customer's; its private endpoint is theirs too.
module peSql 'templates/privateEndpoint.bicep' = if (isPrivate && storageProvider == 'sqlserver' && sqlMode == 'provision') {
  name: 'peSqlDeploy'
  params: {
    name: 'pe-${effectiveSqlServerName}-sqlserver'
    location: effectivePrivateEndpointLocation
    subnetId: privateEndpointSubnetId
    privateLinkServiceId: azureSql!.outputs.id
    groupId: 'sqlServer'
    privateDnsZoneIds: registersDns ? ['${dnsZonePrefix}${dnsZoneNames.sql}'] : []
    tags: networkTags
  }
  dependsOn: [
    privateDnsZones
  ]
}

// The Functions host uses blob, queue and table; Elastic Premium also mounts its
// content share from Azure Files.
var storageGroupIds = concat(['blob', 'queue', 'table'], resolverPlan == 'ElasticPremium' ? ['file'] : [])

module peStorage 'templates/privateEndpoint.bicep' = [for groupId in storageGroupIds: if (isPrivate) {
  name: 'peStorage-${groupId}-Deploy'
  params: {
    name: 'pe-${funcStorageAccountName}-${groupId}'
    location: effectivePrivateEndpointLocation
    subnetId: privateEndpointSubnetId
    privateLinkServiceId: funcstorageaccount.outputs.storageId
    groupId: groupId
    privateDnsZoneIds: registersDns ? ['${dnsZonePrefix}${dnsZoneNames[groupId]}'] : []
    tags: networkTags
  }
  dependsOn: [
    privateDnsZones
  ]
}]

//##############################################
//# Resolver: Hosting plan + Function App (Elastic Premium branch)
//##############################################

module functionappplanElastic 'templates/functionAppPlan.bicep' = if (resolverPlan == 'ElasticPremium') {
  name: 'functionAppplanDeploy'
  params: {
    name: coreAppServicePlanName
    skuName: 'EP1'
    location: effectiveCoreAppServicePlanLocation
  }
}

module resolverFunctionElastic 'templates/functionApp.bicep' = if (resolverPlan == 'ElasticPremium') {
  name: 'resolverDeploy'
  params: {
    appName: resolverFunctionAppName
    appInsightsInstrumentationKey: applicationinsights.outputs.instrumentationKey
    appServicePlanId: functionappplanElastic.outputs.id
    functionAppVersion: '4'
    storageConnectionString: funcstorageaccount.outputs.connectionString
    location: effectiveResolverFunctionAppLocation
    settings: resolverappsettings
    secretSettings: resolverSecretSettings
    functionAppScaleLimit: resolverMaxInstances
    virtualNetworkSubnetId: isPrivate ? resolverSubnetId : ''
    publicNetworkAccess: publicAccess
  }
  dependsOn: [
    peServiceBus
    peCosmos
    peSql
    peStorage
  ]
}

//##############################################
//# Resolver: Hosting plan + Function App (Flex Consumption branch)
//##############################################

module functionappplanFlex 'templates/flexConsumptionPlan.bicep' = if (resolverPlan == 'FlexConsumption') {
  name: 'functionAppplanFlexDeploy'
  params: {
    name: coreAppServicePlanName
    location: effectiveCoreAppServicePlanLocation
  }
}

module resolverFunctionFlex 'templates/flexConsumptionFunctionApp.bicep' = if (resolverPlan == 'FlexConsumption') {
  name: 'resolverFlexDeploy'
  params: {
    appName: resolverFunctionAppName
    appServicePlanId: functionappplanFlex.outputs.id
    storageAccountName: funcstorageaccount.outputs.storageName
    deploymentStorageBlobUri: '${funcstorageaccount.outputs.blobEndpoint}app-package-resolver'
    location: effectiveResolverFunctionAppLocation
    settings: resolverappsettings
    secretSettings: resolverSecretSettings
    maximumInstanceCount: resolverFlexMaximumInstanceCount
    virtualNetworkSubnetId: isPrivate ? resolverSubnetId : ''
    publicNetworkAccess: publicAccess
  }
  dependsOn: [
    peServiceBus
    peCosmos
    peSql
    peStorage
  ]
}

// Branch-aware principal id; one of the two modules deploys, the other is skipped.
var resolverPrincipalId = resolverPlan == 'FlexConsumption'
  ? resolverFunctionFlex.outputs.principalId
  : resolverFunctionElastic.outputs.principalId

var resolverSiteId = resolverPlan == 'FlexConsumption'
  ? resolverFunctionFlex!.outputs.id
  : resolverFunctionElastic!.outputs.id

// The Resolver has no HTTP triggers; this endpoint exists so an in-network runner can
// reach its Kudu/scm site to deploy once public access is off.
module peResolver 'templates/privateEndpoint.bicep' = if (isPrivate) {
  name: 'peResolverDeploy'
  params: {
    name: 'pe-${resolverFunctionAppName}-sites'
    location: effectivePrivateEndpointLocation
    subnetId: privateEndpointSubnetId
    privateLinkServiceId: resolverSiteId
    groupId: 'sites'
    privateDnsZoneIds: registersDns ? ['${dnsZonePrefix}${dnsZoneNames.sites}'] : []
    tags: networkTags
  }
  dependsOn: [
    privateDnsZones
  ]
}

//##############################################
//# Resolver: RBAC role assignments
//##############################################

// Service Bus RBAC must always be granted to the resolver identity (managed-identity
// access to receive/complete messages). Cosmos role assignment is gated inside the
// module so SQL deployments don't try to create Cosmos sqlRoleAssignments resources.
// Flex Consumption additionally needs Storage Blob Data Owner on the function
// storage account for the deployment package + AzureWebJobsStorage host runtime
// (the host key store lives in blobs and requires Owner, not Contributor).
module resolverRoleAssignments 'templates/roleAssignments.bicep' = {
  name: 'resolverRoleAssignmentsDeploy'
  params: {
    serviceBusNamespaceName: sbNamespace
    cosmosAccountName: storageProvider == 'cosmos' ? cosmosAccountName : ''
    principalId: resolverPrincipalId
    storageProvider: storageProvider
    funcStorageAccountName: funcStorageAccountName
    grantFuncStorageBlobAccess: resolverPlan == 'FlexConsumption'
  }
}
