docker compose build --no-cache
docker compose up -d
Write-Host "API running at http://localhost:8080" -ForegroundColor Green

# health check
Start-Sleep -Seconds 3
try {
  $h = Invoke-RestMethod http://localhost:8080/healthz -TimeoutSec 10
  Write-Host "Health: $($h | ConvertTo-Json -Depth 5)" -ForegroundColor Green
} catch {
  Write-Warning "API not reachable yet; check 'docker compose logs -f'"
}
