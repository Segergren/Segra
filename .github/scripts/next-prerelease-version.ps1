param([Parameter(Mandatory)][ValidateSet('patch', 'minor', 'major')][string]$ReleaseType)

# Get the highest final release version (exclude prereleases, sort by semver)
$latestFinalTag = (git tag | Where-Object { $_ -match '^v\d+\.\d+\.\d+$' } | Sort-Object { [version]($_ -replace '^v', '') } | Select-Object -Last 1) -replace '^v', ''
if (-not $latestFinalTag) {
  $latestFinalTag = "0.0.0"
}
Write-Host "Latest final release: $latestFinalTag"

# Calculate what the next version would be based on release type
$versionParts = $latestFinalTag.Split('.')
$major = [int]$versionParts[0]
$minor = [int]$versionParts[1]
$patch = [int]$versionParts[2]

switch ($ReleaseType) {
  "major" { $major++; $minor=0; $patch=0 }
  "minor" { $minor++; $patch=0 }
  "patch" { $patch++ }
}
$nextBaseVersion = "$major.$minor.$patch"
Write-Host "Next base version would be: $nextBaseVersion"

# Check if there are existing betas for this next version
$existingPrereleases = git tag | Where-Object { $_ -match "^v$([regex]::Escape($nextBaseVersion))-beta\.(\d+)$" }

if ($existingPrereleases) {
  # Find the highest beta number
  $highestNum = $existingPrereleases | ForEach-Object {
    if ($_ -match "-beta\.(\d+)$") { [int]$Matches[1] }
  } | Sort-Object | Select-Object -Last 1

  $prereleaseNumber = $highestNum + 1
  Write-Host "Found existing betas, highest is beta.$highestNum, creating beta.$prereleaseNumber"
} else {
  $prereleaseNumber = 1
  Write-Host "No existing betas for $nextBaseVersion, starting at beta.1"
}

$newVersion = "$nextBaseVersion-beta.$prereleaseNumber"
Write-Host "New version: $newVersion"
$newVersion
