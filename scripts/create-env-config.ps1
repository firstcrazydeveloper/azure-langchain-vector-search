param(
  [string]$ProvisionJson = "./provision-output.json",
  [string]$EmbeddingDeployment = "text-embedding-3-small", # must match your AOAI deployment name
  [string]$EnvPath = "../.env"
)

if (-not (Test-Path $ProvisionJson)) {
  throw "Run 00-Provision.ps1 first; $ProvisionJson not found."
}
$prov = Get-Content $ProvisionJson | ConvertFrom-Json

@"
AZURE_SEARCH_ENDPOINT=$($prov.SearchEndpoint)
AZURE_SEARCH_API_KEY=$($prov.SearchAdminKey)
AZURE_SEARCH_INDEX=docs-index

AZURE_OPENAI_ENDPOINT=$($prov.AoaiEndpoint)
AZURE_OPENAI_API_KEY=$($prov.AoaiKey)
AZURE_OPENAI_EMBEDDING_DEPLOYMENT=$EmbeddingDeployment

AZURE_BLOB_CONNECTION_STRING=$($prov.StorageConnectionString)
AZURE_BLOB_CONTAINER=$($prov.BlobContainer)

APP_PORT=8080
"@ | Set-Content -Path $EnvPath -Encoding UTF8

Write-Host ".env created at $EnvPath" -ForegroundColor Green
