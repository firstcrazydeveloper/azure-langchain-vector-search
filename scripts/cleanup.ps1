param([string]$ResourceGroup = "rg-vector-pipeline")

$confirm = Read-Host "Delete resource group '$ResourceGroup'? type YES to continue"
if ($confirm -eq "YES") {
  az group delete -n $ResourceGroup --yes --no-wait
  Write-Host "Delete submitted." -ForegroundColor Yellow
} else {
  Write-Host "Aborted."
}
