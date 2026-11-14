
param solutionId string
param environment string = 'dev'
param webAppVersion string
// Deprecated and ignored; removed in the next major version (docs/versioning.md). The
// Application Insights query API stopped accepting API keys on 2026-03-31, so the WebApp
// queries with its managed identity (Reader on the component, granted below). Still
// accepted, and still secure, so callers that pass a key keep deploying.
@secure()
@description('Deprecated and ignored. The WebApp queries Application Insights with its managed identity.')
#disable-next-line no-unused-params
param apiKey string = ''
param appInsightsAppId string
@secure()
param instrumentationKey string
// Cosmos endpoint is optional now: empty when the active provider is SQL Server.
param cosmosAccountEndpoint string = ''
// SQL connection string. Empty when the active provider is Cosmos. Exactly one of
// cosmosAccountEndpoint and sqlConnectionString must be non-empty. Secret-marked so
// it does not appear in deployment history; production deployments should pass a
// Key Vault reference (e.g. @Microsoft.KeyVault(...)) instead of a literal string.
@secure()
param sqlConnectionString string = ''
param serviceBusFullyQualifiedNamespace string
// Region for new resources. Defaults to the resource group's region; existing resources
// keep theirs through the per-resource overrides below.
param locationParam string = resourceGroup().location

// Optional bootstrap admin for NimBus.Extensions.Identity. When both are set AND
// the active storage provider is SQL Server, the WebApp is configured to use
// ASP.NET Core Identity-backed username/password sign-in; the initializer hosted
// service seeds this single admin on first boot. Leave empty to keep the WebApp
// on its existing auth path (Entra ID / local-dev).
param identityAdminEmail string = ''
@secure()
param identityAdminPassword string = ''

// Per-resource location override. Empty means "use the global locationParam".
// The CLI pins this to the existing web app's location when one is already
// present so we don't try to migrate it to a different region.
param webAppLocation string = ''

// The management App Service Plan location, supplied by the CLI. The WebApp
// MUST live in the same region as its plan (Azure rejects cross-region
// references with a NotFound error on the serverFarm), so when no explicit
// webAppLocation is supplied, we fall back to the plan's location instead of
// the global default.
param managementAppServicePlanLocation string = ''

// Always On keeps the WebApp warm. Supported on Basic and above; the CLI passes
// false when the management plan runs on a Free/Shared SKU (F1/D1).
param alwaysOnEnabled bool = true

// True when the WebApp already exists in the resource group (the CLI passes this
// from its resource discovery). Gates the list() call below, which fails against
// a site that has not been created yet.
param webAppExists bool = false

// Private networking (spec 034). Same meaning as in deploy.core.bicep, which runs
// first and, with privateDnsMode 'create', has already created the privatelink zones.
@allowed([
  'public'
  'private'
])
param networkMode string = 'public'
param allowPublicAccess bool = false
param privateEndpointSubnetId string = ''
param privateEndpointLocation string = ''

// The WebApp's outbound (VNet integration) subnet, delegated to Microsoft.Web/serverFarms.
param managementSubnetId string = ''

@allowed([
  ''
  'create'
  'existing'
  'external'
])
param privateDnsMode string = ''
param privateDnsZoneScope string = ''

// Must match the value passed to deploy.core.bicep. Empty means sb-{solutionId}-{environment}.
param serviceBusNamespaceName string = ''

// Legacy pipeline compatibility; must match the value passed to deploy.core.bicep. Postfixes
// the Service Bus, Cosmos DB and WebApp names; the App Service plan and App Insights keep
// their names, as the legacy templates did.
@description('Legacy resource-name postfix. Lowercase letters and digits; empty means none.')
param resourceNamePostfix string = ''

//##############################################
// Define names Azure resource names
//##############################################
var location = locationParam
var effectivePlanLocation = empty(managementAppServicePlanLocation) ? location : managementAppServicePlanLocation
var effectiveWebAppLocation = empty(webAppLocation) ? effectivePlanLocation : webAppLocation

var nameSuffix = empty(resourceNamePostfix) ? '' : '-${toLower(resourceNamePostfix)}'

var sbNamespace = empty(serviceBusNamespaceName) ? 'sb-${toLower(solutionId)}-${toLower(environment)}${nameSuffix}' : serviceBusNamespaceName

var isPrivate = networkMode == 'private'
var publicAccess = isPrivate && !allowPublicAccess ? 'Disabled' : 'Enabled'
var effectivePrivateEndpointLocation = empty(privateEndpointLocation) ? location : privateEndpointLocation
var sitesDnsZoneId = privateDnsMode == 'create'
  ? '${resourceGroup().id}/providers/Microsoft.Network/privateDnsZones/privatelink.azurewebsites.net'
  : '${privateDnsZoneScope}/providers/Microsoft.Network/privateDnsZones/privatelink.azurewebsites.net'

// Evaluated in the WebApp module name, so invalid input fails at template validation.
var networkValidation = !isPrivate
  ? ''
  : empty(privateEndpointSubnetId)
      ? fail('privateEndpointSubnetId is required when networkMode is private.')
      : empty(managementSubnetId)
          ? fail('managementSubnetId is required when networkMode is private.')
          : empty(privateDnsMode)
              ? fail('privateDnsMode (create, existing or external) is required when networkMode is private.')
              : privateDnsMode == 'existing' && empty(privateDnsZoneScope)
                  ? fail('privateDnsZoneScope is required when privateDnsMode is existing.')
                  : ''

var appServicePlanName = 'asp-${toLower(solutionId)}-${toLower(environment)}-management'

var managementWebAppName = 'webapp-${toLower(solutionId)}-${toLower(environment)}-management${nameSuffix}'

var cosmosAccountName = 'cosmos-${toLower(solutionId)}-${toLower(environment)}${nameSuffix}'

var appInsightsName = 'ai-${toLower(solutionId)}-${toLower(environment)}-global-tracelog'

var isDevelopmentEnvironment = contains([
  'dev'
  'development'
], toLower(environment))

//##############################################
// Create Web App: Conflict Resolution Web App
//##############################################

// Validate exactly one of the storage settings is supplied. Empty strings on both
// sides would silently leave the WebApp without a storage backend.
var hasCosmos = !empty(cosmosAccountEndpoint)
var hasSql = !empty(sqlConnectionString)
var validStorageInput = (hasCosmos && !hasSql) || (!hasCosmos && hasSql)

#disable-next-line BCP088
var storageValidation = validStorageInput ? '' : '[ERROR] Exactly one of cosmosAccountEndpoint or sqlConnectionString must be provided.'

var coreWebAppSettings = [
  {
    name: 'AzureWebJobsServiceBus__fullyQualifiedNamespace'
    value: serviceBusFullyQualifiedNamespace
  }
  {
    name: 'ServiceBusNamespace'
    value: sbNamespace
  }
  {
    name: 'UnresolvedEventLimit'
    value: '50'
  }
  {
    name: 'Environment'
    value: environment
  }
  {
    name: 'AppInsights:ApplicationId'
    value: appInsightsAppId
  }
  {
    name: 'WebAppVersion'
    value: webAppVersion
  }
  {
    // App Service always terminates at its front end, so RemoteIpAddress there
    // is the proxy. Without this the per-client-IP login limiter degenerates to
    // one bucket for the whole site. Template-owned on purpose: it describes
    // deployment topology, not a tuning preference — so unlike the numeric
    // RateLimiting__* limits it does NOT survive a portal edit. Local and
    // Aspire runs keep the safe 'false' default. See docs/rate-limiting.md.
    name: 'RateLimiting__TrustForwardedForHeader'
    value: 'true'
  }
]

var cosmosSetting = hasCosmos ? [
  {
    name: 'CosmosAccountEndpoint'
    value: cosmosAccountEndpoint
  }
  {
    // Container administration uses ARM because Entra-authenticated Cosmos data-plane
    // clients cannot create or delete containers.
    name: 'CosmosAccountResourceId'
    value: resourceId('Microsoft.DocumentDB/databaseAccounts', cosmosAccountName)
  }
] : []

// NimBus Identity (username/password) is wired only on SQL deployments and only
// when a bootstrap admin email is supplied. The connection string reuses the
// MessageDatabase; Identity tables live under a separate schema (default
// 'nimbus') so they don't collide with the message-store schema.
var identityEnabled = hasSql && !empty(identityAdminEmail)
var identitySettings = identityEnabled ? [
  {
    name: 'NimBusIdentity__RequireEmailConfirmation'
    value: 'false'
  }
  {
    name: 'NimBusIdentity__Bootstrap__Email'
    value: identityAdminEmail
  }
] : []

var baseWebAppSettings = concat(coreWebAppSettings, cosmosSetting, identitySettings)

// Secret settings cross the nested-deployment boundary as a secure object. If
// they were folded into the ordinary settings array, Azure deployment history
// could retain their literal values even though the top-level params are secure.
var coreWebAppSecretSettings = {
  APPINSIGHTS_INSTRUMENTATIONKEY: instrumentationKey
}

var sqlSecretSettings = hasSql ? {
  SqlConnection: sqlConnectionString
} : {}

var identitySecretSettings = identityEnabled ? {
  NimBusIdentity__ConnectionString: sqlConnectionString
  NimBusIdentity__Bootstrap__Password: identityAdminPassword
} : {}

// The WebApp's Entra ID sign-in config (AzureAd__*), the platform-admin grants
// (Authorization__*) and the optional portal
// deep-link config (ServiceBusManagement__*) are set out of band — this template
// creates no app registration, so it cannot produce them. appSettings deployment
// is a FULL REPLACE, so without carrying the current values forward every re-run
// would wipe them and take authentication down. They flow through the secure
// settings object because AzureAd__ClientSecret must stay out of deployment
// history; template-managed settings win on any future key collision.
var existingAppSettings = webAppExists ? list(resourceId('Microsoft.Web/sites/config', managementWebAppName, 'appsettings'), '2022-03-01').properties : {}
var preservedExistingSettings = toObject(
  filter(items(existingAppSettings), s => startsWith(s.key, 'AzureAd__') || startsWith(s.key, 'ServiceBusManagement__') || startsWith(s.key, 'Authorization__')),
  s => s.key,
  s => s.value)

var webAppSecretSettings = union(preservedExistingSettings, coreWebAppSecretSettings, sqlSecretSettings, identitySecretSettings)

var developmentDiagnosticSettings = isDevelopmentEnvironment ? [
  {
    name: 'ASPNETCORE_DETAILEDERRORS'
    value: 'true'
  }
  {
    name: 'ASPNETCORE_CAPTURESTARTUPERRORS'
    value: 'true'
  }
] : []

var webappsettings = concat(baseWebAppSettings, developmentDiagnosticSettings)

// Every app-setting key this template owns. Derived from the FINAL template-owned
// array so the exclusion below cannot miss a key, and written generically rather
// than as a literal comparison so it also hardens the documented "template wins
// on any key collision" rule above.
var templateManagedKeys = map(webappsettings, managed => managed.name)

// Operator-tuned rate limits (RateLimiting__*) are set out of band from the
// portal / az CLI, like the auth settings above, and appSettings deployment is a
// FULL REPLACE — without this a re-run silently restores the shipped defaults.
// Unlike the auth settings these are not secrets, so they travel in the plain
// settings array and stay visible in deployment history. Excluding the
// template-managed keys keeps the final array free of duplicate names, which
// Azure does not define behaviour for. map()/filter() rather than a
// for-expression: existingAppSettings comes from the runtime list() call, and a
// for-expression's collection must be calculable at deployment start (BCP178).
// The operator MCP endpoint's settings (NimBus__Mcp__*: Enabled, the MCP resource's
// Entra app registration, allowed origins) follow the same rule: this template
// creates no app registration, so they are set out of band once it exists and
// must survive a redeploy. None of them is a secret. So does the REST API's CORS
// allow-list (Cors__AllowedOrigins__*), which only the operator knows.
var preservedOperatorAppSettings = map(
  filter(
    items(existingAppSettings),
    setting => (startsWith(setting.key, 'RateLimiting__') || startsWith(setting.key, 'NimBus__Mcp__') || startsWith(setting.key, 'Cors__')) && !contains(templateManagedKeys, setting.key)),
  preserved => {
    name: preserved.key
    value: preserved.value
  })

var webappsettingsFinal = concat(webappsettings, preservedOperatorAppSettings)

module webAppModule 'templates/webApp.bicep' = {
  name: 'webAppDeploy${networkValidation}'
  params: {
    appName:managementWebAppName
    appServicePlanId:'/subscriptions/${subscription().subscriptionId}/resourceGroups/${resourceGroup().name}/providers/Microsoft.Web/serverfarms/${appServicePlanName}'
    location:effectiveWebAppLocation
    alwaysOn: alwaysOnEnabled
    settings:webappsettingsFinal
    secretSettings: webAppSecretSettings
    virtualNetworkSubnetId: isPrivate ? managementSubnetId : ''
    publicNetworkAccess: publicAccess
  }
}

// Operators reach the WebApp (and the runner its scm site) through this endpoint once
// public access is off. The data-service endpoints it depends on were created by
// deploy.core.bicep, which runs first.
module peWebApp 'templates/privateEndpoint.bicep' = if (isPrivate) {
  name: 'peWebAppDeploy'
  params: {
    name: 'pe-${managementWebAppName}-sites'
    location: effectivePrivateEndpointLocation
    subnetId: privateEndpointSubnetId
    privateLinkServiceId: webAppModule.outputs.id
    groupId: 'sites'
    privateDnsZoneIds: privateDnsMode == 'external' ? [] : [sitesDnsZoneId]
    tags: {
      'nimbus-deployment': '${toLower(solutionId)}-${toLower(environment)}${nameSuffix}'
    }
  }
}

//##############################################
// WebApp: RBAC role assignments
//##############################################

module webAppRoleAssignments 'templates/roleAssignments.bicep' = {
  name: 'webAppRoleAssignmentsDeploy'
  params: {
    serviceBusNamespaceName: sbNamespace
    cosmosAccountName: hasCosmos ? cosmosAccountName : ''
    principalId: webAppModule.outputs.identity
    storageProvider: hasCosmos ? 'cosmos' : 'sqlserver'
    grantCosmosControlPlaneAccess: hasCosmos
    // The event logs endpoint queries Application Insights as the site's managed identity.
    appInsightsName: appInsightsName
    grantAppInsightsQueryAccess: !empty(appInsightsAppId)
  }
}
