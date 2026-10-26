param(
    [string] $OutputPath = (Join-Path $PSScriptRoot '..\WhatsAppTasks.ico')
)

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NativeIcon {
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);
}
'@

$bitmap = [System.Drawing.Bitmap]::new(32, 32)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)
$green = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(37, 211, 102))
$graphics.FillEllipse($green, 1, 1, 30, 30)
$white = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 2.2)
$graphics.DrawArc($white, 8, 8, 14, 14, 30, 280)
$graphics.DrawLine($white, 10, 20, 7, 24)
$graphics.DrawLine($white, 7, 24, 12, 23)
$clockFace = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
$graphics.FillEllipse($clockFace, 20, 1, 11, 11)
$clock = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(37, 120, 70), 1.2)
$graphics.DrawEllipse($clock, 20, 1, 11, 11)
$graphics.DrawLine($clock, 25.5, 3.5, 25.5, 7)
$graphics.DrawLine($clock, 25.5, 7, 28, 8)
$handle = $bitmap.GetHicon()
try {
    $source = [System.Drawing.Icon]::FromHandle($handle)
    $icon = [System.Drawing.Icon]$source.Clone()
    try {
        $stream = [System.IO.File]::Open($OutputPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
        try { $icon.Save($stream) } finally { $stream.Dispose() }
    } finally { $icon.Dispose() }
} finally {
    [NativeIcon]::DestroyIcon($handle) | Out-Null
    $clock.Dispose(); $clockFace.Dispose(); $white.Dispose(); $green.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
