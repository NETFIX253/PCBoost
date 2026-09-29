<#
.SYNOPSIS
  Boucle de développement PCBoost (outil développeur, non livré dans l'installateur).
  Surveille artifacts\devloop\request.json et exécute UNIQUEMENT une liste fermée d'actions :
  build, test, publish, installer, msi, verify-msi, launch, selfcapture, logs, crashinfo, stop, capture, install-msi, uninstall-msi, status.
  Aucune commande arbitraire n'est acceptée. Fermez la fenêtre pour arrêter l'agent.
  Au démarrage, une instance plus ancienne de l'agent est arrêtée ; si ce script change sur le disque,
  l'agent se relance lui-même (même liste fermée d'actions).
#>
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$loop = Join-Path $root 'artifacts\devloop'
$results = Join-Path $loop 'results'
New-Item -ItemType Directory -Force -Path $results | Out-Null
$requestFile = Join-Path $loop 'request.json'
$heartbeat = Join-Path $loop 'agent-alive.txt'
$agentVersion = 4
$agentHash = (Get-FileHash -Path $PSCommandPath -Algorithm SHA256).Hash

# Une seule instance : arrêter les agents plus anciens (même script, autre processus).
Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -like '*dev-agent.ps1*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
$Host.UI.RawUI.WindowTitle = 'PCBoost — agent de développement (fermer pour arrêter)'

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class DevNative {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
[DevNative]::SetProcessDPIAware() | Out-Null

function Get-AppExe {
    Get-ChildItem -Path (Join-Path $root 'src\PCBoost.App\bin') -Recurse -Filter 'PCBoost.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

function Invoke-Capture([string] $name) {
    $out = Join-Path $results "$name.png"
    $proc = Get-Process -Name 'PCBoost' -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($null -eq $proc) { return "Aucune fenêtre PCBoost visible." }
    [DevNative]::ShowWindow($proc.MainWindowHandle, 9) | Out-Null
    [DevNative]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 900
    $rect = New-Object DevNative+RECT
    [DevNative]::DwmGetWindowAttribute($proc.MainWindowHandle, 9, [ref] $rect, 16) | Out-Null
    $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
    if ($w -le 0 -or $h -le 0) { return "Dimensions de fenêtre invalides." }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bmp.Size)
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    return "Capture : $out ($w x $h)"
}

function Invoke-Action($req) {
    $log = Join-Path $results "$($req.id).log"
    if ($req.action -in @('build', 'test', 'publish', 'installer')) {
        # Une instance lancée verrouille les DLL de sortie : l'arrêter avant de compiler.
        Get-Process -Name 'PCBoost', 'PCBoost.Elevator' -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }
    switch ($req.action) {
        'build'     { & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build.ps1') -SkipTests *> $log; return $LASTEXITCODE }
        'test'      { & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build.ps1') *> $log; return $LASTEXITCODE }
        'publish'   { & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build.ps1') -SkipTests -Publish *> $log; return $LASTEXITCODE }
        'installer' { & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build.ps1') -Publish -Installer *> $log; return $LASTEXITCODE }
        'launch' {
            Get-Process -Name 'PCBoost' -ErrorAction SilentlyContinue | Stop-Process -Force
            Start-Sleep -Milliseconds 500
            $exe = Get-AppExe
            if ($null -eq $exe) { "PCBoost.exe introuvable (compiler d'abord)." | Set-Content $log; return 2 }
            $argList = @()
            if ($req.page) { $argList += @('--page', [string]$req.page) }
            if ($req.theme) { $argList += @('--theme', [string]$req.theme) }
            if ($req.lang) { $argList += @('--lang', [string]$req.lang) }
            if ($argList.Count -gt 0) { Start-Process -FilePath $exe.FullName -ArgumentList $argList } else { Start-Process -FilePath $exe.FullName }
            $wait = if ($req.wait) { [int]$req.wait } else { 8 }
            Start-Sleep -Seconds $wait
            $p = Get-Process -Name 'PCBoost' -ErrorAction SilentlyContinue
            "Lancé : $($exe.FullName) ; en cours : $([bool]$p)" | Set-Content $log
            if ($req.capture) { Invoke-Capture $req.id | Add-Content $log }
            if ($p) { return 0 } else { return 3 }
        }
        'capture' { Invoke-Capture $req.id | Set-Content $log; return 0 }
        'msi' {
            # Reconstruit seulement le MSI à partir de la dernière publication (artifacts\publish\win-x64).
            $publishDir = Join-Path $root 'artifacts\publish\win-x64'
            & dotnet build (Join-Path $root 'installer\PCBoost.Installer\PCBoost.Installer.wixproj') -c Release "-p:PublishDir=$publishDir" '-p:InstallerPlatform=x64' -nologo *> $log
            return $LASTEXITCODE
        }
        'selfcapture' {
            # L'application capture elle-même ses pages (RenderTargetBitmap) puis se ferme : fonctionne même écran verrouillé.
            Get-Process -Name 'PCBoost' -ErrorAction SilentlyContinue | Stop-Process -Force
            Start-Sleep -Milliseconds 500
            $exe = Get-AppExe
            if ($null -eq $exe) { "PCBoost.exe introuvable (compiler d'abord)." | Set-Content $log; return 2 }
            $pages = if ($req.pages -and ([string]$req.pages) -match '^[a-z,-]{1,400}$') { [string]$req.pages } else { 'home' }
            $dir = Join-Path $results "$($req.id)-cap"
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            $argList = @('--capture-dir', "`"$dir`"", '--capture-pages', $pages)
            if ($req.theme -and ([string]$req.theme) -match '^(light|dark|system)$') { $argList += @('--theme', [string]$req.theme) }
            if ($req.lang -and ([string]$req.lang) -match '^(fr|en)$') { $argList += @('--lang', [string]$req.lang) }
            $p = Start-Process -FilePath $exe.FullName -ArgumentList $argList -PassThru
            $timeout = if ($req.wait) { [int]$req.wait } else { 150 }
            $exited = $p.WaitForExit($timeout * 1000)
            if (-not $exited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
            $files = Get-ChildItem -Path $dir -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Name) ($($_.Length) o)" }
            @("Terminé : $exited ; code : $(if ($exited) { $p.ExitCode } else { 'délai dépassé' })") + $files | Set-Content $log
            if ($exited) { return 0 } else { return 4 }
        }
        'logs' {
            $logDir = Join-Path $env:LOCALAPPDATA 'PCBoost\logs'
            $latest = Get-ChildItem -Path $logDir -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($null -eq $latest) { "Aucun journal dans $logDir." | Set-Content $log; return 2 }
            Get-Content -Path $latest.FullName -Tail 400 -ErrorAction SilentlyContinue | Set-Content $log
            return 0
        }
        'crashinfo' {
            # Lecture seule : derniers arrêts anormaux de PCBoost consignés par Windows (journal Application :
            # 1000 = module fautif, 1026 = exception .NET non gérée avec pile).
            $events = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000, 1026; StartTime = (Get-Date).AddDays(-3) } -MaxEvents 300 -ErrorAction SilentlyContinue |
                Where-Object { $_.Message -like '*PCBoost*' } | Select-Object -First 6
            if (-not $events) { "Aucun arrêt anormal de PCBoost consigné depuis 3 jours." | Set-Content $log; return 0 }
            $events | ForEach-Object { "[$($_.TimeCreated.ToString('s'))] Id $($_.Id) $($_.ProviderName)`r`n$($_.Message)`r`n" } | Set-Content $log
            return 0
        }
        'stop' { Get-Process -Name 'PCBoost', 'PCBoost.Elevator' -ErrorAction SilentlyContinue | Stop-Process -Force; "Arrêté." | Set-Content $log; return 0 }
        'verify-msi' {
            # Vérifie le MSI sans l'installer : extraction administrative (sans UAC), comparaison avec la publication,
            # puis lancement de l'exécutable extrait en mode capture (démarrage réel de l'application).
            $msi = Get-ChildItem -Path (Join-Path $root 'dist') -Filter '*.msi' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($null -eq $msi) { "MSI introuvable dans dist\." | Set-Content $log; return 2 }
            $target = Join-Path $root 'artifacts\msi-admin'
            if (Test-Path $target) { Remove-Item -Recurse -Force $target }
            $p = Start-Process msiexec.exe -ArgumentList @('/a', "`"$($msi.FullName)`"", '/qn', "TARGETDIR=`"$target`"", '/l*v', "`"$results\$($req.id)-msi.log`"") -Wait -PassThru
            $lines = @("msiexec /a -> $($p.ExitCode)")
            $exe = Get-ChildItem -Path $target -Recurse -Filter 'PCBoost.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
            $publish = Join-Path $root 'artifacts\publish\win-x64'
            $published = @(Get-ChildItem -Path $publish -Recurse -File | Where-Object { $_.Extension -ne '.pdb' }).Count
            $extracted = if ($exe) { @(Get-ChildItem -Path $exe.DirectoryName -Recurse -File).Count } else { 0 }
            $lines += "Fichiers publiés : $published ; extraits du MSI : $extracted ; exe : $(if ($exe) { $exe.FullName } else { 'absent' })"
            if ($exe) {
                $cap = Join-Path $results "$($req.id)-cap"
                New-Item -ItemType Directory -Force -Path $cap | Out-Null
                $app = Start-Process -FilePath $exe.FullName -ArgumentList @('--capture-dir', "`"$cap`"", '--capture-pages', 'home,about') -PassThru
                $ok = $app.WaitForExit(90000)
                if (-not $ok) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
                $lines += "Lancement depuis le MSI : terminé=$ok ; captures : $((Get-ChildItem $cap -Filter '*.png' -ErrorAction SilentlyContinue | ForEach-Object Name) -join ', ')"
            }
            $lines | Set-Content $log
            if ($p.ExitCode -eq 0 -and $exe) { return 0 } else { return 5 }
        }
        'install-msi' {
            $msi = Get-ChildItem -Path (Join-Path $root 'dist') -Filter '*.msi' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($null -eq $msi) { "MSI introuvable dans dist\." | Set-Content $log; return 2 }
            $p = Start-Process msiexec.exe -ArgumentList @('/i', "`"$($msi.FullName)`"", '/qb', '/l*v', "`"$results\$($req.id)-msi.log`"") -Wait -PassThru
            "msiexec /i -> $($p.ExitCode)" | Set-Content $log; return $p.ExitCode
        }
        'uninstall-msi' {
            $msi = Get-ChildItem -Path (Join-Path $root 'dist') -Filter '*.msi' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($null -eq $msi) { "MSI introuvable dans dist\." | Set-Content $log; return 2 }
            $p = Start-Process msiexec.exe -ArgumentList @('/x', "`"$($msi.FullName)`"", '/qb', '/l*v', "`"$results\$($req.id)-msi.log`"") -Wait -PassThru
            "msiexec /x -> $($p.ExitCode)" | Set-Content $log; return $p.ExitCode
        }
        'status' {
            $exe = Get-AppExe
            $info = [ordered]@{
                time = (Get-Date -Format s)
                app = $(if ($exe) { $exe.FullName } else { $null })
                running = [bool](Get-Process -Name 'PCBoost' -ErrorAction SilentlyContinue)
                installed = Test-Path "$env:ProgramFiles\PCBoost\PCBoost.exe"
            }
            $info | ConvertTo-Json | Set-Content $log; return 0
        }
        default { "Action refusée : $($req.action)" | Set-Content $log; return 99 }
    }
}

Write-Host "Agent de développement PCBoost actif. Dossier : $loop"
Write-Host "Actions autorisées : build, test, publish, installer, msi, verify-msi, launch, selfcapture, logs, crashinfo, stop, capture, install-msi, uninstall-msi, status."
while ($true) {
    "$(Get-Date -Format s) v$agentVersion" | Set-Content $heartbeat
    $currentHash = (Get-FileHash -Path $PSCommandPath -Algorithm SHA256 -ErrorAction SilentlyContinue).Hash
    if ($currentHash -and $currentHash -ne $agentHash) {
        # Laisser la copie se terminer, puis relancer la nouvelle version (elle arrêtera celle-ci).
        Start-Sleep -Seconds 5
        Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"") -WindowStyle Minimized
        exit 0
    }
    if (Test-Path $requestFile) {
        $raw = Get-Content $requestFile -Raw -ErrorAction SilentlyContinue
        Remove-Item $requestFile -Force -ErrorAction SilentlyContinue
        try { $req = $raw | ConvertFrom-Json } catch { $req = $null }
        if ($null -ne $req -and $req.id -match '^[A-Za-z0-9_-]{1,40}$') {
            Write-Host "[$(Get-Date -Format HH:mm:ss)] $($req.action) ($($req.id))"
            $start = Get-Date
            $code = Invoke-Action $req
            $result = [ordered]@{ id = $req.id; action = $req.action; exitCode = $code; seconds = [int]((Get-Date) - $start).TotalSeconds; finished = (Get-Date -Format s) }
            $result | ConvertTo-Json | Set-Content (Join-Path $results "$($req.id).json")
            Write-Host "   -> code $code"
        }
    }
    Start-Sleep -Seconds 2
}
