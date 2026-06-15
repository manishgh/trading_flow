targetScope = 'resourceGroup'

@description('Environment name used in resource names, for example paper, prod, or research.')
param environmentName string = 'paper'

@description('Azure region.')
param location string = resourceGroup().location

@description('AKS Kubernetes version. Empty means Azure chooses the default supported version.')
param kubernetesVersion string = ''

@description('System node pool VM size.')
param systemNodeVmSize string = 'Standard_D4s_v5'

@description('Trading worker node pool VM size.')
param tradingNodeVmSize string = 'Standard_D8s_v5'

@description('Minimum worker node count for paper/live ticker-shard pods.')
param tradingNodeMinCount int = 2

@description('Maximum worker node count for paper/live ticker-shard pods.')
param tradingNodeMaxCount int = 5

@description('Storage account replication SKU.')
@allowed([
  'Standard_LRS'
  'Standard_ZRS'
])
param storageSku string = 'Standard_ZRS'

var cleanEnvironment = toLower(replace(environmentName, '-', ''))
var unique = uniqueString(resourceGroup().id, cleanEnvironment)
var aksName = 'aks-tradingflow-${environmentName}'
var acrName = take('tf${cleanEnvironment}${unique}acr', 50)
var keyVaultName = take('kv-tf-${cleanEnvironment}-${unique}', 24)
var storageName = take('tf${cleanEnvironment}${unique}st', 24)
var lawName = 'law-tradingflow-${environmentName}'
var webIdentityName = 'id-tradingflow-${environmentName}-web'
var paperIdentityName = 'id-tradingflow-${environmentName}-paper'
var liveIdentityName = 'id-tradingflow-${environmentName}-live'

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: lawName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 90
  }
}

resource acr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: acrName
  location: location
  sku: {
    name: 'Standard'
  }
  properties: {
    adminUserEnabled: false
    anonymousPullEnabled: false
    dataEndpointEnabled: false
    networkRuleBypassOptions: 'AzureServices'
    policies: {
      quarantinePolicy: {
        status: 'disabled'
      }
      retentionPolicy: {
        days: 14
        status: 'enabled'
      }
      trustPolicy: {
        status: 'disabled'
        type: 'Notary'
      }
    }
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  sku: {
    name: storageSku
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource resultsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  name: '${storage.name}/default/tradingflow-results'
  properties: {
    publicAccess: 'None'
  }
}

resource candleContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  name: '${storage.name}/default/tradingflow-candles'
  properties: {
    publicAccess: 'None'
  }
}

resource runtimeContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  name: '${storage.name}/default/tradingflow-runtime'
  properties: {
    publicAccess: 'None'
  }
}

resource dataShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  name: '${storage.name}/default/tradingflow-data'
  properties: {
    shareQuota: 512
    enabledProtocols: 'SMB'
  }
}

resource uiRunsShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  name: '${storage.name}/default/tradingflow-ui-runs'
  properties: {
    shareQuota: 64
    enabledProtocols: 'SMB'
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  properties: {
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enablePurgeProtection: true
    enableRbacAuthorization: true
    enabledForDeployment: false
    enabledForDiskEncryption: false
    enabledForTemplateDeployment: false
    softDeleteRetentionInDays: 90
    publicNetworkAccess: 'Enabled'
  }
}

resource webIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: webIdentityName
  location: location
}

resource paperIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: paperIdentityName
  location: location
}

resource liveIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: liveIdentityName
  location: location
}

resource aks 'Microsoft.ContainerService/managedClusters@2024-09-01' = {
  name: aksName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    kubernetesVersion: empty(kubernetesVersion) ? null : kubernetesVersion
    dnsPrefix: 'tradingflow-${environmentName}'
    oidcIssuerProfile: {
      enabled: true
    }
    securityProfile: {
      workloadIdentity: {
        enabled: true
      }
    }
    addonProfiles: {
      omsagent: {
        enabled: true
        config: {
          logAnalyticsWorkspaceResourceID: logAnalytics.id
        }
      }
      azureKeyvaultSecretsProvider: {
        enabled: true
        config: {
          enableSecretRotation: 'true'
          rotationPollInterval: '2m'
        }
      }
    }
    agentPoolProfiles: [
      {
        name: 'system'
        mode: 'System'
        count: 1
        vmSize: systemNodeVmSize
        osType: 'Linux'
        osSKU: 'Ubuntu'
        type: 'VirtualMachineScaleSets'
        enableAutoScaling: true
        minCount: 1
        maxCount: 3
        maxPods: 60
      }
      {
        name: 'trading'
        mode: 'User'
        count: tradingNodeMinCount
        vmSize: tradingNodeVmSize
        osType: 'Linux'
        osSKU: 'Ubuntu'
        type: 'VirtualMachineScaleSets'
        enableAutoScaling: true
        minCount: tradingNodeMinCount
        maxCount: tradingNodeMaxCount
        maxPods: 60
        nodeLabels: {
          workload: 'trading'
        }
        nodeTaints: [
          'workload=trading:NoSchedule'
        ]
      }
    ]
    networkProfile: {
      networkPlugin: 'azure'
      networkPluginMode: 'overlay'
      networkPolicy: 'azure'
      loadBalancerSku: 'standard'
      outboundType: 'loadBalancer'
    }
  }
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aks.id, acr.id, 'AcrPull')
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: aks.properties.identityProfile.kubeletidentity.objectId
    principalType: 'ServicePrincipal'
  }
}

resource kubeletDataShareContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(dataShare.id, aks.properties.identityProfile.kubeletidentity.objectId, 'StorageFileDataSmbShareContributor')
  scope: dataShare
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '0c867c2a-1d8c-454a-a3db-ab2ea1bdc8bb')
    principalId: aks.properties.identityProfile.kubeletidentity.objectId
    principalType: 'ServicePrincipal'
  }
}

resource kubeletUiRunsShareContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(uiRunsShare.id, aks.properties.identityProfile.kubeletidentity.objectId, 'StorageFileDataSmbShareContributor')
  scope: uiRunsShare
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '0c867c2a-1d8c-454a-a3db-ab2ea1bdc8bb')
    principalId: aks.properties.identityProfile.kubeletidentity.objectId
    principalType: 'ServicePrincipal'
  }
}

resource webStorageBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, webIdentity.id, 'StorageBlobDataContributor')
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: webIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource paperStorageBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, paperIdentity.id, 'StorageBlobDataContributor')
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: paperIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource liveStorageBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, liveIdentity.id, 'StorageBlobDataContributor')
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: liveIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource webKeyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, webIdentity.id, 'KeyVaultSecretsUser')
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: webIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource paperKeyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, paperIdentity.id, 'KeyVaultSecretsUser')
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: paperIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource liveKeyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, liveIdentity.id, 'KeyVaultSecretsUser')
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: liveIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output aksName string = aks.name
output resourceGroupName string = resourceGroup().name
output acrLoginServer string = acr.properties.loginServer
output keyVaultName string = keyVault.name
output storageAccountName string = storage.name
output oidcIssuerUrl string = aks.properties.oidcIssuerProfile.issuerURL
output webIdentityClientId string = webIdentity.properties.clientId
output paperIdentityClientId string = paperIdentity.properties.clientId
output liveIdentityClientId string = liveIdentity.properties.clientId
output webIdentityPrincipalId string = webIdentity.properties.principalId
output paperIdentityPrincipalId string = paperIdentity.properties.principalId
output liveIdentityPrincipalId string = liveIdentity.properties.principalId
