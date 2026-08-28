$ErrorActionPreference = 'Stop'
$installDirectory = Join-Path $env:LOCALAPPDATA 'CodexDictation'
$publishDirectory = Join-Path $PSScriptRoot 'artifacts\publish-framework'

dotnet publish $PSScriptRoot -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $publishDirectory
New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $publishDirectory 'CodexDictation.exe') -Destination $installDirectory -Force

$shell = New-Object -ComObject WScript.Shell
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Codex Dictation.lnk'
$shortcut = $shell.CreateShortcut($shortcutPath)
$executable = Join-Path $installDirectory 'CodexDictation.exe'
$shortcut.TargetPath = $executable
$shortcut.WorkingDirectory = $installDirectory
$shortcut.IconLocation = $executable
$shortcut.Save()

Write-Host "Installed. Close Codex, launch '$shortcutPath', then configure ASR in Settings > Voice."
