param(
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [string]$DownloadDirectory = (Join-Path $PSScriptRoot '..\build\soda-node-24.22.1')
)

$ErrorActionPreference = 'Stop'
$version = '24.22.1'
$expectedHash = 'd702daa815eabbff763c7d3808e235cf031cf2ca70211b56847bc72f51e240e5'
$downloadUrl = "https://nodejs.org/dist/v$version/win-x64/node.exe"
$licenseUrl = "https://raw.githubusercontent.com/nodejs/node/v$version/LICENSE"
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'Runtime\Soda\helper.mjs'))) {
    throw 'Expected a published Klang build with Runtime/Soda/helper.mjs.'
}
New-Item -ItemType Directory -Path $DownloadDirectory -Force | Out-Null
$nodePath = Join-Path $DownloadDirectory 'node.exe'
$licensePath = Join-Path $DownloadDirectory 'LICENSE'
if (-not (Test-Path -LiteralPath $nodePath)) {
    Invoke-WebRequest -Uri $downloadUrl -OutFile $nodePath
}
if ((Get-FileHash -LiteralPath $nodePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) {
    throw 'Bundled Node SHA-256 mismatch. No runtime was copied.'
}
if (-not (Test-Path -LiteralPath $licensePath)) {
    Invoke-WebRequest -Uri $licenseUrl -OutFile $licensePath
}
if ((Get-Item -LiteralPath $licensePath).Length -lt 10000) {
    throw 'Node license and third-party notices are incomplete.'
}
$destination = Join-Path $publishRoot 'Runtime\Soda\Node'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item -LiteralPath $nodePath,$licensePath -Destination $destination -Force
[ordered]@{ version=$version; url=$downloadUrl; sha256=$expectedHash; licenseUrl=$licenseUrl } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'DOWNLOAD.json') -Encoding utf8
Write-Host "Bundled verified Node.js $version (win-x64), including license and third-party notices."
