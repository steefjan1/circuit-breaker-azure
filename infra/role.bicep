// One role assignment for a managed identity at resource group scope.
// Kept in its own module so the principal id is resolved before the assignment is evaluated.

@description('Principal (object) id of the managed identity.')
param principalId string

@description('Role definition GUID.')
param roleDefinitionId string

resource assignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, principalId, roleDefinitionId)
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleDefinitionId)
  }
}
