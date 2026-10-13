# Publishes the current working tree as a self-contained exe into publish\MtgaCollectionAdvisor
# and points the "MTGA Deck Advisor" desktop shortcut at it - so the shortcut always runs the
# latest code. Run from anywhere: powershell -ExecutionPolicy Bypass -File publish-local.ps1
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$out = Join-Path $root 'publish\MtgaCollectionAdvisor'
$exe = Join-Path $out 'MtgaDeckAdvisor.exe'

# A running instance locks the exe it was started from, so stop it outright: the desktop app,
# and a browser-hosted one left from before #119.
Get-Process MtgaDeckAdvisor, MtgaCollectionAdvisor.Web -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
# From scratch, so nothing an earlier build left (the browser-hosted one's files) ships along.
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish (Join-Path $root 'src\MtgaCollectionAdvisor.Desktop') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$desktop = [Environment]::GetFolderPath('Desktop')
# The shortcut from before the rename to MTGA Deck Advisor.
Remove-Item (Join-Path $desktop 'MTGA Collection Advisor.lnk') -ErrorAction SilentlyContinue

$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $desktop 'MTGA Deck Advisor.lnk'))
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $out
$shortcut.IconLocation = "$exe,0"
$shortcut.Description = 'MTGA Deck Advisor - A tracker for your decks in Magic Arena'
$shortcut.Save()

$branch = git -C $root branch --show-current
$commit = git -C $root log -1 --format='%h %s'
$dirty = if (git -C $root status --porcelain) { ' (+ uncommitted changes)' } else { '' }
Write-Output "Published $branch @ $commit$dirty"
Write-Output "  -> $exe"
