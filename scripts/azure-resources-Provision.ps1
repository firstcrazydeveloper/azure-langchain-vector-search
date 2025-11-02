param(
  [string]$ResourceGroup = "rg-vector-pipeline",
  [string]$Location = "westeurope",
  [string]$StorageName = ("stvec{0}" -f (Get-Random)),
  [string]$SearchName = ("searchvec{0}" -f (Get-Random)),
  [string]$AoaiName = ("aoaivec{0}" -f (Get-Random)),
  [string]$Container = "documents"
)

Write-Host "Creating resource group..." -ForegroundColor Cyan
az group create -n $ResourceGroup -l $Location | Out-Null

Write-Host "Creating Storage account: $StorageName" -ForegroundColor Cyan
az storage account create -g $ResourceGroup -n $StorageName -l $Location --sku Standard_LRS | Out-Null
$StorageConn = az storage account show-connection-string -g $ResourceGroup -n $StorageName -o tsv
az storage container create --name $Container --connection-string $StorageConn | Out-Null

Write-Host "Creating Cognitive Search: $SearchName" -ForegroundColor Cyan
az search service create --name $SearchName -g $ResourceGroup --sku Basic --location $Location | Out-Null
$SearchKey = az search admin-key show -n $SearchName -g $ResourceGroup --query primaryKey -o tsv
$SearchEndpoint = "https://$SearchName.search.windows.net"

Write-Host "Creating Azure OpenAI: $AoaiName" -ForegroundColor Cyan
az cognitiveservices account create `
  -g $ResourceGroup -n $AoaiName -l $Location `
  --kind OpenAI --sku S0 --yes | Out-Null

$AoaiKey = az cognitiveservices account keys list -g $ResourceGroup -n $AoaiName --query key1 -o tsv
$AoaiEndpoint = az cognitiveservices account show -g $ResourceGroup -n $AoaiName --query properties.endpoint -o tsv

Write-Host "Deploy an embeddings model in Azure OpenAI Studio and note the deployment name (e.g., text-embedding-3-small)." -ForegroundColor Yellow

# Output a JSON you can feed next script
$Out = [pscustomobject]@{
  ResourceGroup      = $ResourceGroup
  Location           = $Location
  StorageAccount     = $StorageName
  StorageConnection  = $StorageConn
  BlobContainer      = $Container
  SearchName         = $SearchName
  SearchEndpoint     = $SearchEndpoint
  SearchAdminKey     = $SearchKey
  AoaiName           = $AoaiName
  AoaiEndpoint       = $AoaiEndpoint
  AoaiKey            = $AoaiKey
}
$Out | ConvertTo-Json -Depth 5 | Set-Content -Path ./provision-output.json -Encoding UTF8

Write-Host "`nProvision complete. See provision-output.json." -ForegroundColor Green
