param location string
param tags object
param prefix string
param token string
param deployApim bool
param apimPublisherEmail string

var pendingQueueName = 'payments-pending'

// Built-in role definition ids.
var roles = {
  storageBlobDataOwner: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  storageQueueDataContributor: '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  storageTableDataContributor: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
  serviceBusDataOwner: '090c5cfd-751d-490a-894a-3ce6f1109419'
  monitoringMetricsPublisher: '3913510d-42f4-4e42-8a64-420c390055eb'
}

// ---------- Identity ----------

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${prefix}-${token}'
  location: location
  tags: tags
}

module roleAssignments 'role.bicep' = [
  for role in items(roles): {
    name: 'role-${role.key}'
    params: {
      principalId: identity.properties.principalId
      roleDefinitionId: role.value
    }
  }
]

// ---------- Monitoring ----------

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${prefix}-${token}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${prefix}-${token}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

// ---------- Storage (host storage, deployment packages, demo tables) ----------

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: take('st${replace(prefix, '-', '')}${token}', 24)
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
  }

  resource blob 'blobServices' = {
    name: 'default'

    resource ordersPackages 'containers' = {
      name: 'deploy-orders'
    }

    resource paymentPackages 'containers' = {
      name: 'deploy-payment'
    }
  }
}

// ---------- Service Bus (parked payments) ----------

resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: 'sb-${prefix}-${token}'
  location: location
  tags: tags
  sku: { name: 'Standard', tier: 'Standard' }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
  }

  resource pending 'queues' = {
    name: pendingQueueName
    properties: {
      // The default. With an open breaker and a consumer that throws, ten
      // deliveries are used up in about a second.
      maxDeliveryCount: 10
      lockDuration: 'PT1M'
      deadLetteringOnMessageExpiration: true
      defaultMessageTimeToLive: 'P1D'
    }
  }
}

// ---------- Function Apps (Flex Consumption: one plan per app) ----------

var commonSettings = [
  { name: 'AzureWebJobsStorage__accountName', value: storage.name }
  { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
  { name: 'AzureWebJobsStorage__clientId', value: identity.properties.clientId }
  { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
  {
    name: 'APPLICATIONINSIGHTS_AUTHENTICATION_STRING'
    value: 'ClientId=${identity.properties.clientId};Authorization=AAD'
  }
  { name: 'TABLE_ENDPOINT', value: storage.properties.primaryEndpoints.table }
]

resource paymentPlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'plan-${prefix}-payment-${token}'
  location: location
  tags: tags
  kind: 'functionapp'
  sku: { tier: 'FlexConsumption', name: 'FC1' }
  properties: { reserved: true }
}

resource paymentApp 'Microsoft.Web/sites@2024-04-01' = {
  name: 'func-${prefix}-payment-${token}'
  location: location
  tags: union(tags, { 'azd-service-name': 'payment' })
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  properties: {
    serverFarmId: paymentPlan.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}deploy-payment'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: identity.id
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: 40
        instanceMemoryMB: 2048
      }
      runtime: { name: 'dotnet-isolated', version: '8.0' }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      appSettings: commonSettings
    }
  }
  dependsOn: [roleAssignments, storage::blob::paymentPackages]
}

// ---------- API Management (optional): the breaker at the gateway ----------

resource apim 'Microsoft.ApiManagement/service@2024-05-01' = if (deployApim) {
  name: 'apim-${prefix}-${token}'
  location: location
  tags: tags
  sku: { name: 'BasicV2', capacity: 1 }
  properties: {
    publisherEmail: apimPublisherEmail
    publisherName: 'Circuit breaker demo'
  }
}

resource paymentBackend 'Microsoft.ApiManagement/service/backends@2024-05-01' = if (deployApim) {
  parent: apim
  name: 'payment-stub'
  properties: {
    url: 'https://${paymentApp.properties.defaultHostName}/api'
    protocol: 'http'
    circuitBreaker: {
      // API Management allows one rule per backend.
      rules: [
        {
          name: 'paymentBreaker'
          failureCondition: {
            // Five 5xx answers within ten seconds trip the circuit.
            count: 5
            interval: 'PT10S'
            statusCodeRanges: [
              { min: 500, max: 599 }
            ]
          }
          // No half-open state here: after the trip duration all traffic resumes.
          tripDuration: 'PT15S'
          acceptRetryAfter: false
        }
      ]
    }
  }
}

resource paymentApi 'Microsoft.ApiManagement/service/apis@2024-05-01' = if (deployApim) {
  parent: apim
  name: 'payments'
  properties: {
    displayName: 'Payments'
    path: 'payments'
    protocols: ['https']
    // Demo only: no subscription key. Do not copy this into a real gateway.
    subscriptionRequired: false
  }
}

resource chargeOperation 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = if (deployApim) {
  parent: paymentApi
  name: 'charge'
  properties: {
    displayName: 'Charge'
    method: 'POST'
    urlTemplate: '/charge'
  }
}

resource paymentApiPolicy 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = if (deployApim) {
  parent: paymentApi
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: loadTextContent('apim-policy.xml')
  }
  dependsOn: [paymentBackend]
}

// ---------- Orders app ----------

resource ordersPlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'plan-${prefix}-orders-${token}'
  location: location
  tags: tags
  kind: 'functionapp'
  sku: { tier: 'FlexConsumption', name: 'FC1' }
  properties: { reserved: true }
}

resource ordersApp 'Microsoft.Web/sites@2024-04-01' = {
  name: 'func-${prefix}-orders-${token}'
  location: location
  tags: union(tags, { 'azd-service-name': 'orders' })
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  properties: {
    serverFarmId: ordersPlan.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}deploy-orders'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: identity.id
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: 40
        instanceMemoryMB: 2048
      }
      runtime: { name: 'dotnet-isolated', version: '8.0' }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      appSettings: concat(commonSettings, [
        { name: 'PAYMENT_URL', value: 'https://${paymentApp.properties.defaultHostName}/api/' }
        { name: 'PAYMENT_GATEWAY_URL', value: deployApim ? '${apim.properties.gatewayUrl}/payments/' : '' }
        { name: 'ServiceBusConnection__fullyQualifiedNamespace', value: '${serviceBus.name}.servicebus.windows.net' }
        { name: 'ServiceBusConnection__credential', value: 'managedidentity' }
        { name: 'ServiceBusConnection__clientId', value: identity.properties.clientId }
        { name: 'PendingQueueName', value: pendingQueueName }
        // Demo values: small numbers so the circuit trips and recovers in seconds.
        { name: 'BREAKER_FAILURE_RATIO', value: '0.5' }
        { name: 'BREAKER_MINIMUM_THROUGHPUT', value: '10' }
        { name: 'BREAKER_SAMPLING_SECONDS', value: '10' }
        { name: 'BREAKER_BREAK_SECONDS', value: '15' }
        { name: 'PAYMENT_ATTEMPT_TIMEOUT_SECONDS', value: '2' }
        { name: 'SETTLE_MAX_ATTEMPTS', value: '20' }
      ])
    }
  }
  dependsOn: [roleAssignments, storage::blob::ordersPackages, serviceBus::pending]
}

output ordersUrl string = 'https://${ordersApp.properties.defaultHostName}/api'
output paymentUrl string = 'https://${paymentApp.properties.defaultHostName}/api'
output paymentGatewayUrl string = deployApim ? '${apim.properties.gatewayUrl}/payments' : ''
output appInsightsName string = appInsights.name
