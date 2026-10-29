# Starts AI Meter from source, without an executable.
# With Smart App Control on, an unsigned .exe is blocked; PowerShell, signed by Microsoft,
# compiles the C# code in memory (Add-Type) and runs it. The result is the same app.
#
# Typical use (no console window):
#   conhost.exe --headless powershell.exe -NoProfile -ExecutionPolicy Bypass -Sta -File AIMeter.ps1 [--settings]
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$AppArgs = @())
$ErrorActionPreference = 'Stop'

# Already running: signal the active instance without recompiling (opens the panel or Settings).
$running = $null
if ([Threading.Mutex]::TryOpenExisting('Local\AIMeter.Instance', [ref]$running)) {
    $name = if ($AppArgs -contains '--settings') { 'Local\AIMeter.Settings' } else { 'Local\AIMeter.Show' }
    $signal = $null
    if ([Threading.EventWaitHandle]::TryOpenExisting($name, [ref]$signal)) { [void]$signal.Set() }
    return
}

$here = $PSScriptRoot
$env:AIMETER_HOME = $here
$env:AIMETER_LAUNCH = "conhost.exe --headless powershell.exe -NoProfile -ExecutionPolicy Bypass -Sta -File `"$PSCommandPath`""

$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = @(
    "$fw\WPF\PresentationCore.dll", "$fw\WPF\PresentationFramework.dll", "$fw\WPF\WindowsBase.dll",
    "$fw\System.Xaml.dll", "$fw\System.Web.Extensions.dll", "$fw\System.Windows.Forms.dll",
    "$fw\System.Drawing.dll", "$fw\System.Core.dll", "$fw\System.Security.dll"
)
$src = Get-ChildItem (Join-Path $here 'src') -Filter *.cs | ForEach-Object { $_.FullName }

try {
    Add-Type -Path $src -ReferencedAssemblies $refs -IgnoreWarnings
    [AIMeter.Entry]::Run([string[]]$AppArgs)
}
catch {
    $log = Join-Path $env:APPDATA 'AIMeter\launcher.log'
    New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
    # The full exception, with the stack trace and the inner one: without it, "Access denied" does not say where.
    $detail = if ($_.Exception.InnerException) { $_.Exception.InnerException.ToString() } else { $_.Exception.ToString() }
    "$(Get-Date -Format s)  $detail" | Add-Content $log -Encoding UTF8
    Add-Type -AssemblyName System.Windows.Forms
    [void][System.Windows.Forms.MessageBox]::Show("AI Meter non è riuscito ad avviarsi. Dettagli in:`n$log", 'AI Meter')
    exit 1
}
