param(
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

if ($Runtime -notmatch '^[a-z0-9-]+$') {
    throw "Runtime invalide : $Runtime"
}

$projectRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path))
$project = Join-Path $projectRoot "src\VMiner.App\VMiner.App.csproj"
$artifactsRoot = Join-Path $projectRoot "artifacts"
$staging = Join-Path $artifactsRoot "VMiner-$Runtime-staging"

function Assert-ChildPath([string]$Path, [string]$Parent) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($fullParent, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Chemin hors du dossier autorisé : $fullPath"
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
    throw "La publication de VMiner a échoué (code $LASTEXITCODE)."
}

$publishedExe = Join-Path $staging "VMiner.exe"
if (-not (Test-Path -LiteralPath $publishedExe)) {
    throw "La publication n'a pas produit VMiner.exe."
}

# L'exécutable reste immédiatement visible à la racine du projet.
Copy-Item -LiteralPath $publishedExe -Destination (Join-Path $projectRoot "VMiner.exe") -Force

# Seuls les dossiers nécessaires au runtime sont remplacés. Le modèle déjà
# téléchargé dans models/ est volontairement préservé entre les builds.
foreach ($directoryName in @("IpaDic", "runtimes")) {
    $source = Join-Path $staging $directoryName
    $destination = Assert-ChildPath (Join-Path $projectRoot $directoryName) $projectRoot
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Dépendance publiée manquante : $directoryName"
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

Write-Host "VMiner portable prêt : $(Join-Path $projectRoot 'VMiner.exe')"
