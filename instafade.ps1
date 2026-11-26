<#
  osu! Instafade Skin Tool  by LTH
  ---------------------------------------------
  osu! 스킨(.osk)에서 "서클을 친 뒤 남는 잔상(퍼지면서 사라지는 효과)"을 없애 줍니다.

  원리: osu!(stable)에서 서클 이미지(hitcircle/hitcircleoverlay)는 치고 나면 커지면서 서서히
  사라지지만, 서클 위 숫자(default-N)는 치는 순간 바로 사라집니다.
  그래서 서클 그림을 숫자 그림 안에 합쳐 넣고, 원래 서클 파일은 투명하게 만듭니다.

  사용법:
    powershell -ExecutionPolicy Bypass -File instafade.ps1 -Skin breakcore.osk
    powershell -ExecutionPolicy Bypass -File instafade.ps1 -Skin breakcore.osk -NoNumbers

  -NoNumbers : 숫자 없이 서클만 (콤보 10 이상에서도 숫자가 겹치지 않음)
  -ComboColor "r,g,b" : 서클 색 직접 지정 (기본값: skin.ini 의 Combo1)
#>
param(
    [Parameter(Mandatory = $true)][string]$Skin,
    [switch]$NoNumbers,
    [string]$ComboColor = "",
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression.FileSystem

$Skin = (Resolve-Path $Skin).Path
if (-not $OutDir) { $OutDir = Split-Path $Skin -Parent }
$baseName = [IO.Path]::GetFileNameWithoutExtension($Skin)
$suffix = if ($NoNumbers) { "_instafade_nonumber" } else { "_instafade" }
$outOsk = Join-Path $OutDir ($baseName + $suffix + ".osk")

# ---- 1. 압축 풀기 ----
$work = Join-Path ([IO.Path]::GetTempPath()) ("instafade_" + [guid]::NewGuid().ToString("N"))
[IO.Compression.ZipFile]::ExtractToDirectory($Skin, $work)
# .osk 안에 폴더가 한 겹 더 있는 경우 처리
if (-not (Test-Path (Join-Path $work "skin.ini"))) {
    $sub = Get-ChildItem $work -Directory | Where-Object { Test-Path (Join-Path $_.FullName "skin.ini") } | Select-Object -First 1
    if ($sub) { $root = $sub.FullName } else { $root = $work }
} else { $root = $work }

$iniPath = Join-Path $root "skin.ini"
$ini = if (Test-Path $iniPath) { [IO.File]::ReadAllLines($iniPath) } else { @("[General]") }

function Get-IniValue($key, $default) {
    foreach ($l in $ini) {
        if ($l -match "^\s*$key\s*:\s*(.+?)\s*$") { return $Matches[1] }
    }
    return $default
}

$prefix = (Get-IniValue "HitCirclePrefix" "default") -replace '\\', '/'
if (-not $ComboColor) { $ComboColor = Get-IniValue "Combo1" "255,192,0" }
$rgb = $ComboColor -split '\s*,\s*' | ForEach-Object { [int]$_ }
Write-Host "HitCirclePrefix = $prefix / 서클 색 = $($rgb -join ',')"

# ---- 이미지 도우미 ----
function New-Canvas($w, $h) {
    $b = New-Object System.Drawing.Bitmap ([int]$w), ([int]$h), ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $b.SetResolution(96, 96)
    return $b
}
function New-Gfx($bmp) {
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    return $g
}
# 이미지를 (x,y)에 (w,h) 크기로 그림. matrix 가 있으면 색을 곱해서(틴트) 그림
function Draw-Image($g, $img, [double]$x, [double]$y, [double]$w, [double]$h, $matrix = $null) {
    $attr = New-Object System.Drawing.Imaging.ImageAttributes
    $attr.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
    if ($matrix) { $attr.SetColorMatrix($matrix) }
    $dest = New-Object System.Drawing.RectangleF ([single]$x), ([single]$y), ([single]$w), ([single]$h)
    $g.DrawImage($img, [System.Drawing.Rectangle]::Round($dest), 0, 0, $img.Width, $img.Height, [System.Drawing.GraphicsUnit]::Pixel, $attr)
    $attr.Dispose()
}
function Open-Image($path) {
    # 파일 잠금 없이 열기
    $bytes = [IO.File]::ReadAllBytes($path)
    $ms = New-Object IO.MemoryStream (, $bytes)
    $img = [System.Drawing.Image]::FromStream($ms)
    $copy = New-Canvas $img.Width $img.Height
    $g = [System.Drawing.Graphics]::FromImage($copy); $g.DrawImage($img, 0, 0, $img.Width, $img.Height); $g.Dispose()
    $img.Dispose(); $ms.Dispose()
    return $copy
}
# 이름에 맞는 이미지를 HD(@2x) 크기로 가져옴. 없으면 $null
function Get-HDImage($name) {
    $hd = Join-Path $root "$name@2x.png"
    $sd = Join-Path $root "$name.png"
    if (Test-Path $hd) { return Open-Image $hd }
    if (Test-Path $sd) {
        $s = Open-Image $sd
        $b = New-Canvas ($s.Width * 2) ($s.Height * 2)
        $g = New-Gfx $b; Draw-Image $g $s 0 0 $b.Width $b.Height; $g.Dispose(); $s.Dispose()
        return $b
    }
    return $null
}
# HD 이미지를 name@2x.png 와 name.png(절반 크기) 두 개로 저장
function Save-Pair($bmp, $name) {
    $dir = Split-Path (Join-Path $root "$name.png") -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $bmp.Save((Join-Path $root "$name@2x.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $sd = New-Canvas ([Math]::Max(1, [int]($bmp.Width / 2))) ([Math]::Max(1, [int]($bmp.Height / 2)))
    $g = New-Gfx $sd; Draw-Image $g $bmp 0 0 $sd.Width $sd.Height; $g.Dispose()
    $sd.Save((Join-Path $root "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $sd.Dispose()
}
function Save-Transparent($path) {
    $b = New-Canvas 1 1; $b.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
}

# ---- 2. 서클 합치기 (hitcircle 에 콤보 색 입히고 그 위에 overlay) ----
$circle = Get-HDImage "hitcircle"
$overlay = Get-HDImage "hitcircleoverlay"
if (-not $circle -and -not $overlay) { throw "hitcircle / hitcircleoverlay 이미지를 찾을 수 없습니다." }

$cw = [Math]::Max($(if ($circle) { $circle.Width } else { 0 }), $(if ($overlay) { $overlay.Width } else { 0 }))
$ch = [Math]::Max($(if ($circle) { $circle.Height } else { 0 }), $(if ($overlay) { $overlay.Height } else { 0 }))
$merged = New-Canvas $cw $ch
$g = New-Gfx $merged
if ($circle) {
    # osu! 처럼 색을 곱해서 입힘 (흰색 부분 → 콤보 색)
    $m = New-Object System.Drawing.Imaging.ColorMatrix
    $m.Matrix00 = $rgb[0] / 255.0; $m.Matrix11 = $rgb[1] / 255.0; $m.Matrix22 = $rgb[2] / 255.0
    Draw-Image $g $circle (($cw - $circle.Width) / 2) (($ch - $circle.Height) / 2) $circle.Width $circle.Height $m
}
if ($overlay) { Draw-Image $g $overlay (($cw - $overlay.Width) / 2) (($ch - $overlay.Height) / 2) $overlay.Width $overlay.Height }
$g.Dispose()

# osu! 는 숫자를 서클의 0.8배 크기로 그리므로, 숫자 그림 안에 넣을 서클은 1.25배로 키움
$bw = [int][Math]::Round($cw * 1.25 / 2) * 2
$bh = [int][Math]::Round($ch * 1.25 / 2) * 2
$big = New-Canvas $bw $bh
$g = New-Gfx $big; Draw-Image $g $merged 0 0 $bw $bh; $g.Dispose()

# ---- 3. 숫자 1~9 에 서클 합치기 ----
$canvasW = $bw; $canvasH = $bh
$nums = @{}
foreach ($i in 1..9) {
    $n = Get-HDImage "$prefix-$i"
    $nums[$i] = $n
    if ($n -and -not $NoNumbers) {
        $canvasW = [Math]::Max($canvasW, $n.Width); $canvasH = [Math]::Max($canvasH, $n.Height)
    }
}
$canvasW = [int][Math]::Ceiling($canvasW / 2) * 2; $canvasH = [int][Math]::Ceiling($canvasH / 2) * 2

foreach ($i in 1..9) {
    $out = New-Canvas $canvasW $canvasH
    $g = New-Gfx $out
    Draw-Image $g $big (($canvasW - $bw) / 2) (($canvasH - $bh) / 2) $bw $bh
    $n = $nums[$i]
    if ($n -and -not $NoNumbers) {
        Draw-Image $g $n ([Math]::Round(($canvasW - $n.Width) / 2)) ([Math]::Round(($canvasH - $n.Height) / 2)) $n.Width $n.Height
    }
    $g.Dispose()
    Save-Pair $out "$prefix-$i"
    $out.Dispose()
}
# 0 은 콤보 시작 숫자로 안 쓰이므로 같은 크기의 투명 이미지 (10, 20 ... 에서 서클이 한 번만 그려지게)
$zero = New-Canvas $canvasW $canvasH
Save-Pair $zero "$prefix-0"; $zero.Dispose()

# ---- 4. 원래 서클 파일들 투명하게 ----
$toClear = @("hitcircle", "hitcircleoverlay", "sliderstartcircle", "sliderstartcircleoverlay")
foreach ($name in $toClear) {
    foreach ($f in @("$name.png", "$name@2x.png")) {
        $p = Join-Path $root $f
        # hitcircle / overlay / sliderstartcircleoverlay 는 없으면 기본 스킨 것이 보이므로 반드시 만들어 둠
        if ((Test-Path $p) -or ($f -notlike "sliderstartcircle.png" -and $f -notlike "sliderstartcircle@2x.png")) { Save-Transparent $p }
    }
}
# 애니메이션 프레임(hitcircleoverlay-0.png 등)도 투명하게
Get-ChildItem $root -File | Where-Object { $_.Name -match '^(hitcircle|hitcircleoverlay|sliderstartcircle|sliderstartcircleoverlay)-\d+(@2x)?\.png$' } |
    ForEach-Object { Save-Transparent $_.FullName }

# ---- 4-1. 왼쪽 아래 콤보 카운터 숫자 만들기 ----
# skin.ini 의 ComboPrefix 가 가리키는 그림(예: d-0.png)이 없으면 콤보 숫자가 안 보이므로
# 점수 숫자(score-*.png)를 복사해 combo-*.png 를 만들고 ComboPrefix 를 combo 로 맞춤
$comboPrefixes = @($ini | Where-Object { $_ -match '^\s*ComboPrefix\s*:' } | ForEach-Object { ($_ -split ':', 2)[1].Trim() -replace '\\', '/' })
$scorePrefix = (Get-IniValue "ScorePrefix" "score") -replace '\\', '/'
$scoreOverlap = Get-IniValue "ScoreOverlap" "0"
$fixCombo = $false
if ($comboPrefixes.Count -gt 1) { $fixCombo = $true }
elseif ($comboPrefixes.Count -eq 1) {
    $cp = $comboPrefixes[0]
    if (-not ((Test-Path (Join-Path $root "$cp-0.png")) -or (Test-Path (Join-Path $root "$cp-0@2x.png")))) { $fixCombo = $true }
}
$comboFixed = $false
if ($fixCombo) {
    $files = Get-ChildItem $root -File | Where-Object { $_.Name -match ('^' + [regex]::Escape((Split-Path $scorePrefix -Leaf)) + '-([0-9]|x)(@2x)?\.png$') }
    if ($files) {
        foreach ($f in $files) {
            $f.Name -match '-([0-9]|x)(@2x)?\.png$' | Out-Null
            Copy-Item $f.FullName (Join-Path $root ("combo-" + $Matches[1].ToLower() + $Matches[2] + ".png")) -Force
        }
        $comboFixed = $true
        Write-Host "콤보 카운터 숫자(combo-*.png) 생성: $($files.Count)개"
    }
}

# ---- 5. skin.ini 고치기 ----
$overlap = [int]($canvasW / 2)   # SD 기준 숫자 그림 너비 → 두 자리 콤보에서 서클이 한 곳에 겹침
$newIni = New-Object System.Collections.Generic.List[string]
$newIni.Add("// instafade applied - Osu Skin Tool by LTH")
$section = ""; $comboWritten = $false; $overlapWritten = $false
foreach ($l in $ini) {
    if ($l -match '^\s*\[(.+)\]\s*$') {
        if ($section -eq "Fonts" -and -not $overlapWritten) { $newIni.Add("HitCircleOverlap: $overlap"); $overlapWritten = $true }
        $section = $Matches[1]
        $newIni.Add($l)
        if ($section -eq "Colours") { $newIni.Add("Combo1: $($rgb -join ',')"); $comboWritten = $true }
        if ($section -eq "Fonts" -and $comboFixed) { $newIni.Add("ComboPrefix: combo"); $newIni.Add("ComboOverlap: $scoreOverlap"); $comboFixed = "done" }
        continue
    }
    if ($comboFixed -and $l -match '^\s*Combo(Prefix|Overlap)\s*:') { continue }  # 아래 [Fonts] 에 새로 씀
    if ($l -match '^\s*Combo\d+\s*:') { continue }                       # 콤보 색은 Combo1 하나만 (어프로치 서클 색 맞춤)
    if ($l -match '^\s*HitCircleOverlap\s*:') {
        if (-not $overlapWritten) { $newIni.Add("HitCircleOverlap: $overlap"); $overlapWritten = $true }
        continue
    }
    if ($l -match '^\s*Name\s*:\s*(.+?)\s*$' -and $section -eq "General") {
        $newIni.Add("Name: $($Matches[1]) instafade by LTH"); continue
    }
    $newIni.Add($l)
}
if (-not $comboWritten) { $newIni.Add("[Colours]"); $newIni.Add("Combo1: $($rgb -join ',')") }
if (-not $overlapWritten -or $comboFixed -eq $true) {
    if ($section -ne "Fonts") { $newIni.Add("[Fonts]") }
    if (-not $overlapWritten) { $newIni.Add("HitCircleOverlap: $overlap") }
    if ($comboFixed -eq $true) { $newIni.Add("ComboPrefix: combo"); $newIni.Add("ComboOverlap: $scoreOverlap") }
}
[IO.File]::WriteAllLines($iniPath, $newIni, (New-Object Text.UTF8Encoding $false))

# ---- 6. 다시 .osk 로 묶기 ----
if (Test-Path $outOsk) { Remove-Item $outOsk -Force }
[IO.Compression.ZipFile]::CreateFromDirectory($root, $outOsk)
Remove-Item $work -Recurse -Force

foreach ($x in @($circle, $overlay, $merged, $big)) { if ($x) { $x.Dispose() } }
foreach ($n in $nums.Values) { if ($n) { $n.Dispose() } }

Write-Host "완료: $outOsk"
Write-Host "  서클 크기(HD) = ${bw}x${bh}, 숫자 그림 크기(HD) = ${canvasW}x${canvasH}, HitCircleOverlap = $overlap"
