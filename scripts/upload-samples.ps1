param(
  [string]$ProvisionJson = "./provision-output.json"
)

$prov = Get-Content $ProvisionJson | ConvertFrom-Json
$Conn = $prov.StorageConnection

# Adjust local sample paths if needed
$files = @(
  @{ Path = "..\tests\data\sample.pdf";  Blob = "samples/sample.pdf" },
  @{ Path = "..\tests\data\sample.docx"; Blob = "policies/policy.docx" },
  @{ Path = "..\tests\data\sample.txt";  Blob = "notes/note.txt" },
  @{ Path = "..\tests\data\sample.png";  Blob = "images/clause.png" }
)

foreach ($f in $files) {
  if (Test-Path $f.Path) {
    Write-Host "Uploading $($f.Path) -> $($f.Blob)" -ForegroundColor Cyan
    az storage blob upload `
      --connection-string "$Conn" `
      -f $f.Path -c $prov.BlobContainer -n $f.Blob --overwrite
  } else {
    Write-Warning "Missing local file: $($f.Path) (skipping)"
  }
}
