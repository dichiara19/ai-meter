# Renders the Microsoft Store screenshots (3840x2160) in every language from the real interface with demo data.
#   powershell -ExecutionPolicy Bypass -File tools\render-store.ps1 [-Languages en,it] [-Output bin\store-screenshots]
# The dashboard slide reuses tools\render-history.ps1, run once per language in its own process.
param([string[]]$Languages = @('en', 'it', 'es', 'fr', 'de', 'pt'), [string]$Output = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Output) { $Output = Join-Path $root 'bin\store-screenshots' }
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    & powershell -NoProfile -Sta -ExecutionPolicy Bypass -File $PSCommandPath -Languages ($Languages -join ',') -Output $Output
    exit $LASTEXITCODE
}
# A comma-separated list arrives as one string when the script restarts itself.
$Languages = @($Languages | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

$history = Join-Path ([IO.Path]::GetTempPath()) ('aimeter-store-history-' + [Guid]::NewGuid().ToString('N'))
try {
    foreach ($lang in $Languages) {
        & powershell -NoProfile -Sta -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'render-history.ps1') -Output (Join-Path $history $lang) -Language $lang | Out-Null
        if ($LASTEXITCODE) { throw "render-history.ps1 failed for $lang" }
    }
    $fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
    $refs = "$fw\WPF\PresentationCore.dll", "$fw\WPF\PresentationFramework.dll", "$fw\WPF\WindowsBase.dll", "$fw\System.Xaml.dll",
            "$fw\System.Web.Extensions.dll", "$fw\System.Windows.Forms.dll", "$fw\System.Drawing.dll", "$fw\System.Core.dll", "$fw\System.Security.dll"
    $env:AIMETER_HOME = $root
    $src = @(Get-ChildItem (Join-Path $root 'src\*.cs') | ForEach-Object FullName) + (Join-Path $PSScriptRoot 'MediaRenderer.cs') + (Join-Path $PSScriptRoot 'StoreScreenshots.cs')
    Add-Type -Path $src -ReferencedAssemblies $refs
    Write-Host ([AIMeter.StoreScreenshots]::Run($Output, $history, [string[]]$Languages))
    Get-ChildItem $Output -Recurse -Filter *.png | Select-Object @{ n = 'File'; e = { $_.FullName.Substring($Output.Length + 1) } }, @{ n = 'KB'; e = { [math]::Round($_.Length / 1KB) } } | Format-Table -AutoSize
}
finally {
    if (Test-Path $history) { Remove-Item -LiteralPath $history -Recurse -Force }
}
