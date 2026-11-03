# Copy the files the script build of AI Meter runs from (AIMeter.ps1 compiles src at start-up and embeds nothing, so it
# reads providers, agents and assets from beside it) into a folder. Shared by build.ps1's script install and the CI's
# AIMeter-script.zip, so that the two cannot drift apart again.
#   powershell -ExecutionPolicy Bypass -File tools\copy-script-app.ps1 -Destination <folder>
param([Parameter(Mandatory = $true)][string]$Destination)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

New-Item -ItemType Directory -Force (Join-Path $Destination 'src'), (Join-Path $Destination 'assets'), (Join-Path $Destination 'providers'), (Join-Path $Destination 'agents') | Out-Null
Copy-Item (Join-Path $root 'src\*.cs') (Join-Path $Destination 'src') -Force
Copy-Item (Join-Path $root 'providers\*') (Join-Path $Destination 'providers') -Include *.json, *.png -Force
Copy-Item (Join-Path $root 'agents\*.json') (Join-Path $Destination 'agents') -Force
Copy-Item (Join-Path $root 'assets\app.ico'), (Join-Path $root 'assets\antigravity.png') (Join-Path $Destination 'assets') -Force
Copy-Item (Join-Path $root 'AIMeter.ps1') $Destination -Force
