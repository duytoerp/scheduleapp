<#
.SYNOPSIS
    Build ScheduleApp: chạy kiểm thử, publish ScheduleApp.exe (một file), tạo version.json cho tự cập nhật
    và bộ cài (nếu máy có Inno Setup 6).

.EXAMPLE
    .\build.ps1                                  # test + publish + bộ cài
    .\build.ps1 -SkipTests -SelfContained        # bản không cần cài .NET (file lớn hơn)
    .\build.ps1 -Notes "Sửa lỗi ghi Excel" -Share \\may-chu\ScheduleApp
        # chép ScheduleApp.exe + version.json lên thư mục dùng chung → các máy có nguồn cập nhật
        # là thư mục đó sẽ được báo có bản mới.
#>
param(
    [switch]$SkipTests,
    [switch]$SelfContained,
    [string]$Notes = "",
    [string]$Share = ""
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publish = Join-Path $root 'publish'

if (-not $SkipTests) {
    Write-Host '▶ Chạy kiểm thử…' -ForegroundColor Cyan
    dotnet test (Join-Path $root 'ScheduleApp.slnx') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Kiểm thử thất bại — dừng build.' }
}

Write-Host '▶ Publish ScheduleApp.exe…' -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
$sc = if ($SelfContained) { 'true' } else { 'false' }
dotnet publish (Join-Path $root 'ScheduleApp.csproj') -c Release -r win-x64 --self-contained $sc `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish thất bại.' }

$exe = Join-Path $publish 'ScheduleApp.exe'
$version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
$sha = (Get-FileHash $exe -Algorithm SHA256).Hash
$manifest = [ordered]@{ version = $version; url = 'ScheduleApp.exe'; notes = $Notes; sha256 = $sha }
[IO.File]::WriteAllText((Join-Path $publish 'version.json'), ($manifest | ConvertTo-Json), (New-Object System.Text.UTF8Encoding $false))
Write-Host "  ScheduleApp $version — SHA-256 $sha"

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    Write-Host '▶ Tạo bộ cài…' -ForegroundColor Cyan
    & $iscc "/DAppVersion=$version" (Join-Path $root 'installer\ScheduleApp.iss') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Tạo bộ cài thất bại.' }
} else {
    Write-Host '  (Bỏ qua bộ cài — chưa cài Inno Setup 6: https://jrsoftware.org/isdl.php hoặc winget install JRSoftware.InnoSetup)' -ForegroundColor Yellow
}

if ($Share) {
    Write-Host "▶ Chép bản mới lên $Share…" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force $Share | Out-Null
    # Chép exe trước, version.json sau cùng để máy khác không thấy version.json mới khi exe chưa chép xong.
    Copy-Item $exe (Join-Path $Share 'ScheduleApp.exe') -Force
    Copy-Item (Join-Path $publish 'version.json') (Join-Path $Share 'version.json') -Force
}

Write-Host "✔ Xong: $publish" -ForegroundColor Green
Get-ChildItem $publish | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
