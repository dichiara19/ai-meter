# Regenerates the README images (docs\media) with the real interface components and the demo data.
#   powershell -ExecutionPolicy Bypass -File tools\render-media.ps1 [-Lang en]
# The GIF requires ffmpeg (on Windows or in WSL).
param([string]$Lang = 'en')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'docs\media'
$frames = Join-Path $env:TEMP 'aimeter-frames'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    & powershell -NoProfile -Sta -ExecutionPolicy Bypass -File $PSCommandPath -Lang $Lang
    exit $LASTEXITCODE
}
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$refs = "$fw\WPF\PresentationCore.dll", "$fw\WPF\PresentationFramework.dll", "$fw\WPF\WindowsBase.dll", "$fw\System.Xaml.dll",
        "$fw\System.Web.Extensions.dll", "$fw\System.Windows.Forms.dll", "$fw\System.Drawing.dll", "$fw\System.Core.dll", "$fw\System.Security.dll"
$env:AIMETER_HOME = $root
$src = @(Get-ChildItem (Join-Path $root 'src\*.cs') | ForEach-Object FullName) + (Join-Path $PSScriptRoot 'MediaRenderer.cs')
Add-Type -Path $src -ReferencedAssemblies $refs
Write-Host ([AIMeter.MediaRenderer]::Run($out, $frames, $Lang))

# GIF: no alpha channel (so each frame stores only what changes), palette from the frames, infinite loop.
$gif = Join-Path $out 'demo.gif'
$vf = 'fps=15,format=rgb24,split[a][b];[a]palettegen=max_colors=128:stats_mode=full:reserve_transparent=0[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle'
if (Get-Command ffmpeg -ErrorAction SilentlyContinue) {
    & ffmpeg -y -loglevel error -framerate 15 -i (Join-Path $frames 'f%03d.png') -vf $vf -loop 0 $gif
} else {
    $w = { param($p) '/mnt/' + $p.Substring(0, 1).ToLower() + $p.Substring(2).Replace('\', '/') }
    & wsl.exe -e ffmpeg -y -loglevel error -framerate 15 -i ((& $w $frames) + '/f%03d.png') -vf $vf -loop 0 (& $w $gif)
}
Get-ChildItem $out | Select-Object Name, @{ n = 'KB'; e = { [math]::Round($_.Length / 1KB) } } | Format-Table -AutoSize
