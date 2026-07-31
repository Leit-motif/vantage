<#
.SYNOPSIS
  Captures a rectangle of the real screen to a PNG.

.DESCRIPTION
  Ticket #6 asks for frames of the running overlay on the real desktop, which means capturing what
  the compositor actually put on screen — the translucent surface with the target application
  showing through it. Rendering the window's own visual tree cannot show that, because what sits
  behind the surface is not part of the window.

  The rectangle is given in physical pixels and is expected to be cropped to the overlay and its
  target application, so that nothing else on the owner's desktop is in the frame.

.PARAMETER Rect
  Left, Top, Width, Height in physical pixels.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [int[]] $Rect,
    [Parameter(Mandatory)] [string] $Path
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Per-monitor DPI aware, so the coordinates below are physical pixels rather than the virtualized
# ones a DPI-unaware process is handed.
if (-not ('DpiAware' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class DpiAware {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
  public static void Apply() { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
}
'@ -Language CSharp
}
[DpiAware]::Apply() | Out-Null

$bmp = New-Object System.Drawing.Bitmap($Rect[2], $Rect[3])
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($Rect[0], $Rect[1], 0, 0, (New-Object System.Drawing.Size($Rect[2], $Rect[3])))
$g.Dispose()

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
$bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Host ("{0}  {1}x{2} at ({3},{4})  {5} KB" -f $Path, $Rect[2], $Rect[3], $Rect[0], $Rect[1], [math]::Round((Get-Item $Path).Length / 1KB))
