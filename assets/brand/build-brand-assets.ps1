# Production brand asset pipeline for ChunkPilot Visual System v2.
#
# Draws the chosen mark ("Lift") natively at every target size, writes the PNG set and a
# multi-frame ICO, and validates optical occupancy. Nothing here downscales one large source:
# that is precisely the defect the previous icon had (61-65% frame occupancy at every size).
#
# Run:  powershell -File build-production-assets.ps1 -OutDir <dir>
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\..\artifacts\visual-system-v2-production\brand-review\production')
)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$sizes = 16,20,24,32,40,48,64,128,256,512

function New-Ctx { param([int]$S)
    $bmp=New-Object System.Drawing.Bitmap($S,$S,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g=[System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode=[System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.CompositingQuality=[System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.PixelOffsetMode=[System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent); @{Bmp=$bmp;G=$g;S=$S} }

# Micro frames fill almost the whole box; large frames get optical breathing room.
# 16-48 are the sizes Windows draws in the taskbar, title bar and Alt+Tab at 100/125/150%
# scaling, and those are the frames that must not look smaller than neighbouring apps.
function Get-Margin { param([int]$S)
    if ($S -le 20) { 0.015 } elseif ($S -le 32) { 0.025 } elseif ($S -le 48) { 0.030 }
    elseif ($S -le 64) { 0.050 } else { 0.065 } }

function Add-RR { param($P,$x,$y,$w,$h,$r)
    if ($r -lt 0.3) { $P.AddRectangle((New-Object System.Drawing.RectangleF([single]$x,[single]$y,[single]$w,[single]$h))); return }
    $d=$r*2
    $P.AddArc($x,$y,$d,$d,180,90); $P.AddArc(($x+$w-$d),$y,$d,$d,270,90)
    $P.AddArc(($x+$w-$d),($y+$h-$d),$d,$d,0,90); $P.AddArc($x,($y+$h-$d),$d,$d,90,90); $P.CloseFigure() }

function Draw-Mark { param($Ctx,[string]$Variant = 'colour')
    $S=$Ctx.S; $g=$Ctx.G; $m=(Get-Margin $S)*$S; $side=$S-2*$m
    $micro = $S -le 48
    $mono  = $Variant -ne 'colour'
    $inkLight = $Variant -eq 'mono-light'   # white knockout, for dark backgrounds

    $off  = $side * 0.155
    $body = $side - $off
    $r    = $body * 0.215
    $cut  = $body * 0.40
    $gap  = [Math]::Max(($side*0.075), 2.0)
    $tilt = if ($S -le 24) { 0 } else { 10 }

    if ($mono) {
        $c = if ($inkLight) { [System.Drawing.Color]::White } else { [System.Drawing.ColorTranslator]::FromHtml('#1B1B20') }
        $bBody = New-Object System.Drawing.SolidBrush($c)
    } elseif ($micro) {
        $bBody = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#8265E8'))
    } else {
        $rect = New-Object System.Drawing.RectangleF([single]$m,[single]($m+$off),[single]$body,[single]$body)
        $bBody = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect,
            [System.Drawing.ColorTranslator]::FromHtml('#8E72EC'),
            [System.Drawing.ColorTranslator]::FromHtml('#5B3FD0'),62.0)
    }

    $bp=New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-RR $bp $m ($m+$off) $body $body $r
    $g.FillPath($bBody,$bp)

    $old=$g.CompositingMode
    $g.CompositingMode=[System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $kb=New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::Transparent)
    $g.FillRectangle($kb,[single]($m+$body-$cut-$gap),[single]($m+$off-1),[single]($cut+$gap+2),[single]($cut+$gap))
    $g.CompositingMode=$old

    # The chip brush is built in the chip's own local space. Building it over the whole icon
    # rectangle and then rotating produced a visible diagonal seam across the chip.
    if ($mono) {
        $c2 = if ($inkLight) { [System.Drawing.Color]::White } else { [System.Drawing.ColorTranslator]::FromHtml('#1B1B20') }
        $bChip = New-Object System.Drawing.SolidBrush($c2)
    } elseif ($micro) {
        $bChip = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#BCA8FF'))
    } else {
        $local = New-Object System.Drawing.RectangleF([single](-$cut/2.0 - 1),[single](-$cut/2.0 - 1),[single]($cut + 2),[single]($cut + 2))
        $bChip = New-Object System.Drawing.Drawing2D.LinearGradientBrush($local,
            [System.Drawing.ColorTranslator]::FromHtml('#D3C6FF'),
            [System.Drawing.ColorTranslator]::FromHtml('#9B7DF5'),62.0)
    }

    $cx = $m + $side - $cut/2.0
    $cy = $m + $cut/2.0
    $st=$g.Save()
    $g.TranslateTransform([single]$cx,[single]$cy)
    $g.RotateTransform([single]$tilt)
    $cp=New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-RR $cp (-$cut/2.0) (-$cut/2.0) $cut $cut ($cut*0.26)
    $g.FillPath($bChip,$cp)
    $g.Restore($st)
    $cp.Dispose();$bp.Dispose();$kb.Dispose();$bBody.Dispose();$bChip.Dispose()
}

# ---- PNG set -------------------------------------------------------------
foreach ($s in $sizes) {
    foreach ($v in 'colour','mono-light','mono-dark') {
        $ctx=New-Ctx $s
        Draw-Mark $ctx $v
        $name = if ($v -eq 'colour') { "ChunkPilot-$s.png" } else { "ChunkPilot-$v-$s.png" }
        $ctx.Bmp.Save((Join-Path $OutDir $name),[System.Drawing.Imaging.ImageFormat]::Png)
        $ctx.G.Dispose();$ctx.Bmp.Dispose()
    }
}

# ---- Multi-frame ICO (PNG-compressed frames; supported since Windows Vista) ----
$icoSizes = 16,20,24,32,40,48,64,128,256
$icoPath = Join-Path $OutDir 'ChunkPilot.ico'
$streams = @()
foreach ($s in $icoSizes) {
    $ctx=New-Ctx $s
    Draw-Mark $ctx 'colour'
    $ms=New-Object System.IO.MemoryStream
    $ctx.Bmp.Save($ms,[System.Drawing.Imaging.ImageFormat]::Png)
    $ctx.G.Dispose();$ctx.Bmp.Dispose()
    $streams += ,@($s, $ms.ToArray())
    $ms.Dispose()
}
$fs=[System.IO.File]::Create($icoPath)
$bw=New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$streams.Count)   # ICONDIR
$offset = 6 + (16 * $streams.Count)
foreach ($e in $streams) {
    $sz=$e[0]; $bytes=$e[1]
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([Byte]$dim); $bw.Write([Byte]$dim)      # width, height (0 = 256)
    $bw.Write([Byte]0); $bw.Write([Byte]0)            # palette, reserved
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)       # planes, bpp
    $bw.Write([UInt32]$bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $bytes.Length
}
foreach ($e in $streams) { $bw.Write($e[1]) }
$bw.Flush(); $bw.Close(); $fs.Close()
Write-Output ("ico written: {0} ({1} frames, {2} bytes)" -f $icoPath, $streams.Count, (Get-Item $icoPath).Length)

# ---- Validation: optical occupancy -------------------------------------
Write-Output ''
Write-Output 'size  bbox        %frame  ink%   margins(L T R B)   verdict'
$fail = 0
foreach ($s in $sizes) {
    $bmp=New-Object System.Drawing.Bitmap((Join-Path $OutDir "ChunkPilot-$s.png"))
    $minX=$s;$minY=$s;$maxX=-1;$maxY=-1;$op=0
    for($y=0;$y -lt $s;$y++){ for($x=0;$x -lt $s;$x++){
        if ($bmp.GetPixel($x,$y).A -ge 40){ $op++
            if($x -lt $minX){$minX=$x}; if($x -gt $maxX){$maxX=$x}
            if($y -lt $minY){$minY=$y}; if($y -gt $maxY){$maxY=$y} } } }
    $bw2=$maxX-$minX+1; $bh2=$maxY-$minY+1
    $pct=[Math]::Round(100.0*[Math]::Max($bw2,$bh2)/$s,1)
    $ink=[Math]::Round(100.0*$op/($s*$s),1)
    # Size-aware gate. The micro frames are the ones that sit next to other apps in the
    # taskbar and must hold their own; the large frames are used where padding is expected.
    $bboxTarget = if ($s -le 48) { 94 } else { 85 }
    $ok = ($pct -ge $bboxTarget) -and ($ink -ge 45)
    if (-not $ok) { $fail++ }
    $v = if ($ok) { 'ok' } else { "BELOW TARGET (need $bboxTarget%)" }
    Write-Output ("{0,-5} {1,3}x{2,-3}   {3,5}   {4,5}   {5} {6} {7} {8}        {9}" -f `
        $s,$bw2,$bh2,$pct,$ink,$minX,$minY,($s-1-$maxX),($s-1-$maxY),$v)
    $bmp.Dispose()
}
Write-Output ''
Write-Output ("occupancy targets: bbox >= 94% of frame, ink >= 45% of frame. failures: {0}" -f $fail)
Write-Output 'reference - previous production icon: bbox 62.5%, ink 19.1% at every size.'
