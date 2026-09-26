#Requires -Version 5.1
<#
.SYNOPSIS
    构建并打包 Windows x64 Release Candidate。

.DESCRIPTION
    沿用仓库既有的发布约定（见 .github/workflows/release.yml）：Release + win-x64 +
    self-contained + PublishSingleFile。本脚本把那套流程变成可以在本机复现的一步操作，
    而不是另建第二套冲突流程。

    产出：
      artifacts/release-candidate/win-x64/DLSSGManager.exe      单文件绿色版
      artifacts/release-candidate/win-x64/README.md             使用说明
      artifacts/release-candidate/win-x64/LICENSE
      artifacts/release-candidate/win-x64/THIRD_PARTY_NOTICES.txt
      artifacts/RTX30-FrameGen-Manager-win-x64-<version>.zip
      artifacts/SHA256SUMS.txt

    任何一步失败即以非 0 退出码结束，不吞错误。

.PARAMETER Version
    覆盖版本号。默认读取主项目 csproj 中的 <Version>。

.PARAMETER SkipTests
    跳过 Harness。仅用于调试打包流程本身；正式产物不得使用。

.EXAMPLE
    pwsh -File scripts/package-release.ps1
#>
[CmdletBinding()]
param(
    [string]$Version = "",
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Step([string]$Text) { Write-Host "`n=== $Text ===" -ForegroundColor Cyan }

# ── 定位仓库根（脚本位于 scripts/）────────────────────────────────────────
$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $RepoRoot 'DLSSGManager.sln'))) {
    throw "未找到 DLSSGManager.sln，脚本位置可能不对：$RepoRoot"
}
Set-Location $RepoRoot

$MainProject = Join-Path $RepoRoot 'src/DLSSGManager/DLSSGManager.csproj'
if (-not (Test-Path $MainProject)) { throw "未找到主项目：$MainProject" }

# ── 版本号：来自 csproj，不凭空宣布 ──────────────────────────────────────
if ([string]::IsNullOrWhiteSpace($Version)) {
    $m = Select-String -Path $MainProject -Pattern '<Version>([^<]+)</Version>'
    if (-not $m) { throw "csproj 中没有 <Version>，请用 -Version 显式指定" }
    $Version = $m.Matches[0].Groups[1].Value.Trim()
}
Write-Host "版本: $Version"

$ArtifactsDir = Join-Path $RepoRoot 'artifacts'
$StageDir     = Join-Path $ArtifactsDir 'release-candidate/win-x64'
$ZipPath      = Join-Path $ArtifactsDir "RTX30-FrameGen-Manager-win-x64-$Version.zip"
$SumsPath     = Join-Path $ArtifactsDir 'SHA256SUMS.txt'

# ── 1. 清理 ───────────────────────────────────────────────────────────────
Step "清理旧产物"
if (Test-Path $StageDir) { Remove-Item $StageDir -Recurse -Force }
if (Test-Path $ZipPath)  { Remove-Item $ZipPath -Force }
if (Test-Path $SumsPath) { Remove-Item $SumsPath -Force }
New-Item -ItemType Directory -Force -Path $StageDir | Out-Null

# ── 2. 构建 ───────────────────────────────────────────────────────────────
Step "构建 Release"
dotnet build DLSSGManager.sln -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "构建失败（退出码 $LASTEXITCODE）" }

# ── 3. Harness ────────────────────────────────────────────────────────────
if (-not $SkipTests) {
    Step "运行 Harness"
    $harness = Join-Path $RepoRoot 'test/Harness/bin/Release/net8.0-windows/Harness.exe'
    if (-not (Test-Path $harness)) { throw "未找到 Harness：$harness" }
    & $harness
    if ($LASTEXITCODE -ne 0) { throw "测试失败（退出码 $LASTEXITCODE）" }
} else {
    Write-Warning "已跳过 Harness（-SkipTests）：产物不可用于正式发布"
}

# ── 4. 发布（与 release.yml 相同的语义）──────────────────────────────────
Step "发布 win-x64 self-contained"
dotnet publish $MainProject `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:Version=$Version `
    -o $StageDir --nologo
if ($LASTEXITCODE -ne 0) { throw "发布失败（退出码 $LASTEXITCODE）" }

# ── 5. 随附声明文件 ──────────────────────────────────────────────────────
Step "复制随附文件"
foreach ($name in @('README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.txt')) {
    $src = Join-Path $RepoRoot $name
    if (Test-Path $src) { Copy-Item $src -Destination $StageDir -Force }
    else { Write-Warning "缺少 $name" }
}

# ── 6. 剔除不得进入成品的内容 ────────────────────────────────────────────
Step "剔除开发期文件与禁止项"
$forbidden = @('*.pdb', '*.xml', '*.cs', '*.csproj', '*.sln', '.git*', '_research', 'test', 'obj', '*.log', '*.json.user')
foreach ($pattern in $forbidden) {
    Get-ChildItem -Path $StageDir -Filter $pattern -Recurse -Force -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

# 防呆：源码/凭据/研究资料一旦混入即中止，而不是默默打包
$leak = Get-ChildItem -Path $StageDir -Recurse -Force -ErrorAction SilentlyContinue | Where-Object {
    $_.Name -match '^\.git' -or $_.Name -match 'credentials' -or $_.Extension -in @('.cs', '.csproj', '.sln', '.pdb')
}
if ($leak) {
    $leak | ForEach-Object { Write-Host "  混入: $($_.FullName)" -ForegroundColor Red }
    throw "发布目录中出现了不该包含的内容，已中止"
}

$exe = Join-Path $StageDir 'DLSSGManager.exe'
if (-not (Test-Path $exe)) { throw "发布目录中没有 DLSSGManager.exe" }

# ── 7. ZIP ────────────────────────────────────────────────────────────────
Step "生成 ZIP"
Compress-Archive -Path (Join-Path $StageDir '*') -DestinationPath $ZipPath -CompressionLevel Optimal -Force
if (-not (Test-Path $ZipPath)) { throw "ZIP 未生成" }

# ── 8. SHA256SUMS ─────────────────────────────────────────────────────────
Step "生成 SHA256SUMS"
$lines = @()
foreach ($f in @($exe, $ZipPath)) {
    $hash = (Get-FileHash $f -Algorithm SHA256).Hash
    $lines += "{0}  {1}" -f $hash, (Split-Path $f -Leaf)
}
$lines | Out-File -FilePath $SumsPath -Encoding ascii
Get-Content $SumsPath | ForEach-Object { Write-Host "  $_" }

# ── 9. 汇总 ───────────────────────────────────────────────────────────────
Step "完成"
"版本            : $Version"
"发布目录        : $StageDir"
"EXE             : $exe ({0:N1} MB)" -f ((Get-Item $exe).Length / 1MB)
"ZIP             : $ZipPath ({0:N1} MB)" -f ((Get-Item $ZipPath).Length / 1MB)
"SHA256SUMS      : $SumsPath"
exit 0
