# Avvia AI Meter dal sorgente, senza eseguibile.
# Con Smart App Control attivo un .exe non firmato viene bloccato; PowerShell, firmato da Microsoft,
# compila il codice C# in memoria (Add-Type) e lo esegue. Il risultato è la stessa app.
#
# Uso tipico (nessuna finestra di console):
#   conhost.exe --headless powershell.exe -NoProfile -ExecutionPolicy Bypass -Sta -File AIMeter.ps1 [--settings]
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$AppArgs = @())
$ErrorActionPreference = 'Stop'

# Già in esecuzione: segnala all'istanza attiva senza ricompilare (apre il pannello o le impostazioni).
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
    "$(Get-Date -Format s)  $($_ | Out-String)" | Add-Content $log -Encoding UTF8
    Add-Type -AssemblyName System.Windows.Forms
    [void][System.Windows.Forms.MessageBox]::Show("AI Meter non è riuscito ad avviarsi. Dettagli in:`n$log", 'AI Meter')
    exit 1
}
