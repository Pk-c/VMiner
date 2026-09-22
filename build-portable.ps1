param(
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

if ($Runtime -notmatch '^[a-z0-9-]+$') {
    throw "Invalid runtime: $Runtime"
}

$projectRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path))
$project = Join-Path $projectRoot "src\VMiner.App\VMiner.App.csproj"
$artifactsRoot = Join-Path $projectRoot "artifacts"
$staging = Join-Path $artifactsRoot "VMiner-$Runtime-staging"

function Assert-ChildPath([string]$Path, [string]$Parent) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($fullParent, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the allowed directory: $fullPath"
    }
    return $fullPath
}

$staging = Assert-ChildPath $staging $projectRoot
if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}
New-Item -ItemType Directory -Path $staging -Force | Out-Null

dotnet publish $project `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    --output $staging

if ($LASTEXITCODE -ne 0) {
    throw "VMiner publish failed (exit code $LASTEXITCODE)."
}

$publishedExe = Join-Path $staging "VMiner.exe"
if (-not (Test-Path -LiteralPath $publishedExe)) {
    throw "The publish operation did not produce VMiner.exe."
}

# Keep the executable immediately visible in the repository root.
Copy-Item -LiteralPath $publishedExe -Destination (Join-Path $projectRoot "VMiner.exe") -Force

# Replace only the runtime dependency folders. Preserve the model already
# downloaded into models/ between builds.
foreach ($directoryName in @("IpaDic", "runtimes")) {
    $source = Join-Path $staging $directoryName
    $destination = Assert-ChildPath (Join-Path $projectRoot $directoryName) $projectRoot
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Published dependency is missing: $directoryName"
    }
    if (Test-Path -LiteralPath $destination) {
        Remove-Item -LiteralPath $destination -Recurse -Force
    }
    Move-Item -LiteralPath $source -Destination $destination
}

$models = Join-Path $projectRoot "models"
New-Item -ItemType Directory -Path $models -Force | Out-Null
$modelReadme = Join-Path $staging "models\README.txt"
if (Test-Path -LiteralPath $modelReadme) {
    Copy-Item -LiteralPath $modelReadme -Destination (Join-Path $models "README.txt") -Force
}

Remove-Item -LiteralPath $staging -Recurse -Force
if ((Test-Path -LiteralPath $artifactsRoot) -and
    -not (Get-ChildItem -LiteralPath $artifactsRoot -Force | Select-Object -First 1)) {
    Remove-Item -LiteralPath $artifactsRoot
}

Write-Host "VMiner portable build ready: $(Join-Path $projectRoot 'VMiner.exe')"
