@description('Location for all resources')
param location string = 'eastus'

@description('Storage account name (must be globally unique)')
param storageAccountName string = 'simairavec${uniqueString(resourceGroup().id)}'

@description('Azure Cognitive Search service name')
param searchServiceName string = 'simaira-search-service-${uniqueString(resourceGroup().id)}'

@description('Azure OpenAI service name')
param openAiName string = 'simaira-openai-service-${uniqueString(resourceGroup().id)}'

@description('Blob container name')
param containerName string = 'documents'

@description('SKU for Cognitive Search')
param searchSku string = 'basic'

@description('SKU for Azure OpenAI')
param openAiSku string = 'S0'

@description('Replication type for Storage Account')
param storageSku string = 'Standard_LRS'

/* ───────────────────────────────
   STORAGE ACCOUNT
────────────────────────────────*/
/* Storage account */
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: storageSku
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
  }
}

/* The implicit “default” blob service under the account */
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
}

/* Container parented to the blob service */
resource blobContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: containerName
  properties: {
    publicAccess: 'None'
  }
}

/* ───────────────────────────────
   AZURE COGNITIVE SEARCH
────────────────────────────────*/
resource searchService 'Microsoft.Search/searchServices@2023-11-01' = {
  name: searchServiceName
  location: location
  sku: {
    name: searchSku
  }
  properties: {
    partitionCount: 1
    replicaCount: 1
    hostingMode: 'default'
  }
}

/* ───────────────────────────────
   AZURE COGNITIVE SERVICES (OpenAI)
────────────────────────────────*/
resource openAiService 'Microsoft.CognitiveServices/accounts@2025-06-01'= {
  name: openAiName
  location: location
  sku: {
    name: openAiSku
  }
  kind: 'OpenAI'
  properties: {
    customSubDomainName: openAiName
    publicNetworkAccess: 'Enabled'
  }
}

/* ───────────────────────────────
   OUTPUTS
────────────────────────────────*/
output storageAccountName string = storageAccount.name
output storageConnectionString string = 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};EndpointSuffix=${environment().suffixes.storage};AccountKey='
output blobContainerName string = blobContainer.name

output searchServiceName string = searchService.name
output searchEndpoint string = 'https://${searchService.name}.search.windows.net'

output openAiName string = openAiService.name
output openAiEndpoint string = 'https://${openAiService.name}.openai.azure.com'

output location string = location
