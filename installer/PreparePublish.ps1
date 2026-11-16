param(
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [string]$NodeDownloadDirectory,
    [switch]$SkipSourceArchive
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'Klang.Soda.exe'))) {
    throw 'Expected a published Klang Soda directory containing Klang.Soda.exe.'
}
if (-not $SkipSourceArchive) {
    $pending = git -C $repoRoot status --porcelain
    if ($LASTEXITCODE -ne 0 -or $pending) {
        throw 'A source archive requires a clean committed checkout. For local validation only, use -SkipSourceArchive.'
    }
}
$nodeArguments = @{ PublishDirectory=$publishRoot }
if ($NodeDownloadDirectory) { $nodeArguments.DownloadDirectory=$NodeDownloadDirectory }
& (Join-Path $PSScriptRoot 'BundleSodaNode.ps1') @nodeArguments
foreach ($file in @('LICENSE','PRIVACY.md','THIRD_PARTY_NOTICES.md','README.md','RELEASE_NOTES.md')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $file) -Destination $publishRoot -Force
}
$docs = Join-Path $publishRoot 'docs'
New-Item -ItemType Directory -Path $docs -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $repoRoot 'docs') -File -Filter '*.md' | Copy-Item -Destination $docs -Force
if (-not $SkipSourceArchive) {
    $source = Join-Path $publishRoot 'Source'
    New-Item -ItemType Directory -Path $source -Force | Out-Null
    git -C $repoRoot archive --format=zip --prefix=klang-soda/ --output (Join-Path $source 'klang-soda-source.zip') HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not archive the corresponding source.' }
}
foreach ($symbol in Get-ChildItem -LiteralPath $publishRoot -Recurse -File -Filter '*.pdb') {
    if (-not $symbol.FullName.StartsWith($publishRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unexpected symbol path.'
    }
    Remove-Item -LiteralPath $symbol.FullName
}
$privateFiles = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File | Where-Object {
    $_.Name -match '^(session\.bin|device\.json|player_state\.(json|bak)|running\.lock|metasecml\.dll)$' -or
    $_.Extension -in '.node','.log' -or $_.FullName -match '[\\/]login-temp-[^\\/]+'
})
if ($privateFiles.Count) { throw 'Unexpected user data, logs or official Soda components in publish payload.' }
Write-Host 'Prepared Klang Soda payload; account/native-component exclusion checks passed.'
