# Shared by site staging and local branding checks. No build or upload is run.
param(
    [Parameter(Mandatory = $true)][string]$Destination,
    [Parameter(Mandatory = $true)][string]$CacheTag
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'src/app/patchy.ico') -Destination (Join-Path $Destination 'favicon.ico')
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging/branding/patchy-logo-folded.svg') -Destination (Join-Path $Destination 'patchy-logo.svg')
foreach ($name in @('apple-touch-icon.png', 'patchy-192.png', 'patchy-512.png')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot "packaging/web/icons/$name") -Destination (Join-Path $Destination $name)
}
$manifest = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'packaging/web/site.webmanifest.in')
$manifest = $manifest.Replace('__PATCHY_CACHE_TAG__', [Uri]::EscapeDataString($CacheTag))
$null = $manifest | ConvertFrom-Json
Set-Content -LiteralPath (Join-Path $Destination 'site.webmanifest') -Value $manifest -Encoding UTF8 -NoNewline
