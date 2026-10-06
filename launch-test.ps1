# Tarnished in a Mech - test launcher.
# Starts Elden Ring offline through ModEngine2 with the mech mod loaded. The mod keeps its own save
# (ER0000.mec); your real ER0000.sl2 is never opened. After you quit, this checks that your real save
# is byte-for-byte what it was before, and shows what the mod logged.

$ErrorActionPreference = 'Stop'
$tools  = 'C:\Users\ddean\modtools'
$lab    = "$tools\modengine2\ModEngine-2.1.0.0-win64"
$config = 'config_tarnished_lab.toml'
$saves  = Join-Path $env:APPDATA 'EldenRing\76561198153276745'
$logDir = 'C:\Users\ddean\modtools\labmod\logs'
$log    = "$logDir\latest.log"

function Hash($f) { if (Test-Path $f) { (Get-FileHash $f -Algorithm MD5).Hash } else { 'missing' } }

Write-Host ''
Write-Host '=== Tarnished in a Mech: test launcher ===' -ForegroundColor Cyan
Write-Host ''
Write-Host 'Before you continue:'
Write-Host '  - Close any other game (Elden Ring has to be the only one on screen).'
Write-Host '  - Steam must be running.'
Write-Host ''

if (Get-Process eldenring -ErrorAction SilentlyContinue) {
    Write-Host 'Elden Ring is already running. Close it first, then run this again.' -ForegroundColor Yellow
    return
}
foreach ($need in "$lab\modengine2_launcher.exe", "$tools\labmod\regulation.bin", "$tools\labmod\tarnished_mech.dll") {
    if (-not (Test-Path $need)) { Write-Host "Missing file: $need  (tell Claude)" -ForegroundColor Red; return }
}
if (Test-Path "$tools\labmod\DEPLOYED.txt") { Write-Host ("Mod build: " + (Get-Content "$tools\labmod\DEPLOYED.txt" -TotalCount 1)) }

# the test character lives in ER0000.mec; offer a clean start
$mec = Join-Path $saves 'ER0000.mec'
if (Test-Path $mec) {
    $a = Read-Host 'A test save exists (it may already hold your mech character). Start fresh? (y = fresh start, just press Enter = keep it)'
    if ($a -match '^[yY]') {
        $keep = "$tools\mec-backups\$(Get-Date -Format yyyyMMdd-HHmmss)"
        New-Item -ItemType Directory -Force $keep | Out-Null
        Move-Item "$saves\ER0000.mec*" $keep
        Write-Host "Old test character moved to $keep" -ForegroundColor Yellow
    }
}

$before = @{}
foreach ($f in 'ER0000.sl2', 'ER0000.sl2.bak', 'ER0000.co2', 'ER0000.cnv') { $before[$f] = Hash (Join-Path $saves $f) }

# fresh log for this run; keep the last one
if (Test-Path $log) { Move-Item $log "$logDir\previous.log" -Force }

Write-Host ''
Write-Host 'Starting Elden Ring (offline, mech mod). The title screen takes about 40 seconds.' -ForegroundColor Green
Write-Host ''
Write-Host '  New game: pick a class, finish creation.  You should land in Limgrave (tutorial skipped).'
Write-Host '  Warrior = Light kit, Vagabond = Medium kit, Hero = Heavy kit.'
Write-Host '  Quit the game normally when you are done.'
Write-Host ''

$launchTime = Get-Date
Start-Process -FilePath "$lab\modengine2_launcher.exe" -ArgumentList '-t', 'er', '-c', $config -WorkingDirectory $lab -WindowStyle Hidden

$deadline = (Get-Date).AddSeconds(90)
while (-not (Get-Process eldenring -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
if (-not (Get-Process eldenring -ErrorAction SilentlyContinue)) {
    Write-Host 'The game did not start (or closed immediately). Tell Claude; the mod log is:' -ForegroundColor Red
    if (Test-Path $log) { Get-Content $log -Tail 8 }
    return
}
while (Get-Process eldenring -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 3 }
Start-Sleep -Seconds 5   # let Steam finish its exit sync

# did the game crash? Windows records it in the Application log
$crash = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Application Error'; StartTime = $launchTime } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match 'eldenring' } | Select-Object -First 1
Write-Host ''
if ($crash) {
    Write-Host 'THE GAME CRASHED. Windows says:' -ForegroundColor Red
    ($crash.Message -split "`n" | Select-String 'Faulting module name|Exception code|Fault offset') | ForEach-Object { Write-Host "  $($_.Line.Trim())" -ForegroundColor Red }
    Write-Host '  (send this to Claude)' -ForegroundColor Red
} else {
    Write-Host 'The game exited normally (no crash recorded).' -ForegroundColor Green
}
Write-Host ''
Write-Host 'Checking your real save files...' -ForegroundColor Cyan
$ok = $true
foreach ($f in $before.Keys) {
    $now = Hash (Join-Path $saves $f)
    if ($now -ne $before[$f]) { $ok = $false; Write-Host "  CHANGED: $f" -ForegroundColor Red } else { Write-Host "  unchanged: $f" }
}
if ($ok) { Write-Host 'Your real save is exactly as it was.' -ForegroundColor Green }
else { Write-Host "A real save file changed. Tell Claude right away; backup: $tools\save-backup" -ForegroundColor Red }

Write-Host ''
Write-Host 'What the mod logged (last lines):'
if (Test-Path $log) {
    $lines = Get-Content $log | Where-Object { $_ -notmatch 'savefile-io' }
    $panics = $lines | Where-Object { $_ -match 'PANIC' }
    if ($panics) { Write-Host '  There were errors inside the mod:' -ForegroundColor Red; $panics | Select-Object -First 3 | ForEach-Object { Write-Host "  $_" } }
    $lines | Select-Object -Last 6 | ForEach-Object { Write-Host "  $_" }
}
Write-Host ''
Write-Host "Full log: $log"

