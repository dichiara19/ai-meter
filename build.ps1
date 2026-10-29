# Builds AI Meter with the .NET Framework 4.8 C# compiler (included in Windows) and installs it for the current user.
#   powershell -ExecutionPolicy Bypass -File build.ps1            build and install
#   powershell -ExecutionPolicy Bypass -File build.ps1 -NoInstall build only (into .\bin)
#   -Mode exe     install the compiled executable (needs a signed executable if Smart App Control is on)
#   -Mode script  install the sources with the AIMeter.ps1 launcher (default: works with Smart App Control)
#   -Package      also build the MSIX package for the Microsoft Store (bin\AIMeter.msix; needs the Windows SDK)
#                 package identity from Partner Center: -IdentityName, -Publisher, -PublisherName
#                 (or the environment variables STORE_IDENTITY_NAME, STORE_PUBLISHER, STORE_PUBLISHER_NAME)
param(
    [switch]$NoInstall, [ValidateSet('script', 'exe')][string]$Mode = 'script',
    [switch]$Package,
    [string]$IdentityName = $(if ($env:STORE_IDENTITY_NAME) { $env:STORE_IDENTITY_NAME } else { 'dichiara19.AIMeter' }),
    [string]$Publisher = $(if ($env:STORE_PUBLISHER) { $env:STORE_PUBLISHER } else { 'CN=dichiara19' }),
    [string]$PublisherName = $(if ($env:STORE_PUBLISHER_NAME) { $env:STORE_PUBLISHER_NAME } else { 'dichiara19' })
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$bin = Join-Path $root 'bin'
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
New-Item -ItemType Directory -Force $bin | Out-Null

# --- app icon: two concentric gradient rings (Claude outside, ChatGPT inside), with no background like system icons
Add-Type -AssemblyName System.Drawing
# $size: icon side; $w x $h: image size (Start menu tiles centre the icon in a larger area)
function New-RingPng([int]$size, [int]$w = $size, [int]$h = $size) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TranslateTransform(($w - $size) / 2.0, ($h - $size) / 2.0)
    $s = $size / 16.0
    $c = $size / 2.0
    # Thick rings with a diagonal gradient (light from the top left): amber -> coral -> terracotta outside,
    # mint -> emerald -> teal inside; translucent grey tracks, readable on light and dark backgrounds.
    $ring = {
        param([double]$r, [double]$width, [int]$sweep, [string[]]$colors)
        $box = New-Object System.Drawing.RectangleF ([float]($c - $r - $width)), ([float]($c - $r - $width)), ([float](2 * ($r + $width))), ([float](2 * ($r + $width)))
        $track = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(60, 128, 128, 128)), $width
        $g.DrawEllipse($track, [float]($c - $r), [float]($c - $r), [float](2 * $r), [float](2 * $r))
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $box, ([System.Drawing.Color]::Black), ([System.Drawing.Color]::Black), 45.0
        $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
        $blend.Colors = [System.Drawing.Color[]]($colors | ForEach-Object { [System.Drawing.ColorTranslator]::FromHtml($_) })
        $blend.Positions = [float[]](0, 0.5, 1)
        $brush.InterpolationColors = $blend
        $pen = New-Object System.Drawing.Pen $brush, $width
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $g.DrawArc($pen, [float]($c - $r), [float]($c - $r), [float](2 * $r), [float](2 * $r), -90, $sweep)
        $pen.Dispose(); $brush.Dispose(); $track.Dispose()
    }
    & $ring (6.5 * $s) ([Math]::Max(1.6, 2.3 * $s)) 250 @('#F6A062', '#E06A45', '#B8432C')
    & $ring (3.3 * $s) ([Math]::Max(1.6, 2.4 * $s)) 190 @('#45DFA4', '#14A97E', '#0B6F5C')
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
[IO.File]::WriteAllBytes((Join-Path $root 'assets\logo.png'), (New-RingPng 256))   # README logo

# --- manifest (per-monitor DPI, Windows 10/11) and runtime configuration (lightweight GC)
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

# --- compilation
function Invoke-Csc([string]$out, [string[]]$extra = @()) {
    $refs = @(
        "$fw\WPF\PresentationCore.dll", "$fw\WPF\PresentationFramework.dll", "$fw\WPF\WindowsBase.dll",
        "$fw\System.Xaml.dll", "$fw\System.Web.Extensions.dll", "$fw\System.Windows.Forms.dll",
        "$fw\System.Drawing.dll", "$fw\System.Core.dll", "$fw\System.Security.dll", "$fw\System.dll"
    ) | ForEach-Object { "/r:$_" }
    $res = @('app.ico', 'antigravity.png' | ForEach-Object { "/resource:$(Join-Path $root "assets\$_"),$_" })
    # Service definitions and their logos embedded in the executable (when running from source they are read from the providers folder).
    $res += @(Get-ChildItem (Join-Path $root 'providers\*') -Include *.json, *.png | ForEach-Object { "/resource:$($_.FullName),providers.$($_.Name)" })
    $src = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
    & "$fw\csc.exe" /nologo /target:winexe /optimize+ /platform:anycpu /langversion:5 `
        "/out:$out" "/win32icon:$(Join-Path $root 'assets\app.ico')" "/win32manifest:$(Join-Path $bin 'app.manifest')" `
        @refs @res @extra @src
    if ($LASTEXITCODE -ne 0) { throw "Compilazione fallita" }
    Write-Host "Compilato: $out"
}
Invoke-Csc (Join-Path $bin 'AIMeter.exe')

# --- MSIX package for the Microsoft Store (the Store signs it: no SmartScreen or Smart App Control blocks)
if ($Package) {
    $layout = Join-Path $bin 'msix'
    $work = Join-Path $bin 'msix-work'
    foreach ($d in $layout, $work) { if (Test-Path $d) { Remove-Item $d -Recurse -Force }; New-Item -ItemType Directory $d | Out-Null }
    New-Item -ItemType Directory (Join-Path $layout 'Assets'), (Join-Path $layout 'assets') -Force | Out-Null

    # Same sources with the package's start-up task (Windows StartupTask API).
    $wm = Join-Path $env:WINDIR 'System32\WinMetadata'
    Invoke-Csc (Join-Path $layout 'AIMeter.exe') @('/define:MSIX', "/r:$fw\System.Runtime.dll",
        "/r:$wm\Windows.ApplicationModel.winmd", "/r:$wm\Windows.Foundation.winmd")
    Copy-Item (Join-Path $bin 'AIMeter.exe.config'), (Join-Path $root 'LICENSE'), (Join-Path $root 'NOTICE') $layout
    Copy-Item (Join-Path $root 'assets\app.ico') (Join-Path $layout 'assets')

    # Package images: taskbar and app list icon in several sizes, tiles, Store logo.
    $img = Join-Path $layout 'Assets'
    function Save-Png([string]$name, [byte[]]$png) { [IO.File]::WriteAllBytes((Join-Path $img $name), $png) }
    foreach ($t in 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256) {
        $png = New-RingPng $t
        Save-Png "Square44x44Logo.targetsize-$t.png" $png
        Save-Png "Square44x44Logo.targetsize-${t}_altform-unplated.png" $png
        Save-Png "Square44x44Logo.targetsize-${t}_altform-lightunplated.png" $png
    }
    foreach ($sc in 100, 125, 150, 200, 400) {
        $f = $sc / 100.0
        Save-Png "Square44x44Logo.scale-$sc.png" (New-RingPng ([int](36 * $f)) ([int](44 * $f)) ([int](44 * $f)))
        Save-Png "Square150x150Logo.scale-$sc.png" (New-RingPng ([int](72 * $f)) ([int](150 * $f)) ([int](150 * $f)))
        Save-Png "Wide310x150Logo.scale-$sc.png" (New-RingPng ([int](72 * $f)) ([int](310 * $f)) ([int](150 * $f)))
        Save-Png "StoreLogo.scale-$sc.png" (New-RingPng ([int](50 * $f)))
    }

    $version = ([regex]::Match((Get-Content (Join-Path $root 'src\AssemblyInfo.cs') -Raw), 'AssemblyVersion\("(\d+\.\d+\.\d+)')).Groups[1].Value + '.0'
    $esc = { param($s) [Security.SecurityElement]::Escape($s) }
    @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
         IgnorableNamespaces="uap desktop rescap">
  <Identity Name="$(& $esc $IdentityName)" Publisher="$(& $esc $Publisher)" Version="$version" ProcessorArchitecture="neutral"/>
  <Properties>
    <DisplayName>AI Meter</DisplayName>
    <PublisherDisplayName>$(& $esc $PublisherName)</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.22000.0" MaxVersionTested="10.0.26100.0"/>
  </Dependencies>
  <Resources>
    <Resource Language="en-us"/><Resource Language="it-it"/><Resource Language="es-es"/>
    <Resource Language="fr-fr"/><Resource Language="de-de"/><Resource Language="pt-pt"/>
  </Resources>
  <Applications>
    <Application Id="App" Executable="AIMeter.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="AI Meter" Description="Sessions and usage limits of your AI services"
                          BackgroundColor="transparent" Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png">
        <uap:DefaultTile Wide310x150Logo="Assets\Wide310x150Logo.png"/>
      </uap:VisualElements>
      <Extensions>
        <desktop:Extension Category="windows.startupTask" Executable="AIMeter.exe" EntryPoint="Windows.FullTrustApplication">
          <desktop:StartupTask TaskId="AIMeterStartup" Enabled="false" DisplayName="AI Meter"/>
        </desktop:Extension>
      </Extensions>
    </Application>
  </Applications>
  <Capabilities>
    <Capability Name="internetClient"/>
    <rescap:Capability Name="runFullTrust"/>
  </Capabilities>
</Package>
"@ | Set-Content (Join-Path $layout 'AppxManifest.xml') -Encoding UTF8

    # makepri and makeappx from the Windows SDK (present on GitHub Actions runners).
    $kit = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName | Select-Object -Last 1
    if (-not $kit) { Write-Warning "Windows SDK non trovato: contenuto del pacchetto pronto in $layout, MSIX non creato"; return }
    $sdk = $kit.DirectoryName
    $cfg = Join-Path $work 'priconfig.xml'
    & "$sdk\makepri.exe" createconfig /cf $cfg /dq en-US /pv 10.0.0 /o | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "makepri createconfig fallito" }
    # A single resources.pri with every scale (no separate resource packages, which only bundles need)
    # and the six interface languages as package languages.
    $x = [IO.File]::ReadAllText($cfg)
    $x = [regex]::Replace($x, '(?s)<packaging>.*?</packaging>', '')
    $x = $x.Replace('name="Language" value="en-US"', 'name="Language" value="en-US;it-IT;es-ES;fr-FR;de-DE;pt-PT"')
    [IO.File]::WriteAllText($cfg, $x)
    & "$sdk\makepri.exe" new /pr $layout /cf $cfg /mn (Join-Path $layout 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o
    if ($LASTEXITCODE -ne 0) { throw "makepri fallito" }
    $msix = Join-Path $bin 'AIMeter.msix'
    & "$sdk\makeappx.exe" pack /d $layout /p $msix /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx fallito" }
    Write-Host "Pacchetto: $msix (versione $version, non firmato: lo firma il Microsoft Store)"
    return
}
if ($NoInstall) { return }

# --- installation for the current user (no administrator rights)
$dest = Join-Path $env:LOCALAPPDATA 'Programs\AI Meter'

# Stop the running instance, whether executable or script.
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
}
else {
    New-Item -ItemType Directory -Force (Join-Path $dest 'src'), (Join-Path $dest 'assets'), (Join-Path $dest 'providers') | Out-Null
    Copy-Item (Join-Path $root 'src\*.cs') (Join-Path $dest 'src') -Force
    Copy-Item (Join-Path $root 'providers\*') (Join-Path $dest 'providers') -Include *.json, *.png -Force
    Copy-Item (Join-Path $root 'assets\app.ico'), (Join-Path $root 'assets\antigravity.png') (Join-Path $dest 'assets') -Force
    Copy-Item (Join-Path $root 'AIMeter.ps1') $dest -Force
    $target = Join-Path $env:WINDIR 'System32\conhost.exe'
    $arguments = "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -Sta -File `"$(Join-Path $dest 'AIMeter.ps1')`""
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

# Start-up, if enabled, follows the installed mode: a shortcut in the Startup folder
# (the old entry in the Run key moves there).
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'AI Meter.lnk'
if ((Get-ItemProperty $runKey -Name 'AI Meter' -ErrorAction SilentlyContinue) -or (Test-Path $startup)) {
    Remove-ItemProperty $runKey -Name 'AI Meter' -ErrorAction SilentlyContinue
    $a = $sh.CreateShortcut($startup)
    $a.TargetPath = $target
    $a.Arguments = "$arguments --startup".Trim()
    $a.WorkingDirectory = $dest
    $a.IconLocation = $s.IconLocation
    $a.Description = 'AI Meter'
    $a.Save()
}
Write-Host "Installato in $dest (modalità $Mode, collegamento nel menu Start)"