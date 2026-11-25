# Renders Settings > MCP > Delegation agent (docs\media\delegation-*.png) from real WPF controls and an isolated
# fixture. No running app, network or saved accounts are used.
#   powershell -Sta -ExecutionPolicy Bypass -File tools\render-mcp.ps1 [-Language en]
param([string]$Output = '', [string]$Language = 'en')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Output) { $Output = Join-Path $root 'docs\media' }
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$parameters = New-Object System.CodeDom.Compiler.CompilerParameters
$parameters.GenerateInMemory = $true
$parameters.CompilerOptions = '/langversion:5 /define:AIMETER_TEST'
@(
    "$fw\WPF\PresentationCore.dll", "$fw\WPF\PresentationFramework.dll", "$fw\WPF\WindowsBase.dll",
    "$fw\System.Xaml.dll", "$fw\System.Web.Extensions.dll", "$fw\System.Windows.Forms.dll",
    "$fw\System.Drawing.dll", "$fw\System.Core.dll", "$fw\System.Security.dll", "$fw\System.IO.Compression.dll", "$fw\System.IO.Compression.FileSystem.dll", "$fw\System.dll"
) | ForEach-Object { [void]$parameters.ReferencedAssemblies.Add($_) }
$source = @(Get-ChildItem (Join-Path $root 'src\*.cs') | ForEach-Object { $_.FullName })
$source += Join-Path $PSScriptRoot 'McpPreview.cs'
$source += Join-Path $PSScriptRoot 'MediaRenderer.cs'
$storage = Join-Path ([IO.Path]::GetTempPath()) ('AIMeter-mcp-preview-' + [Guid]::NewGuid().ToString('N'))
$previousHome = $env:AIMETER_HOME
try {
    $env:AIMETER_HOME = $root
    Add-Type -Path $source -CompilerParameters $parameters
    [AIMeter.McpPreview]::Run($Output, $storage, $Language)
    Write-Output "Delegation agent page: $Output"
}
catch {
    [Console]::Error.WriteLine($_.Exception.ToString())
    throw
}
finally {
    $env:AIMETER_HOME = $previousHome
    if (Test-Path $storage) { Remove-Item -LiteralPath $storage -Recurse -Force }
}
