$ResourceGroup = "simaira-rg-vector-pipeline-test"
$Location = "eastus"

az group create -n $ResourceGroup -l $Location | Out-Null

Write-Host "Deploying Bicep..." -ForegroundColor Cyan

$OutputsJson  = az deployment group create `
  -g $ResourceGroup `
  -f ./provision.bicep `
  -p location=$Location `
  --query properties.outputs `
  -o json

$Outputs = $OutputsJson | ConvertFrom-Json
Write-Host "Bicep deployed..." -ForegroundColor Green

# 2) Extract values we need from outputs
$searchName = $Outputs.searchServiceName.value
$aoaiName   = $Outputs.openAiName.value

# 3) Fetch keys (post-deploy, via CLI)
Write-Host "Fetching API keys..." -ForegroundColor Cyan
$SearchKey = az search admin-key show -g $ResourceGroup -n $searchName --query primaryKey -o tsv
$AoaiKey   = az cognitiveservices account keys list -g $ResourceGroup -n $aoaiName --query key1 -o tsv
Write-Host "Got API keys..." -ForegroundColor Green

# 4) Build a flat object with all values + keys
Write-Host "Building output file ..." -ForegroundColor Cyan
$Out = [ordered]@{
  ResourceGroup     = $ResourceGroup
  Location          = $Outputs.location.value
  StorageAccount    = $Outputs.storageAccountName.value
  BlobContainer     = $Outputs.blobContainerName.value
  StorageConnectionString = $Outputs.storageConnectionString.value
  SearchName        = $searchName
  SearchEndpoint    = $Outputs.searchEndpoint.value
  SearchAdminKey    = $SearchKey
  AoaiName          = $aoaiName
  AoaiEndpoint      = $Outputs.openAiEndpoint.value
  AoaiKey           = $AoaiKey
}

$File = "./provision-output.json"
($Out | ConvertTo-Json -Depth 5) | Set-Content -Path $File -Encoding UTF8
Write-Host "Created output file ..." -ForegroundColor Green

Write-Host "`n✅ Deployment complete. Outputs saved to $File" -ForegroundColor Green
