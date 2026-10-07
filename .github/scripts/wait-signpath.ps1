param(
  [Parameter(Mandatory)][string]$SignedArtifactUrl,
  [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# The SignPath action's own wait backs off 10s, 20s, 40s..., overshooting completion by up to 40s.
$headers = @{ Authorization = "Bearer $env:SIGNPATH_API_TOKEN" }
$statusUrl = $SignedArtifactUrl -replace '/SignedArtifact\?', '/Status?'
if ($statusUrl -eq $SignedArtifactUrl) { throw "Unexpected signed artifact URL: $SignedArtifactUrl" }

$deadline = (Get-Date).AddMinutes(30)
while ($true) {
  $status = Invoke-RestMethod -Uri $statusUrl -Headers $headers -TimeoutSec 30 -MaximumRetryCount 5 -RetryIntervalSec 5
  if ($status.isFinalStatus) { break }
  if ((Get-Date) -gt $deadline) { throw "Signing request still $($status.status) after 30 minutes" }
  Start-Sleep -Seconds 3
}
if ($status.status -ne 'Completed') { throw "Signing request ended as $($status.status)" }
Write-Host "Signing request completed"

$zip = Join-Path $env:RUNNER_TEMP "signed-$([guid]::NewGuid()).zip"
Invoke-WebRequest -Uri $SignedArtifactUrl -Headers $headers -OutFile $zip -TimeoutSec 600 -MaximumRetryCount 5 -RetryIntervalSec 5
Expand-Archive -Path $zip -DestinationPath $OutputDirectory -Force
Remove-Item $zip
Write-Host "Signed artifact extracted to $OutputDirectory"
