# Builds the single-file FolderMirror.exe and the Inno Setup installer into .\release
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
dotnet test tests/FolderMirror.Tests -c Release --nologo
if ($LASTEXITCODE) { throw 'tests failed' }
Remove-Item publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish src/FolderMirror -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish --nologo
if ($LASTEXITCODE) { throw 'publish failed' }
New-Item -ItemType Directory -Force release | Out-Null
Copy-Item publish\FolderMirror.exe release\FolderMirror.exe -Force
$extra = Get-ChildItem publish -File | Where-Object { $_.Name -ne 'FolderMirror.exe' }
if ($extra) { throw "Publish output contains extra files: $($extra.Name -join ', ')" }
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found' }
& $iscc installer\FolderMirror.iss
Get-ChildItem release | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}
