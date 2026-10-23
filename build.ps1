# Compila AI Meter con il compilatore C# di .NET Framework 4.8 (già incluso in Windows) e lo installa per l'utente.
#   powershell -ExecutionPolicy Bypass -File build.ps1            compila e installa
#   powershell -ExecutionPolicy Bypass -File build.ps1 -NoInstall compila soltanto (in .\bin)
#   -Mode exe     installa l'eseguibile compilato (serve un eseguibile firmato se Smart App Control è attivo)
#   -Mode script  installa il sorgente con il launcher AIMeter.ps1 (predefinito: funziona con Smart App Control)
param([switch]$NoInstall, [ValidateSet('script', 'exe')][string]$Mode = 'script')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$bin = Join-Path $root 'bin'
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
New-Item -ItemType Directory -Force $bin | Out-Null

# --- icona dell'app: due anelli concentrici (Claude fuori, ChatGPT dentro), senza sfondo come le icone di sistema
Add-Type -AssemblyName System.Drawing
function New-RingPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $s = $size / 16.0
    $w = [Math]::Max(1.6, 2.3 * $s)
    $track = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 128, 128, 128)), $w
    $outer = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 217, 119, 87)), $w
    $inner = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 16, 163, 127)), $w
    foreach ($p in $outer, $inner) { $p.StartCap = 'Round'; $p.EndCap = 'Round' }
    $r1 = 6.6 * $s; $r2 = 3.3 * $s; $c = $size / 2.0
    $g.DrawEllipse($track, $c - $r1, $c - $r1, 2 * $r1, 2 * $r1)
    $g.DrawEllipse($track, $c - $r2, $c - $r2, 2 * $r2, 2 * $r2)
    $g.DrawArc($outer, $c - $r1, $c - $r1, 2 * $r1, 2 * $r1, -90, 250)
    $g.DrawArc($inner, $c - $r2, $c - $r2, 2 * $r2, 2 * $r2, -90, 190)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$pngs = $sizes | ForEach-Object { ,(New-RingPng $_) }
$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ico
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz }))); $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset); $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()
[IO.File]::WriteAllBytes((Join-Path $root 'assets\app.ico'), $ico.ToArray())

# --- manifest (DPI per monitor, Windows 10/11) e configurazione runtime (GC leggero)
@'
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="2.0.0.0" name="AIMeter"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="asInvoker" uiAccess="false"/></requestedPrivileges></security></trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1"><application><supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/></application></compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3"><windowsSettings>
    <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
    <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
  </windowsSettings></application>
</assembly>
'@ | Set-Content (Join-Path $bin 'app.manifest') -Encoding UTF8

@'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8"/></startup>
  <runtime>
    <gcConcurrent enabled="false"/>
    <AppContextSwitchOverrides value="Switch.System.Windows.DoNotScaleForDpiChanges=false"/>
  </runtime>
</configuration>
'@ | Set-Content (Join-Path $bin 'AIMeter.exe.config') -Encoding UTF8

# --- compilazione
$refs = @(
    "$fw\WPF\PresentationCore.dll", "$fw\WPF\PresentationFramework.dll", "$fw\WPF\WindowsBase.dll",
    "$fw\System.Xaml.dll", "$fw\System.Web.Extensions.dll", "$fw\System.Windows.Forms.dll",
    "$fw\System.Drawing.dll", "$fw\System.Core.dll", "$fw\System.dll"
) | ForEach-Object { "/r:$_" }
$res = @('app.ico' | ForEach-Object { "/resource:$(Join-Path $root "assets\$_"),$_" })
$src = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
& "$fw\csc.exe" /nologo /target:winexe /optimize+ /platform:anycpu /langversion:5 `
    "/out:$(Join-Path $bin 'AIMeter.exe')" "/win32icon:$(Join-Path $root 'assets\app.ico')" "/win32manifest:$(Join-Path $bin 'app.manifest')" `
    @refs @res @src
if ($LASTEXITCODE -ne 0) { throw "Compilazione fallita" }
Write-Host "Compilato: $bin\AIMeter.exe"
if ($NoInstall) { return }

# --- installazione per l'utente corrente (nessun permesso di amministratore)
$dest = Join-Path $env:LOCALAPPDATA 'Programs\AI Meter'

# ferma l'istanza in esecuzione, sia come eseguibile sia come script
Get-Process AIMeter -ErrorAction SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.CommandLine -like '*AIMeter.ps1*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 500

if (Test-Path $dest) { Remove-Item (Join-Path $dest '*') -Recurse -Force }
New-Item -ItemType Directory -Force $dest | Out-Null
if ($Mode -eq 'exe') {
    Copy-Item (Join-Path $bin 'AIMeter.exe'), (Join-Path $bin 'AIMeter.exe.config') $dest -Force
    $target = Join-Path $dest 'AIMeter.exe'; $arguments = ''
    $run = "`"$target`""
}
else {
    New-Item -ItemType Directory -Force (Join-Path $dest 'src'), (Join-Path $dest 'assets') | Out-Null
    Copy-Item (Join-Path $root 'src\*.cs') (Join-Path $dest 'src') -Force
    Copy-Item (Join-Path $root 'assets\app.ico') (Join-Path $dest 'assets') -Force
    Copy-Item (Join-Path $root 'AIMeter.ps1') $dest -Force
    $target = Join-Path $env:WINDIR 'System32\conhost.exe'
    $arguments = "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -Sta -File `"$(Join-Path $dest 'AIMeter.ps1')`""
    $run = "conhost.exe $arguments"
}

$lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\AI Meter.lnk'
$sh = New-Object -ComObject WScript.Shell
$s = $sh.CreateShortcut($lnk)
$s.TargetPath = $target
$s.Arguments = $arguments
$s.WorkingDirectory = $dest
$s.IconLocation = (Join-Path $dest 'assets\app.ico') -replace '^(.*)$', '$1,0'
if ($Mode -eq 'exe') { $s.IconLocation = "$target,0" }
$s.Description = 'Sessioni e limiti dei modelli AI'
$s.Save()

# l'avvio automatico, se attivo, segue la modalità installata
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty $runKey -Name 'AI Meter' -ErrorAction SilentlyContinue) {
    Set-ItemProperty $runKey -Name 'AI Meter' -Value "$run --startup"
}
Write-Host "Installato in $dest (modalità $Mode, collegamento nel menu Start)"