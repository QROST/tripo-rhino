[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string] $Version,

  [ValidateSet("Debug", "Release")]
  [string] $Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$declaredVersion = (Get-Content -LiteralPath (Join-Path $repositoryRoot "VERSION") -Raw).Trim()
if ($declaredVersion -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
  throw "VERSION must contain a canonical release version."
}
$requestedVersion = $Version.Trim()
if ($requestedVersion.StartsWith("v", [StringComparison]::OrdinalIgnoreCase)) {
  $requestedVersion = $requestedVersion.Substring(1)
}

if ($requestedVersion -cne $declaredVersion) {
  throw "Requested version '$Version' does not match VERSION '$declaredVersion'."
}

$hostOutput = Join-Path $repositoryRoot "src/Tripo.Rhino/bin/$Configuration/net7.0"
$grasshopperOutput = Join-Path $repositoryRoot "src/Tripo.Rhino.Grasshopper/bin/$Configuration/net7.0"
$mcpOutput = Join-Path $repositoryRoot "src/Tripo.Rhino.Mcp/bin/$Configuration/net8.0"

foreach ($requiredPath in @(
  (Join-Path $hostOutput "Tripo.Rhino.rhp"),
  (Join-Path $hostOutput "sidecar/Tripo.Rhino.Mcp.dll"),
  (Join-Path $grasshopperOutput "Tripo.Rhino.Grasshopper.gha"),
  (Join-Path $mcpOutput "Tripo.Rhino.Mcp.dll")
)) {
  if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
    throw "Required release input is missing: $requiredPath"
  }
}

$artifactsDirectory = Join-Path $repositoryRoot "artifacts"
$candidateName = "tripo-rhino-v$declaredVersion"
$candidateRoot = Join-Path $artifactsDirectory $candidateName
$archivePath = "$candidateRoot.zip"
$checksumPath = "$archivePath.sha256"

foreach ($generatedPath in @($candidateRoot, $archivePath, $checksumPath)) {
  if (Test-Path -LiteralPath $generatedPath) {
    throw "Candidate already exists: $generatedPath. Preserve or move it before rebuilding."
  }
}

New-Item -ItemType Directory -Path $artifactsDirectory -Force | Out-Null
$lockPath = Join-Path $artifactsDirectory "$candidateName.lock"
# CreateNew excludes a second builder. A crash leaves a visible lock for review.
$candidateLock = [IO.File]::Open($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
$stage = Join-Path $artifactsDirectory ".candidate-$([Guid]::NewGuid().ToString('N'))"
$stagedRoot = Join-Path $stage $candidateName
$stagedArchive = Join-Path $stage "$candidateName.zip"
$stagedChecksum = "$stagedArchive.sha256"
$publishedPaths = [Collections.Generic.List[string]]::new()
try {
  New-Item -ItemType Directory -Path (Join-Path $stagedRoot "rhino") -Force | Out-Null
  New-Item -ItemType Directory -Path (Join-Path $stagedRoot "grasshopper") -Force | Out-Null
  New-Item -ItemType Directory -Path (Join-Path $stagedRoot "mcp") -Force | Out-Null

  Copy-Item -Path (Join-Path $hostOutput "*") -Destination (Join-Path $stagedRoot "rhino") -Recurse -Force
  Copy-Item -Path (Join-Path $grasshopperOutput "*") -Destination (Join-Path $stagedRoot "grasshopper") -Recurse -Force
  Copy-Item -Path (Join-Path $mcpOutput "*") -Destination (Join-Path $stagedRoot "mcp") -Recurse -Force
  foreach ($document in @("README.md", "README.zh-CN.md", "LICENSE", "NOTICE")) {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot $document) -Destination $stagedRoot
  }

  Compress-Archive -LiteralPath $stagedRoot -DestinationPath $stagedArchive -CompressionLevel Optimal
  $hash = (Get-FileHash -LiteralPath $stagedArchive -Algorithm SHA256).Hash.ToLowerInvariant()
  "$hash  $([IO.Path]::GetFileName($archivePath))" |
    Set-Content -LiteralPath $stagedChecksum -Encoding ascii

  # Same-volume moves refuse overwrites; the ZIP appears only after completion.
  [IO.Directory]::Move($stagedRoot, $candidateRoot)
  $publishedPaths.Add($candidateRoot)
  [IO.File]::Move($stagedChecksum, $checksumPath)
  $publishedPaths.Add($checksumPath)
  [IO.File]::Move($stagedArchive, $archivePath)
  $publishedPaths.Add($archivePath)
} catch {
  foreach ($publishedPath in $publishedPaths) {
    Remove-Item -LiteralPath $publishedPath -Recurse -Force
  }
  throw
} finally {
  try {
    if (Test-Path -LiteralPath $stage) {
      Remove-Item -LiteralPath $stage -Recurse -Force
    }
  } finally {
    $candidateLock.Dispose()
    Remove-Item -LiteralPath $lockPath -Force
  }
}

Write-Output $archivePath
Write-Output $checksumPath
