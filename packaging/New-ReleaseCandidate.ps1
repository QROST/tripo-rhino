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
    Remove-Item -LiteralPath $generatedPath -Recurse -Force
  }
}

New-Item -ItemType Directory -Path (Join-Path $candidateRoot "rhino") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $candidateRoot "grasshopper") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $candidateRoot "mcp") -Force | Out-Null

Copy-Item -Path (Join-Path $hostOutput "*") -Destination (Join-Path $candidateRoot "rhino") -Recurse -Force
Copy-Item -Path (Join-Path $grasshopperOutput "*") -Destination (Join-Path $candidateRoot "grasshopper") -Recurse -Force
Copy-Item -Path (Join-Path $mcpOutput "*") -Destination (Join-Path $candidateRoot "mcp") -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot "README.md") -Destination $candidateRoot
Copy-Item -LiteralPath (Join-Path $repositoryRoot "README.zh-CN.md") -Destination $candidateRoot

Compress-Archive -LiteralPath $candidateRoot -DestinationPath $archivePath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($archivePath))" |
  Set-Content -LiteralPath $checksumPath -Encoding ascii

Write-Output $archivePath
Write-Output $checksumPath
