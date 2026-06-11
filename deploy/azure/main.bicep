targetScope = 'resourceGroup'

@description('Short lowercase deployment prefix, for example tradingflowdev.')
param prefix string

@description('Azure region.')
param location string = resourceGroup().location

@description('Container image for TradingFlow.Web.')
param webImage string

@description('Container image for the C# TradingFlow service/CLI worker.')
param tradingServiceImage string

@description('Container image for the Go news sentiment service.')
param newsImage string

@description('Minimum web replicas.')
param webMinReplicas int = 1

@description('Maximum web replicas.')
param webMaxReplicas int = 3

var safePrefix = toLower(replace(prefix, '-', ''))
var storageName = toLower(take('${safePrefix}${uniqueString(resourceGroup().id)}', 24))

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefix}-law'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
  }
}

resource dataShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  name: '${storage.name}/default/tradingflow-data'
  properties: {}
}

resource uiRunsShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  name: '${storage.name}/default/tradingflow-ui-runs'
  properties: {}
}

resource acaEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${prefix}-aca-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

resource dataVolume 'Microsoft.App/managedEnvironments/storages@2024-03-01' = {
  parent: acaEnv
  name: 'tradingflow-data'
  properties: {
    azureFile: {
      accountName: storage.name
      accountKey: storage.listKeys().keys[0].value
      shareName: dataShare.name
      accessMode: 'ReadWrite'
    }
  }
}

resource uiRunsVolume 'Microsoft.App/managedEnvironments/storages@2024-03-01' = {
  parent: acaEnv
  name: 'tradingflow-ui-runs'
  properties: {
    azureFile: {
      accountName: storage.name
      accountKey: storage.listKeys().keys[0].value
      shareName: uiRunsShare.name
      accessMode: 'ReadWrite'
    }
  }
}

resource newsApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-news'
  location: location
  properties: {
    managedEnvironmentId: acaEnv.id
    configuration: {
      ingress: {
        external: false
        targetPort: 8080
      }
    }
    template: {
      containers: [
        {
          name: 'news'
          image: newsImage
          env: [
            { name: 'PORT', value: '8080' }
            { name: 'NEWS_ENABLED', value: 'true' }
            { name: 'NEWS_WORKER_COUNT', value: '4' }
            { name: 'NEWS_CHANNEL_BUFFER', value: '1024' }
          ]
          resources: {
            cpu: 0.5
            memory: '1Gi'
          }
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 2
      }
    }
  }
}

resource webApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-web'
  location: location
  properties: {
    managedEnvironmentId: acaEnv.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
    }
    template: {
      containers: [
        {
          name: 'web'
          image: webImage
          env: [
            { name: 'ASPNETCORE_URLS', value: 'http://+:8080' }
          ]
          volumeMounts: [
            { volumeName: 'data', mountPath: '/app/data' }
            { volumeName: 'ui-runs', mountPath: '/app/configs/backtest/ui-runs' }
          ]
          resources: {
            cpu: 1.0
            memory: '2Gi'
          }
        }
      ]
      volumes: [
        { name: 'data', storageType: 'AzureFile', storageName: dataVolume.name }
        { name: 'ui-runs', storageType: 'AzureFile', storageName: uiRunsVolume.name }
      ]
      scale: {
        minReplicas: webMinReplicas
        maxReplicas: webMaxReplicas
      }
    }
  }
}

resource tradingJob 'Microsoft.App/jobs@2024-03-01' = {
  name: '${prefix}-trading-job'
  location: location
  properties: {
    environmentId: acaEnv.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 3600
      replicaRetryLimit: 1
    }
    template: {
      containers: [
        {
          name: 'trading-service'
          image: tradingServiceImage
          args: [
            'configs/backtest/semiconductors-research.yaml'
          ]
          volumeMounts: [
            { volumeName: 'data', mountPath: '/app/data' }
          ]
          resources: {
            cpu: 1.0
            memory: '2Gi'
          }
        }
      ]
      volumes: [
        { name: 'data', storageType: 'AzureFile', storageName: dataVolume.name }
      ]
    }
  }
}

output webUrl string = 'https://${webApp.properties.configuration.ingress.fqdn}'
output newsInternalFqdn string = newsApp.properties.configuration.ingress.fqdn
output tradingJobName string = tradingJob.name
