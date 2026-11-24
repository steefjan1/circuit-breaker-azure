targetScope = 'subscription'

@minLength(1)
@description('Name of the azd environment. Used to derive resource names.')
param environmentName string

@minLength(1)
@description('Azure region for all resources.')
param location string

@description('Deploy API Management (Basic v2) for the gateway circuit breaker path. Adds a few minutes and a fixed hourly cost.')
param deployApim bool = true

@description('Publisher e-mail that API Management requires.')
param apimPublisherEmail string = 'noreply@example.com'

var tags = { 'azd-env-name': environmentName }
var token = toLower(uniqueString(subscription().id, environmentName, location))
var prefix = take(replace(toLower(environmentName), '_', '-'), 12)

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-${environmentName}'
  location: location
  tags: tags
}

module resources 'resources.bicep' = {
  name: 'resources'
  scope: rg
  params: {
    location: location
    tags: tags
    prefix: prefix
    token: token
    deployApim: deployApim
    apimPublisherEmail: apimPublisherEmail
  }
}

output AZURE_LOCATION string = location
output AZURE_RESOURCE_GROUP string = rg.name
output ORDERS_URL string = resources.outputs.ordersUrl
output PAYMENT_URL string = resources.outputs.paymentUrl
output PAYMENT_GATEWAY_URL string = resources.outputs.paymentGatewayUrl
output APPLICATIONINSIGHTS_NAME string = resources.outputs.appInsightsName
