# Puts one consistent set of mod files into the test mod folder (modtools\labmod) in one step:
#   regulation.bin (built from the sheets), the converted armor parts, the patched Chapel event script,
#   and a private copy of the code add-on (so rebuilding it never changes a running or half-started game).
# Refuses to run while Elden Ring is running.

$ErrorActionPreference = 'Stop'
$proj  = 'C:\Users\ddean\OneDrive\Desktop\CLAUDE AAP\tarnished-in-a-mech'
$tools = 'C:\Users\ddean\modtools'
$lab   = "$tools\labmod"
$er    = 'C:\program files (x86)\steam\steamapps\common\ELDEN RING\Game'
$dll   = "$tools\target\x86_64-pc-windows-gnullvm\release\tarnished_mech.dll"
$conv  = "$tools\work\ac6convert-bin\Ac6Convert.exe"
$toml  = "$tools\modengine2\ModEngine-2.1.0.0-win64\config_tarnished_lab.toml"

if (Get-Process eldenring -ErrorAction SilentlyContinue) { throw 'Elden Ring is running; close it before deploying.' }
$env:DOTNET_ROOT = "$tools\dotnet"

New-Item -ItemType Directory -Force "$lab\parts", "$lab\event" | Out-Null

# 1. regulation.bin from the sheets
& "$tools\python\python.exe" "$proj\tools\build_regulation.py" "$lab\regulation.bin" | Select-Object -Last 2

# 2. converted armor parts (staged by `Ac6Convert kit ...`)
Copy-Item "$tools\work\stage\parts\*" "$lab\parts" -Force

# 3. Chapel event script patched from the player's own copy of the game
& $conv skiptut $er "$tools\work\erraw\event\m10_00_00_00.emevd.dcx" "$lab\event\m10_00_00_00.emevd.dcx" | Select-Object -Last 1

# 4. private copy of the code add-on, and point ModEngine2 at it
Copy-Item $dll "$lab\tarnished_mech.dll" -Force
$t = Get-Content $toml -Raw
$t = [regex]::Replace($t, '(?m)^external_dlls\s*=.*$', 'external_dlls = [ "C:\\Users\\ddean\\modtools\\labmod\\tarnished_mech.dll" ]')
Set-Content $toml $t -Encoding ASCII

# 4b. ported Armored Core VI animation archives (built by tools\batch_port_anims.py), if staged
if (Test-Path "$tools\work\stage_anim\chr") {
    New-Item -ItemType Directory -Force "$lab\chr" | Out-Null
    Copy-Item "$tools\work\stage_anim\chr\*.anibnd.dcx" "$lab\chr" -Force
}

# 5. a manifest so any test can be matched to the exact files it ran with
$lines = @("Deployed $(Get-Date -Format s)")
foreach ($f in (Get-ChildItem $lab -Recurse -File | Sort-Object FullName)) {
    $lines += ('{0}  {1,10}  {2}' -f (Get-FileHash $f.FullName -Algorithm MD5).Hash.Substring(0, 12), $f.Length, $f.FullName.Substring($lab.Length + 1))
}
$lines | Set-Content "$lab\DEPLOYED.txt" -Encoding ASCII
"deployed $($lines.Count - 1) files to $lab"
