param name string
param location string = resourceGroup().location

// A reused account keeps its own SKU and kind: changing either on redeploy would alter its
// replication or irreversibly upgrade a 'Storage' account to 'StorageV2'.
param skuName string = 'Standard_LRS'
@allowed([
  'StorageV2'
  'Storage'
])
param kind string = 'StorageV2'

// A reused account's current networkAcls (ARM shape), so a redeployment does not replace its
// firewall rules with the defaults below. Empty for an account NimBus owns.
param existingNetworkAcls object = {}

// When the resolver is hosted on Flex Consumption, it needs a blob container
// that holds its zipped app package. We provision it here so the Function App
// can reference it via SystemAssignedIdentity at create time.
param createDeploymentContainer bool = false
param deploymentContainerName string = 'app-package-resolver'

// 'Disabled' only in the private network state (spec 034 §5.13). In the public and
// private-transition states the firewall default stays Allow: Deny alone would cut
// off every client that does not yet have a private path.
@allowed([
  'Enabled'
  'Disabled'
])
param publicNetworkAccess string = 'Enabled'

// Elastic Premium keeps its code on an Azure Files content share. Once the account is
// private the platform can no longer create that share, so the template declares it.
param contentShareName string = ''

var isLocked = publicNetworkAccess == 'Disabled'

// Locking keeps a reused account's IP and VNet rules (public access is off, so they have no
// effect) and only forces the default action; otherwise a reused account keeps its rules as-is.
var defaultNetworkAcls = {
  defaultAction: 'Allow'
  bypass: 'AzureServices'
}
var networkAcls = isLocked
  ? union(existingNetworkAcls, {
      defaultAction: 'Deny'
      bypass: 'None'
    })
  : (empty(existingNetworkAcls) ? defaultNetworkAcls : existingNetworkAcls)

resource storageaccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: name
  location: location
  kind: kind
  sku: {
    name: skuName
  }
  properties: union({
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: publicNetworkAccess
    networkAcls: networkAcls
  }, isLocked ? {
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
  } : {})
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = if (createDeploymentContainer) {
  parent: storageaccount
  name: 'default'
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = if (createDeploymentContainer) {
  parent: blobService
  name: deploymentContainerName
}

resource fileService 'Microsoft.Storage/storageAccounts/fileServices@2023-05-01' = if (!empty(contentShareName)) {
  parent: storageaccount
  name: 'default'
}

resource contentShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = if (!empty(contentShareName)) {
  parent: fileService
  name: contentShareName
}

@secure()
output connectionString string = 'DefaultEndpointsProtocol=https;AccountName=${name};AccountKey=${listKeys(storageaccount.id, storageaccount.apiVersion).keys[0].value};EndpointSuffix=core.windows.net'
output storageId string = storageaccount.id
output storageName string = storageaccount.name
output blobEndpoint string = storageaccount.properties.primaryEndpoints.blob
