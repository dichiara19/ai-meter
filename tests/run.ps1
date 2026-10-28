# Compile the real application sources in memory, with storage isolated from the installed app.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
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
$source += @(Get-ChildItem (Join-Path $PSScriptRoot '*.cs') | ForEach-Object { $_.FullName })
Add-Type -Path $source -CompilerParameters $parameters
$testData = Join-Path ([IO.Path]::GetTempPath()) ('AIMeter-tests-' + [Guid]::NewGuid().ToString('N'))
try {
    [AIMeter.RegressionTests]::Run($testData)
}
finally {
    if (Test-Path $testData) { Remove-Item -LiteralPath $testData -Recurse -Force }
}
