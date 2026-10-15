# 发布打包脚本：Release 单文件发布 + 便携 zip + Inno Setup 安装包
#
# 用法（仓库根目录，或任意目录）：
#   powershell -File tools\Build-Release.ps1
#   powershell -File tools\Build-Release.ps1 -Version 1.0.0
#   powershell -File tools\Build-Release.ps1 -SkipInstaller
#
# 产物（artifacts\installer\）：
#   DeskShelf-<版本>-setup.exe              安装程序（per-user，免管理员）
#   DeskShelf-<版本>-portable-win-x64.zip   便携版（解压即用）
#
# 依赖：.NET 8 SDK；生成安装包还需要 Inno Setup 6
#   winget install --id JRSoftware.InnoSetup

[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 本脚本位于 <仓库根>\tools\，因此根目录是它的上一级
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root "artifacts\publish\$Runtime"
$installerDir = Join-Path $root 'artifacts\installer'
$project = Join-Path $root 'src\DeskShelf.App\DeskShelf.App.csproj'
$solution = Join-Path $root 'DeskShelf.slnx'
$testProject = Join-Path $root 'tests\DeskShelf.Tests\DeskShelf.Tests.csproj'
$issFile = Join-Path $root 'installer\DeskShelf.iss'

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host "=== $text ===" -ForegroundColor Cyan
}

function Write-Artifact([string]$path) {
    $item = Get-Item $path
    Write-Host ('  {0}  {1} MB' -f $item.Name, [math]::Round($item.Length / 1MB, 2)) -ForegroundColor Green
}

Write-Host "仓库根目录：$root" -ForegroundColor DarkGray
Write-Host "版本：$Version  配置：$Configuration  运行时：$Runtime" -ForegroundColor DarkGray

Write-Step '清理旧产物'
foreach ($dir in @($publishDir, $installerDir)) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}
New-Item -ItemType Directory -Path $installerDir -Force | Out-Null

Write-Step "构建（$Configuration）"
& dotnet build $solution -c $Configuration -v q --nologo
if ($LASTEXITCODE -ne 0) { throw '构建失败' }

Write-Step '运行单元测试'
& dotnet test $testProject -c $Configuration -v q --nologo
if ($LASTEXITCODE -ne 0) { throw '测试失败，已中止发布' }

Write-Step "单文件发布（self-contained $Runtime）"
& dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=false `
    -p:DebugType=none `
    -p:Version=$Version `
    -o $publishDir `
    -v q --nologo
if ($LASTEXITCODE -ne 0) { throw '发布失败' }

Write-Artifact (Join-Path $publishDir 'DeskShelf.exe')

Write-Step '生成便携包（zip）'
$zipPath = Join-Path $installerDir "DeskShelf-$Version-portable-$Runtime.zip"
# 用 ZipFile.CreateFromDirectory 而不是 Compress-Archive：
# 后者对已是压缩格式的大文件会产出损坏的包（这里曾生成 0.02 MB 的假包）
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $publishDir,
    $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)
Write-Artifact $zipPath

if ($SkipInstaller) {
    Write-Step '已跳过安装包（-SkipInstaller）'
    return
}

Write-Step '生成安装程序（Inno Setup）'
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning '未找到 Inno Setup（ISCC.exe），已跳过安装包。安装：winget install --id JRSoftware.InnoSetup'
    return
}

# 传入仓库根目录：Iscc 解析文件路径以「当前工作目录」为基准，不能靠相对路径猜测
& $iscc "/DRepoRoot=$root" $issFile
if ($LASTEXITCODE -ne 0) { throw '生成安装程序失败' }

Write-Step '发布产物'
Get-ChildItem $installerDir -File | ForEach-Object { Write-Artifact $_.FullName }

$setupPath = Join-Path $installerDir "DeskShelf-$Version-setup.exe"
Write-Host ''
Write-Host '发布到 GitHub Release：' -ForegroundColor Yellow
Write-Host "  gh release create v$Version `"$setupPath`" `"$zipPath`" --title `"DeskShelf v$Version`" --notes-file `"$root\Docs\ReleaseNotes-v$Version.md`""
