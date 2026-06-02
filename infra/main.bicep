// ═══════════════════════════════════════════════════════════════════════
// OPC UA Knowledge Base — Azure Infrastructure
// Deploys all resources needed to run the crawl+index pipeline and
// expose the knowledge base via Azure AI Search MCP endpoint.
// ═══════════════════════════════════════════════════════════════════════

@minLength(3)
@description('Prefix used to derive all resource names')
param prefix string = 'opcua-kb'

@description('Azure region for all resources')
param location string = resourceGroup().location

@description('GPT-4o model version')
param gptModelVersion string = '2024-11-20'

@description('Container image for the pipeline job (empty = placeholder)')
param pipelineImage string = ''

@description('Cron schedule for the pipeline job')
param cronSchedule string = '0 2 * * 0'

@description('Optional: UA-CloudLibrary username (enables CloudLib integration)')
@secure()
param cloudLibUsername string = ''

@description('Optional: UA-CloudLibrary password')
@secure()
param cloudLibPassword string = ''

@description('When true, set storage publicNetworkAccess=Disabled and require VNet/private-endpoint access only. Toggle on after the first pipeline run has validated the deployment end-to-end.')
param lockdownStorage bool = false

// ── Derived names ────────────────────────────────────────────────────
var searchName = '${prefix}-search'
var foundryName = '${prefix}-foundry'
var storageName = take(replace('${prefix}storage', '-', ''), 24)
var docaiName = '${prefix}-docai'
var acrName = replace('${prefix}registry', '-', '')
var envName = '${prefix}-env'
var jobName = '${prefix}-pipeline-job'
var mcpAppName = '${prefix}-mcp-server'
var logAnalyticsName = '${prefix}-logs'
var workbookName = guid(resourceGroup().id, 'opcua-pipeline-dashboard')

// ── VNet sizing for workload-profile env + private endpoint ──────────
// 10.20.0.0/24 = 256 IPs total:
//   apps-subnet  10.20.0.0/26     (64 IPs)  ── Microsoft.App workload-profile env
//   pe-subnet    10.20.0.64/28    (16 IPs)  ── private endpoint NICs
//   spare        10.20.0.80+               ── reserved for future workloads
var vnetName = '${prefix}-vnet'
var vnetCidr = '10.20.0.0/24'
var appsSubnetName = 'apps-subnet'
var appsSubnetCidr = '10.20.0.0/26'
var peSubnetName = 'pe-subnet'
var peSubnetCidr = '10.20.0.64/28'

var containerImage = empty(pipelineImage) ? 'mcr.microsoft.com/dotnet/runtime:9.0' : pipelineImage

// ── 1. Azure AI Search ──────────────────────────────────────────────
resource search 'Microsoft.Search/searchServices@2024-06-01-preview' = {
  name: searchName
  location: location
  sku: {
    name: 'standard'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    hostingMode: 'default'
    semanticSearch: 'standard'
    replicaCount: 1
    partitionCount: 1
  }
}

// ── 2. Azure AI Foundry (AIServices account + Project) ──────────────
resource foundry 'Microsoft.CognitiveServices/accounts@2025-04-01-preview' = {
  name: foundryName
  location: location
  kind: 'AIServices'
  identity: {
    type: 'SystemAssigned'
  }
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: foundryName
    publicNetworkAccess: 'Enabled'
    allowProjectManagement: true
    disableLocalAuth: false
  }
}

resource foundryProject 'Microsoft.CognitiveServices/accounts/projects@2025-04-01-preview' = {
  parent: foundry
  name: 'default'
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    displayName: 'OPC UA Knowledge Base'
    description: 'Foundry project for the OPC UA Knowledge Base (agents, evaluations, models)'
  }
}

resource gpt4oDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: foundry
  name: 'gpt-4o'
  sku: {
    name: 'Standard'
    capacity: 30
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4o'
      version: gptModelVersion
    }
  }
}

resource embeddingDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: foundry
  name: 'text-embedding-3-large'
  sku: {
    name: 'Standard'
    capacity: 120
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'text-embedding-3-large'
      version: '1'
    }
  }
  dependsOn: [gpt4oDeployment]
}

// ── 3. Azure Blob Storage ───────────────────────────────────────────
// Shared-key auth is disabled — all clients must use Entra ID / managed
// identity (DefaultAzureCredential). The pipeline job's system-assigned
// identity is granted Storage Blob Data Contributor below.
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    // Storage uses managed identity only (allowSharedKeyAccess=false).
    // When lockdownStorage=true, public network access is fully off — only
    // private-endpoint traffic via the VNet is accepted, and every request
    // must still carry a valid Entra token for a principal holding
    // Storage Blob Data Contributor.
    publicNetworkAccess: lockdownStorage ? 'Disabled' : 'Enabled'
    networkAcls: lockdownStorage ? {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
      ipRules: []
      virtualNetworkRules: []
    } : {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
      ipRules: []
      virtualNetworkRules: []
    }
  }
}

// ── 3a. VNet + subnets for the workload-profile environment ─────────
resource vnet 'Microsoft.Network/virtualNetworks@2023-11-01' = {
  name: vnetName
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [vnetCidr]
    }
    subnets: [
      {
        name: appsSubnetName
        properties: {
          addressPrefix: appsSubnetCidr
          // Delegation to the Container Apps service. Required for
          // workload-profile env vnetConfiguration.
          delegations: [
            {
              name: 'Microsoft.App.environments'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: peSubnetName
        properties: {
          addressPrefix: peSubnetCidr
          // Private endpoint NICs need this disabled.
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

resource appsSubnet 'Microsoft.Network/virtualNetworks/subnets@2023-11-01' existing = {
  parent: vnet
  name: appsSubnetName
}

resource peSubnet 'Microsoft.Network/virtualNetworks/subnets@2023-11-01' existing = {
  parent: vnet
  name: peSubnetName
}

// ── 3b. Private DNS zone for blob endpoint ──────────────────────────
resource blobPrivateDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.blob.core.windows.net'
  location: 'global'
}

resource blobPrivateDnsZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: blobPrivateDnsZone
  name: '${vnetName}-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnet.id
    }
  }
}

// ── 3c. Private endpoint for the storage blob service ───────────────
resource storagePrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = {
  name: '${prefix}-storage-pe'
  location: location
  properties: {
    subnet: {
      id: peSubnet.id
    }
    privateLinkServiceConnections: [
      {
        name: 'blob'
        properties: {
          privateLinkServiceId: storageAccount.id
          groupIds: ['blob']
        }
      }
    ]
  }
}

// Wires the private endpoint NIC's A record into the privatelink DNS zone
// so any VNet client resolving opcuakbscstorage.blob.core.windows.net gets
// the private IP, not the public one.
resource storagePrivateEndpointDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = {
  parent: storagePrivateEndpoint
  name: 'blob-dns-zone-group'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'blob'
        properties: {
          privateDnsZoneId: blobPrivateDnsZone.id
        }
      }
    ]
  }
}

// ── 4. Azure Document Intelligence ──────────────────────────────────
resource docai 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: docaiName
  location: location
  kind: 'FormRecognizer'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: docaiName
    publicNetworkAccess: 'Enabled'
  }
}

// ── 5. Azure Container Registry ─────────────────────────────────────
resource acr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: acrName
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: true
  }
}

// ── 6. Log Analytics + Container Apps Environment ───────────────────
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource containerEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: envName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    vnetConfiguration: {
      infrastructureSubnetId: appsSubnet.id
      // External ingress: MCP server still needs a public FQDN so the
      // Hosted Agent in westus3 can reach it cross-region. Outbound to
      // the storage account flows through the private endpoint via the
      // VNet's privatelink DNS zone, never the public Internet.
      internal: false
    }
  }
}

// ── 7. Container Apps Job ───────────────────────────────────────────
resource pipelineJob 'Microsoft.App/jobs@2024-03-01' = {
  name: jobName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    environmentId: containerEnv.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Schedule'
      scheduleTriggerConfig: {
        cronExpression: cronSchedule
        parallelism: 1
        replicaCompletionCount: 1
      }
      replicaTimeout: 86400 // 24 hours
      registries: [
        {
          server: acr.properties.loginServer
          username: acr.listCredentials().username
          passwordSecretRef: 'acr-password'
        }
      ]
      secrets: concat([
        {
          name: 'acr-password'
          value: acr.listCredentials().passwords[0].value
        }
        {
          name: 'search-api-key'
          value: search.listAdminKeys().primaryKey
        }
      ], empty(cloudLibUsername) ? [] : [
        {
          name: 'cloudlib-username'
          value: cloudLibUsername
        }
        {
          name: 'cloudlib-password'
          value: cloudLibPassword
        }
      ])
    }
    template: {
      containers: [
        {
          name: 'pipeline'
          image: containerImage
          resources: {
            cpu: json('2')
            memory: '4Gi'
          }
          env: concat([
            {
              name: 'STORAGE_ACCOUNT_NAME'
              value: storageAccount.name
            }
            {
              name: 'SEARCH_ENDPOINT'
              value: 'https://${search.name}.search.windows.net'
            }
            {
              name: 'SEARCH_API_KEY'
              secretRef: 'search-api-key'
            }
            {
              name: 'AOAI_ENDPOINT'
              value: foundry.properties.endpoint
            }
          ], empty(cloudLibUsername) ? [] : [
            {
              name: 'CLOUDLIB_USERNAME'
              secretRef: 'cloudlib-username'
            }
            {
              name: 'CLOUDLIB_PASSWORD'
              secretRef: 'cloudlib-password'
            }
          ])
        }
      ]
    }
  }
}

// ── 8. MCP Server Container Apps ────────────────────────────────────
var mcpImage = empty(pipelineImage) ? 'mcr.microsoft.com/dotnet/aspnet:10.0' : '${acr.properties.loginServer}/opcua-mcp-server:latest'

resource mcpServer 'Microsoft.App/containerApps@2024-03-01' = {
  name: mcpAppName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    environmentId: containerEnv.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
      }
      registries: [
        {
          server: acr.properties.loginServer
          username: acr.listCredentials().username
          passwordSecretRef: 'acr-password'
        }
      ]
      secrets: [
        {
          name: 'acr-password'
          value: acr.listCredentials().passwords[0].value
        }
        {
          name: 'search-api-key'
          value: search.listAdminKeys().primaryKey
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'mcp-server'
          image: mcpImage
          resources: {
            cpu: json('1.0')
            memory: '2Gi'
          }
          env: [
            {
              name: 'SEARCH_ENDPOINT'
              value: 'https://${search.name}.search.windows.net'
            }
            {
              name: 'SEARCH_API_KEY'
              secretRef: 'search-api-key'
            }
            {
              name: 'AOAI_ENDPOINT'
              value: foundry.properties.endpoint
            }
            {
              name: 'STORAGE_ACCOUNT_NAME'
              // Used by NodeSetLoader to resolve nodeset_ref=blob:... and
              // by POST /upload-nodeset for content-addressed user uploads.
              value: storageAccount.name
            }
            {
              name: 'MCP_API_KEY'
              secretRef: 'search-api-key'
            }
            {
              name: 'MCP_REQUIRE_AUTH'
              value: 'false'
            }
            {
              name: 'MCP_ANON_RATE_LIMIT'
              // Higher limit to accommodate Microsoft 365 Copilot agent traffic
              // (Copilot infra shares egress IPs across tenants)
              value: '100'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 2
        rules: [
          {
            name: 'http-rule'
            http: {
              metadata: {
                concurrentRequests: '5'
              }
            }
          }
        ]
      }
    }
  }
  // The mcpServer reads/writes to the storage account exclusively through
  // the private endpoint (DNS resolved via blobPrivateDnsZone). If the PE
  // + DNS zone group don't exist yet, the very first cold start would
  // resolve to the public IP and then fail with 403 once lockdownStorage
  // is on. Force ordering.
  dependsOn: [
    storagePrivateEndpointDnsGroup
  ]
}


// ── 9. Role assignments — MIs → Foundry ─────────────────────────────
// Cognitive Services OpenAI User (data-plane access to model deployments)
var cognitiveServicesOpenAIUserRole = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
)

resource searchFoundryRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, foundry.id, cognitiveServicesOpenAIUserRole)
  scope: foundry
  properties: {
    principalId: search.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: cognitiveServicesOpenAIUserRole
  }
}

resource pipelineJobFoundryRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(pipelineJob.id, foundry.id, cognitiveServicesOpenAIUserRole)
  scope: foundry
  properties: {
    principalId: pipelineJob.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: cognitiveServicesOpenAIUserRole
  }
}

resource mcpServerFoundryRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(mcpServer.id, foundry.id, cognitiveServicesOpenAIUserRole)
  scope: foundry
  properties: {
    principalId: mcpServer.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: cognitiveServicesOpenAIUserRole
  }
}

// Storage Blob Data Contributor — pipeline job MI → storage account.
// Required because the storage account has allowSharedKeyAccess=false;
// the pipeline authenticates with DefaultAzureCredential at runtime.
var storageBlobDataContributorRole = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
)

resource pipelineJobStorageBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(pipelineJob.id, storageAccount.id, 'StorageBlobDataContributor')
  scope: storageAccount
  properties: {
    principalId: pipelineJob.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageBlobDataContributorRole
  }
}

// MCP server also needs Storage Blob Data Contributor — it reads NodeSets
// referenced via `nodeset_ref=blob:...` and writes content-addressed user
// uploads to `uploads/{sha256}.xml` via the POST /upload-nodeset endpoint.
resource mcpServerStorageBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(mcpServer.id, storageAccount.id, 'StorageBlobDataContributor')
  scope: storageAccount
  properties: {
    principalId: mcpServer.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageBlobDataContributorRole
  }
}


// Lifecycle policy — auto-delete user uploads after 1 day to keep the
// privacy footprint minimal. Pipeline-written paths (nodesets/, html/,
// sts-xml/, markdown/, images/, api/) are untouched.
resource storageLifecyclePolicy 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = {
  parent: storageAccount
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'expire-uploaded-nodesets'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: {
              blobTypes: ['blockBlob']
              prefixMatch: ['opcua-content/uploads/']
            }
            actions: {
              baseBlob: {
                delete: {
                  daysAfterCreationGreaterThan: 1
                }
              }
            }
          }
        }
      ]
    }
  }
}

// ── 10. Azure Monitor Workbook ───────────────────────────────────────
var workbookContent = '''
{"version":"Notebook/1.0","items":[{"type":1,"content":{"json":"# OPC UA Knowledge Base Pipeline Dashboard\n\nMonitors crawl + index pipeline for reference.opcfoundation.org"},"name":"header"},{"type":3,"content":{"version":"KqlItem/1.0","query":"ContainerAppConsoleLogs_CL\n| where ContainerGroupName_s startswith \"opcua-pipeline-job\"\n| where Log_s has \"[PIPELINE]\"\n| parse Log_s with * \"Phase=\" Phase:string \" Status=\" Status:string \" \" *\n| project TimeGenerated, Phase, Status\n| order by TimeGenerated desc\n| take 20","size":1,"title":"Recent Pipeline Events","timeContext":{"durationMs":604800000},"queryType":0,"resourceType":"microsoft.operationalinsights/workspaces"},"name":"pipeline-events"},{"type":3,"content":{"version":"KqlItem/1.0","query":"ContainerAppConsoleLogs_CL\n| where ContainerGroupName_s startswith \"opcua-pipeline-job\"\n| where Log_s has \"[CRAWL]\" and Log_s has \"Downloaded=\"\n| parse Log_s with * \"Downloaded=\" Downloaded:long \" Skipped=\" Skipped:long \" Errors=\" Errors:long \" Queued=\" Queued:long\n| project TimeGenerated, Downloaded, Skipped, Errors, Queued\n| order by TimeGenerated asc","size":0,"title":"Crawl Progress Over Time","timeContext":{"durationMs":86400000},"queryType":0,"resourceType":"microsoft.operationalinsights/workspaces","visualization":"linechart","chartSettings":{"yAxis":["Downloaded","Queued","Errors"]}},"name":"crawl-progress"},{"type":3,"content":{"version":"KqlItem/1.0","query":"ContainerAppConsoleLogs_CL\n| where ContainerGroupName_s startswith \"opcua-pipeline-job\"\n| where Log_s has \"[CRAWL]\" and Log_s has \"Downloaded=\"\n| parse Log_s with * \"Downloaded=\" Downloaded:long \" Skipped=\" Skipped:long \" Errors=\" Errors:long *\n| summarize MaxDownloaded=max(Downloaded), MaxSkipped=max(Skipped), TotalErrors=max(Errors) by bin(TimeGenerated, 1h)\n| order by TimeGenerated desc\n| take 1","size":4,"title":"Latest Crawl Stats","timeContext":{"durationMs":86400000},"queryType":0,"resourceType":"microsoft.operationalinsights/workspaces","visualization":"tiles","tileSettings":{"showBorder":true}},"name":"crawl-stats"},{"type":3,"content":{"version":"KqlItem/1.0","query":"ContainerAppConsoleLogs_CL\n| where ContainerGroupName_s startswith \"opcua-pipeline-job\"\n| where Log_s has \"[INDEX]\" and Log_s has \"Phase=\"\n| parse Log_s with * \"Phase=\" Phase:string \" \" *\n| extend Embedded = extract(\"Embedded=([0-9]+)\", 1, Log_s)\n| extend Uploaded = extract(\"Uploaded=([0-9]+)\", 1, Log_s)\n| extend Chunks = extract(\"Chunks=([0-9]+)\", 1, Log_s)\n| extend Parsed = extract(\"Parsed=([0-9]+)\", 1, Log_s)\n| project TimeGenerated, Phase, Parsed, Chunks, Embedded, Uploaded\n| order by TimeGenerated asc","size":0,"title":"Index Progress Over Time","timeContext":{"durationMs":86400000},"queryType":0,"resourceType":"microsoft.operationalinsights/workspaces","visualization":"linechart"},"name":"index-progress"},{"type":3,"content":{"version":"KqlItem/1.0","query":"ContainerAppConsoleLogs_CL\n| where ContainerGroupName_s startswith \"opcua-pipeline-job\"\n| where Log_s has \"Error=\" or Log_s has \"error\" or Log_s has \"Warning\"\n| project TimeGenerated, Log_s\n| order by TimeGenerated desc\n| take 50","size":1,"title":"Errors & Warnings","timeContext":{"durationMs":604800000},"queryType":0,"resourceType":"microsoft.operationalinsights/workspaces","visualization":"table"},"name":"errors"},{"type":3,"content":{"version":"KqlItem/1.0","query":"ContainerAppConsoleLogs_CL\n| where ContainerGroupName_s startswith \"opcua-pipeline-job\"\n| where Log_s has \"[PIPELINE]\" and Log_s has \"TotalElapsedSec=\"\n| parse Log_s with * \"TotalElapsedSec=\" ElapsedSec:long\n| project TimeGenerated, DurationMin=ElapsedSec/60.0\n| order by TimeGenerated desc\n| take 10","size":1,"title":"Execution History (Duration in Minutes)","timeContext":{"durationMs":2592000000},"queryType":0,"resourceType":"microsoft.operationalinsights/workspaces","visualization":"barchart"},"name":"exec-history"}],"isLocked":false}
'''

resource workbook 'Microsoft.Insights/workbooks@2022-04-01' = {
  name: workbookName
  location: location
  kind: 'shared'
  properties: {
    displayName: 'OPC UA Pipeline Dashboard'
    category: 'workbook'
    sourceId: logAnalytics.id
    serializedData: workbookContent
  }
}

// ── Outputs ─────────────────────────────────────────────────────────

output searchEndpoint string = 'https://${search.name}.search.windows.net'

@secure()
output searchApiKey string = search.listAdminKeys().primaryKey

output foundryEndpoint string = foundry.properties.endpoint

output foundryProjectEndpoint string = 'https://${foundry.name}.services.ai.azure.com/api/projects/${foundryProject.name}'

// Backward-compat: aoaiEndpoint still emitted (same value as foundryEndpoint)
output aoaiEndpoint string = foundry.properties.endpoint

output storageAccountName string = storageAccount.name

output acrLoginServer string = acr.properties.loginServer

output mcpEndpoint string = 'https://${search.name}.search.windows.net/knowledgebases/${prefix}-kb/mcp?api-version=2025-11-01-preview'

output mcpServerEndpoint string = 'https://${mcpServer.properties.configuration.ingress.fqdn}'
output vnetId string = vnet.id
output storagePrivateEndpointId string = storagePrivateEndpoint.id
