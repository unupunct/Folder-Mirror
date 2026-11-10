# Trims the empty right margin from wizard screenshots (docs/screenshots/wizard-*.png)
Add-Type -AssemblyName System.Drawing
foreach($f in Get-ChildItem "$PSScriptRoot\..\docs\screenshots\wizard-*.png"){
  $b=[System.Drawing.Bitmap]::FromFile($f.FullName)
  if($b.Width -gt 1320){
    $rect = New-Object System.Drawing.Rectangle 0,0,1310,$b.Height
    $c=$b.Clone($rect,$b.PixelFormat); $b.Dispose(); $tmp=$f.FullName+'.tmp'; $c.Save($tmp,[System.Drawing.Imaging.ImageFormat]::Png); $c.Dispose()
    Move-Item $tmp $f.FullName -Force
  } else { $b.Dispose() }
}
