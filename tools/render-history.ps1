# Render real WPF controls with isolated fixtures. No running app, network or saved accounts are used.
param([string]$Output = '', [string]$Language = 'en')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Output) { $Output = Join-Path $root 'bin\history-preview' }
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$parameters = New-Object System.CodeDom.Compiler.CompilerParameters
$parameters.GenerateInMemory = $true
$parameters.CompilerOptions = '/langversion:5 /define:AIMETER_TEST'
@(
    "$fw\WPF\PresentationCore.dll", "$fw\WPF\PresentationFramework.dll", "$fw\WPF\WindowsBase.dll",
    "$fw\System.Xaml.dll", "$fw\System.Web.Extensions.dll", "$fw\System.Windows.Forms.dll",
    "$fw\System.Drawing.dll", "$fw\System.Core.dll", "$fw\System.Security.dll", "$fw\System.dll"
) | ForEach-Object { [void]$parameters.ReferencedAssemblies.Add($_) }
$source = @(Get-ChildItem (Join-Path $root 'src\*.cs') | ForEach-Object { $_.FullName })
$source += Join-Path $PSScriptRoot 'HistoryPreview.cs'
$source += Join-Path $PSScriptRoot 'MediaRenderer.cs'
$storage = Join-Path ([IO.Path]::GetTempPath()) ('AIMeter-history-preview-' + [Guid]::NewGuid().ToString('N'))
$previousHome = $env:AIMETER_HOME
try {
    $env:AIMETER_HOME = $root
    Add-Type -Path $source -CompilerParameters $parameters
    [AIMeter.HistoryPreview]::Run($Output, $storage, $Language)
    Write-Output "History previews: $Output"
}
catch {
    [Console]::Error.WriteLine($_.Exception.ToString())
    throw
}
finally {
    $env:AIMETER_HOME = $previousHome
    if (Test-Path $storage) { Remove-Item -LiteralPath $storage -Recurse -Force }
}
