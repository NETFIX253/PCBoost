<#
.SYNOPSIS
  Compile, teste et (optionnellement) publie PCBoost et son installateur.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\build.ps1                 # Release x64 + tests
  powershell -ExecutionPolicy Bypass -File build\build.ps1 -Publish -Installer
#>
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [ValidateSet('x64', 'ARM64')] [string] $Platform = 'x64',
    [switch] $SkipTests,
    [switch] $Publish,
    [switch] $Installer
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$logs = Join-Path $root 'artifacts\logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
$summary = Join-Path $logs 'summary.txt'
$rid = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Log([string] $line) {
    Write-Host $line
    Add-Content -Path $summary -Value $line -Encoding UTF8
}

function Step([string] $name, [scriptblock] $action) {
    $start = Get-Date
    Log "[$($start.ToString('HH:mm:ss'))] $name..."
    & $action
    $code = $LASTEXITCODE
    $elapsed = [int]((Get-Date) - $start).TotalSeconds
    Log "[$((Get-Date).ToString('HH:mm:ss'))] $name -> code $code ($elapsed s)"
    if ($code -ne 0) { Log "ECHEC : $name"; exit $code }
}

Set-Content -Path $summary -Value "=== PCBoost build $Configuration $Platform - $(Get-Date -Format s) ===" -Encoding UTF8
dotnet --info *> (Join-Path $logs 'dotnet-info.txt')

Step 'Restauration' { dotnet restore PCBoost.sln -p:Platform=$Platform *> (Join-Path $logs 'restore.log') }
Step 'Compilation' { dotnet build PCBoost.sln -c $Configuration -p:Platform=$Platform --no-restore -nologo -clp:NoSummary "-flp:logfile=$logs\build.log;verbosity=minimal" *> (Join-Path $logs 'build-console.log') }

if (-not $SkipTests) {
    Step 'Tests' { dotnet test PCBoost.sln -c $Configuration -p:Platform=$Platform --no-build -nologo --logger "trx;LogFilePrefix=pcboost" --results-directory (Join-Path $root 'artifacts\test-results') *> (Join-Path $logs 'test.log') }
}

if ($Publish -or $Installer) {
    [xml] $branding = Get-Content (Join-Path $root 'build\Branding.props')
    $product = $branding.Project.PropertyGroup.BrandProductName
    $version = $branding.Project.PropertyGroup.BrandVersion
    $publishDir = Join-Path $root "artifacts\publish\$rid"
    $dist = Join-Path $root 'dist'
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    Step 'Publication' { dotnet publish src\PCBoost.App\PCBoost.App.csproj -c Release -r $rid -p:Platform=$Platform --self-contained true -o $publishDir -nologo *> (Join-Path $logs 'publish.log') }
    Step 'Contrôle de la publication' {
        # Garde-fou : une publication sans XAML compilé ni index de ressources ne démarre pas.
        foreach ($required in @('PCBoost.exe', 'PCBoost.pri', 'App.xbf', 'MainWindow.xbf', 'Views\HomePage.xbf', 'PCBoost.Elevator.exe', 'Assets\Branding\app.ico')) {
            if (-not (Test-Path (Join-Path $publishDir $required))) { Log "Fichier manquant dans la publication : $required"; $global:LASTEXITCODE = 1; return }
        }
        $global:LASTEXITCODE = 0
    }
    Step 'Version portable' {
        # Archive autonome : décompresser puis lancer PCBoost.exe (aucune installation ; données dans %LOCALAPPDATA%\PCBoost).
        $zip = Join-Path $dist "$product-$version-$Platform-portable.zip"
        if (Test-Path $zip) { Remove-Item -Force $zip }
        Get-ChildItem -Path $publishDir -Recurse -Filter '*.pdb' | Remove-Item -Force
        Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip -CompressionLevel Optimal
        Copy-Item -Force (Join-Path $root 'docs\dist\README.md') (Join-Path $dist 'README.md')
        Copy-Item -Force (Join-Path $root 'CHANGELOG.md') (Join-Path $dist 'CHANGELOG.md')
        $global:LASTEXITCODE = 0
    }
}

if ($Installer) {
    Step 'Installateur MSI' { dotnet build installer\PCBoost.Installer\PCBoost.Installer.wixproj -c Release "-p:PublishDir=$publishDir" "-p:InstallerPlatform=$Platform" -nologo *> (Join-Path $logs 'installer.log') }
    Step 'Empreintes' {
        # Empreintes SHA-256 des livrables de CETTE version (vérification d'intégrité par l'utilisateur ou un mécanisme de
        # mise à jour). Les fichiers d'une version précédente restés dans dist\ n'y figurent pas : SHA256SUMS.txt est joint
        # tel quel à la Release et recopié dans ses notes.
        Get-ChildItem -Path $dist -File | Where-Object { $_.Extension -in '.msi', '.zip' -and $_.Name -like "$product-$version-*" } |
            ForEach-Object { "$((Get-FileHash -Algorithm SHA256 -Path $_.FullName).Hash.ToLowerInvariant())  $($_.Name)" } |
            Set-Content -Path (Join-Path $dist 'SHA256SUMS.txt') -Encoding ASCII
        $global:LASTEXITCODE = 0
    }
}

Log "TERMINE"
