# Compila o Ghost Key Monitor usando o csc.exe que ja vem no Windows (.NET Framework 4.x).
# Nao precisa instalar nada.   Uso:  powershell -ExecutionPolicy Bypass -File .\build.ps1
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe nao encontrado em $csc" }

if (-not $SkipTests) {
    Write-Host "== Testes do detector =="
    & $csc /nologo /codepage:65001 /target:exe /out:DetectorTests.exe DetectorTests.cs GhostKeyDetector.cs
    if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar os testes" }
    & .\DetectorTests.exe
    $code = $LASTEXITCODE
    Remove-Item DetectorTests.exe -ErrorAction SilentlyContinue
    if ($code -ne 0) { throw "Testes falharam" }
}

# Icone do app (32x32), gerado com System.Drawing - o mesmo desenho da bandeja.
Write-Host "== Gerando app.ico =="
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 32, 32
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(40, 44, 52))), 1, 8, 30, 20)
$keyBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(190, 195, 205))
foreach ($row in 0..2) { foreach ($col in 0..4) {
    if ($row -eq 2 -and $col -gt 3) { continue }
    $g.FillRectangle($keyBrush, (4 + $col * 5), (11 + $row * 5), 3, 3)
} }
$g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(60, 200, 110))), 20, 1, 11, 11)
$g.Dispose()
$icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
$fs = [System.IO.File]::Create((Join-Path $PSScriptRoot 'app.ico'))
$icon.Save($fs); $fs.Close(); $bmp.Dispose()

Write-Host "== Compilando GhostKeyMonitor.exe =="
& $csc /nologo /codepage:65001 /target:winexe /optimize+ /win32icon:app.ico /out:GhostKeyMonitor.exe `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    GhostKeyMonitor.cs GhostKeyDetector.cs
if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar o app" }
Write-Host "OK: $(Join-Path $PSScriptRoot 'GhostKeyMonitor.exe')"
