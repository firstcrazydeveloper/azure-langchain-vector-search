param(
  [string]$Query = "termination clause europe",
  [int]$K = 5
)

$Url = "http://localhost:8080/search?q=$( [uri]::EscapeDataString($Query) )&k=$K"
Write-Host "GET $Url" -ForegroundColor Cyan

try {
  $res = Invoke-RestMethod -Uri $Url -TimeoutSec 30
  $res | ConvertTo-Json -Depth 6
} catch {
  Write-Error $_
}
