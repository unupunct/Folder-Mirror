# Generates assets/FolderMirror.ico and assets/logo.png (two folders + two opposing arrows).
Add-Type -AssemblyName System.Drawing
$out = Join-Path (Split-Path $PSScriptRoot -Parent) 'assets'
New-Item -ItemType Directory -Force $out | Out-Null

function New-RoundRect([float]$x,[float]$y,[float]$w,[float]$h,[float]$r){
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $r*2
  $p.AddArc($x,$y,$d,$d,180,90); $p.AddArc($x+$w-$d,$y,$d,$d,270,90)
  $p.AddArc($x+$w-$d,$y+$h-$d,$d,$d,0,90); $p.AddArc($x,$y+$h-$d,$d,$d,90,90); $p.CloseFigure(); return $p
}

function Draw-Logo([int]$size){
  $bmp = New-Object System.Drawing.Bitmap $size,$size,([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode='HighQuality'; $g.InterpolationMode='HighQualityBicubic'
  $g.ScaleTransform($size/256.0,$size/256.0)
  # background
  $bg = New-RoundRect 8 8 240 240 52
  $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0,0)),([System.Drawing.PointF]::new(256,256)),([System.Drawing.Color]::FromArgb(255,44,50,56)),([System.Drawing.Color]::FromArgb(255,22,25,28))
  $g.FillPath($br,$bg)
  $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(90,61,220,132)),3
  $g.DrawPath($pen,$bg)
  # folders
  foreach($f in @(@(30,@(61,220,132),@(36,170,100)),@(142,@(190,198,206),@(140,150,160)))){
    $x=[float]$f[0]; $c1=$f[1]; $c2=$f[2]
    $tab = New-RoundRect $x 46 46 24 8
    $body = New-RoundRect $x 58 84 74 12
    $fb = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new($x,58)),([System.Drawing.PointF]::new($x,132)),([System.Drawing.Color]::FromArgb(255,$c1[0],$c1[1],$c1[2])),([System.Drawing.Color]::FromArgb(255,$c2[0],$c2[1],$c2[2]))
    $g.FillPath($fb,$tab); $g.FillPath($fb,$body)
  }
  # opposing arrows
  $ap = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255,61,220,132)),15
  $ap.StartCap='Round'; $ap.EndCap='Round'; $ap.LineJoin='Round'
  $g.DrawLine($ap,52,166,196,166)
  $g.DrawLines($ap,@([System.Drawing.PointF]::new(170,142),[System.Drawing.PointF]::new(198,166),[System.Drawing.PointF]::new(170,190)))
  $bp = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255,220,226,232)),15
  $bp.StartCap='Round'; $bp.EndCap='Round'; $bp.LineJoin='Round'
  $g.DrawLine($bp,204,210,60,210)
  $g.DrawLines($bp,@([System.Drawing.PointF]::new(86,186),[System.Drawing.PointF]::new(58,210),[System.Drawing.PointF]::new(86,234)))
  $g.Dispose(); return $bmp
}

$sizes = 16,20,24,32,40,48,64,128,256
$pngs = @{}
foreach($s in $sizes){
  $b = Draw-Logo $s; $ms = New-Object System.IO.MemoryStream
  $b.Save($ms,[System.Drawing.Imaging.ImageFormat]::Png); $pngs[$s]=$ms.ToArray(); $b.Dispose()
}
(Draw-Logo 512).Save((Join-Path $out 'logo.png'),[System.Drawing.Imaging.ImageFormat]::Png)

$fs = [System.IO.File]::Create((Join-Path $out 'FolderMirror.ico'))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16*$sizes.Count
foreach($s in $sizes){
  $w = if($s -ge 256){0}else{$s}
  $bw.Write([byte]$w); $bw.Write([byte]$w); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$s].Length); $bw.Write([uint32]$offset)
  $offset += $pngs[$s].Length
}
foreach($s in $sizes){ $bw.Write($pngs[$s]) }
$bw.Close(); $fs.Close()
Write-Host "Icon written to $out"
