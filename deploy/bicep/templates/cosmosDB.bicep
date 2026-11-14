param name string
param location string = resourceGroup().location
param dbname string
param createIntelligenceContainer bool = false

// 'Disabled' only in the private network state (spec 034 §5.13). Container management
// goes through ARM, which data-plane network rules do not affect.
@allowed([
  'Enabled'
  'Disabled'
])
param publicNetworkAccess string = 'Enabled'

resource cosmosDbAccount 'Microsoft.DocumentDB/databaseAccounts@2022-05-15' = {
  name: name
  location: location
  kind: 'GlobalDocumentDB'
  properties: {
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
      maxStalenessPrefix: 100
      maxIntervalInSeconds: 5
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
      }
    ]
    databaseAccountOfferType: 'Standard'
    publicNetworkAccess: publicNetworkAccess
    networkAclBypass: 'None'
  }
}


resource sqlDb 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2021-06-15' = {
  parent: cosmosDbAccount
  name: dbname
  properties: {
    resource: {
      id: dbname
    }
  }
}

resource messagesContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2021-06-15' = {
  parent: sqlDb
  name: 'messages'
  properties: {
    resource: {
      id: 'messages'
      partitionKey: {
        paths: ['/eventId']
        kind: 'Hash'
      }
      defaultTtl: 7776000 // 90 days
      indexingPolicy: {
        indexingMode: 'consistent'
        includedPaths: [
          { path: '/*' }
        ]
        excludedPaths: [
          { path: '/message/messageContent/*' }
          { path: '/"_etag"/?' }
        ]
      }
    }
  }
}

resource auditsContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2021-06-15' = {
  parent: sqlDb
  name: 'audits'
  properties: {
    resource: {
      id: 'audits'
      partitionKey: {
        paths: ['/eventId']
        kind: 'Hash'
      }
      defaultTtl: 31536000 // 1 year
    }
  }
}

resource intelligenceSettingsContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2021-06-15' = if (createIntelligenceContainer) {
  parent: sqlDb
  name: 'intelligencesettings'
  properties: {
    resource: {
      id: 'intelligencesettings'
      partitionKey: {
        paths: ['/id']
        kind: 'Hash'
      }
    }
  }
}

resource intelligenceContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2021-06-15' = if (createIntelligenceContainer) {
  parent: sqlDb
  name: 'failureclassifications'
  properties: {
    resource: {
      id: 'failureclassifications'
      partitionKey: {
        paths: ['/failureMessageId']
        kind: 'Hash'
      }
    }
  }
}

// Shared containers the apps otherwise create lazily via the SDK. Lazy
// creation only works with account keys — Entra data-plane RBAC (which the
// deployed apps use) allows item reads/writes but NOT container management —
// so every shared container must be declared here. Per-endpoint containers
// (one per catalog endpoint, PK /id) are catalog-dependent and cannot be
// declared statically; create them alongside topology provisioning.
// Keep in sync with CosmosContainerDefaults.ReservedContainerIds (enforced by
// CosmosBicepContainerSyncTests). The one deliberate omission is 'inbox': the
// Cosmos consumer inbox is opt-in, its container id is configurable and it
// requires Strong consistency (this account is Session), so a subscriber that
// enables it provisions its own container (docs/inbox-pattern.md).
var sharedContainers = [
  { name: 'subscriptions', pk: '/id' }   // endpoint notification subscriptions
  { name: 'eventschemas', pk: '/id' }    // agent-defined event schemas (spec 022)
  { name: 'eventreports', pk: '/EndpointId' } // per-event reported markers
  { name: 'accesscontrol', pk: '/id' }   // site + endpoint ACLs (spec 026)
  { name: 'Metadata', pk: '/id' }        // endpoint metadata (owner, heartbeat opt-in)
  { name: 'settings', pk: '/id' }        // operator-tuned platform settings
  { name: 'servicehealth', pk: '/id' }   // Resolver liveness beats
  { name: 'endpointacknowledgements', pk: '/id' } // shared Monitor ACKs, one document per endpoint
  { name: 'operatoroperations', pk: '/id', ttl: -1 } // operator operation journal (Spec 044); TTL on, items expire after 30 days
  { name: 'heartbeatuptimedays', pk: '/EndpointId', ttl: -1 } // heartbeat uptime rollups; TTL on, items decide expiry
  { name: 'heartbeatgaps', pk: '/EndpointId', ttl: -1 }       // heartbeat silent periods; TTL on, items decide expiry
]

resource sharedContainerResources 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2021-06-15' = [for c in sharedContainers: {
  parent: sqlDb
  name: c.name
  properties: {
    // union() so containers without a ttl entry omit defaultTtl entirely —
    // the Cosmos RP rejects an explicit "defaultTtl": null as invalid input.
    resource: union({
      id: c.name
      partitionKey: {
        paths: [c.pk]
        kind: 'Hash'
      }
    }, c.?ttl != null ? { defaultTtl: c.?ttl } : {})
  }
}]

// No connection-string output: nothing consumed it, and producing it read the
// account keys on every deployment. The apps authenticate with Entra RBAC.
output id string = cosmosDbAccount.id
output accountEndpoint string = cosmosDbAccount.properties.documentEndpoint
