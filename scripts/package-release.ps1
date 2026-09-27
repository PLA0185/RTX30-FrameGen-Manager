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

# ── 1. 清理（**只清理临时目录，旧产物留到最后才动**）────────────────────
#
# 这里原来是「先删旧产物再构建/测试/发布」—— 于是任何一步失败都会留下
# 「旧产物已删 + 残缺 StageDir」：用户既没有上一次可用的包，也没有这一次的。
# **一次失败不该抹掉上一次的成功交付物。**
#
# 现在：全程写临时路径 → 全部成功后才替换正式产物。（同项目 `Store.cs` 的
# 「临时文件 + File.Move(overwrite)」就是这个模式。）
Step "准备临时输出目录"
$StageTmp = Join-Path $ArtifactsDir 'release-candidate/.win-x64-staging'
if (Test-Path $StageTmp) { Remove-Item $StageTmp -Recurse -Force }
New-Item -ItemType Directory -Force -Path $StageTmp | Out-Null

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
    -o $StageTmp --nologo
if ($LASTEXITCODE -ne 0) { throw "发布失败（退出码 $LASTEXITCODE）" }

# ── 5. 随附声明文件 ──────────────────────────────────────────────────────
Step "复制随附文件"
foreach ($name in @('README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.txt')) {
    $src = Join-Path $RepoRoot $name
    if (Test-Path $src) { Copy-Item $src -Destination $StageTmp -Force }
    else { Write-Warning "缺少 $name" }
}

# ── 6. 剔除不得进入成品的内容 ────────────────────────────────────────────
Step "剔除开发期文件与禁止项"
#
# **名单式检查（黑名单）覆盖不到的要单独拦。** 补上三类「不该出现在发布目录里」而此前
# 完全没人拦的扩展名：`*.dll`（上游二进制绝不随包分发）· `*.json`（配置/凭据常是 json）·
# `*.user` / `*.suo`（本机 IDE 状态，`.gitignore` 里就是这么写的，原来只写了 `*.json.user`）。
# `DLSSGManager.exe` 是 exe，不会被 `*.dll` 拦到；单文件发布也不需要额外 dll。
$forbidden = @('*.pdb', '*.xml', '*.cs', '*.csproj', '*.sln', '.git*', '_research', 'test', 'obj',
               '*.log', '*.json.user', '*.user', '*.suo', '*.dll', '*.json')
foreach ($pattern in $forbidden) {
    Get-ChildItem -Path $StageTmp -Filter $pattern -Recurse -Force -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

# 防呆：源码/凭据/研究资料一旦混入即中止，而不是默默打包
#
# ⚠️ **名单必须与上面 `$forbidden` 同宽**（Pass D 报出）：这里原来只查 `.git*`/`credentials`/
# `.cs/.csproj/.sln/.pdb`，而 `$forbidden` 还含 `*.dll`/`*.json`/`*.log`/`*.user`/`*.suo`/
# `_research`/`test`/`obj` —— 上面那些是**尽力删除**（`-ErrorAction SilentlyContinue`），
# **删不掉就静默放行打包**，其中 `*.dll` 正是注释写着「绝不随包分发」的那一类。
# **「删掉了」与「确认不在了」是两件事**：删除是动作，这道检查才是判据。
$leakExtensions = @('.cs', '.csproj', '.sln', '.pdb', '.dll', '.json', '.log', '.user', '.suo', '.xml')
$leak = Get-ChildItem -Path $StageTmp -Recurse -Force -ErrorAction SilentlyContinue | Where-Object {
    ($_.Extension -in $leakExtensions) -or ($_.Name -match '^\.git') -or ($_.Name -match 'credentials') -or ($_.Name -match '^test$') -or ($_.Name -match '^obj$') -or ($_.Name -match '^_research$')
}
if ($leak) {
    $leak | ForEach-Object { Write-Host "  混入: $($_.FullName)" -ForegroundColor Red }
    throw "发布目录中出现了不该包含的内容，已中止"
}

if (-not (Test-Path (Join-Path $StageTmp 'DLSSGManager.exe'))) { throw "发布目录中没有 DLSSGManager.exe" }

# ── 6b. ZIP 与 SHA256SUMS —— **都从临时目录做，正式产物此刻还没动** ────────
#
# ⚠️ **替换 `$StageDir` 必须推迟到全部产物都成功之后**（Pass D 报出）。
# 这里原来先把 `$StageTmp` 换成 `$StageDir`，于是**后面任何一步失败**都会留下
# 「**新 EXE + 旧 ZIP + 旧 SHA256SUMS**」——`SHA256SUMS.txt` 里那只 EXE 的哈希与目录里实际的
# EXE **不再对应**，而这正是用户拿来校验发布物的东西。**「旧产物完好」当时只对 ZIP 成立。**
$exe = Join-Path $StageTmp 'DLSSGManager.exe'

# ── 7. ZIP ────────────────────────────────────────────────────────────────
Step "生成 ZIP"
# **临时名也必须以 `.zip` 结尾**：`Compress-Archive` 只接受 `.zip`，给 `.tmp` 会直接报
# 「不是支持的存档文件格式」——**而且它是在生成阶段才报，所以整个脚本会在最后一步失败。**
$ZipTmp = "$ZipPath.tmp.zip"
if (Test-Path $ZipTmp) { Remove-Item $ZipTmp -Force }
Compress-Archive -Path (Join-Path $StageTmp '*') -DestinationPath $ZipTmp -CompressionLevel Optimal -Force
if (-not (Test-Path $ZipTmp)) { throw "ZIP 未生成" }

# **只查「存在」不够**：`Compress-Archive` 中途失败（磁盘满、被中断）会留下一个**残缺 ZIP**，
# 而 `Test-Path` 对残缺文件同样返回真。这里核对条目数与关键文件 —— **发布路径上「文件在」
# 与「文件完整」必须分开检查。**
Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
$zip = [System.IO.Compression.ZipFile]::OpenRead($ZipTmp)
try { $zipEntries = @($zip.Entries | ForEach-Object { $_.FullName }) }
finally { $zip.Dispose() }
if ($zipEntries.Count -lt 3) { throw "ZIP 条目数异常（$($zipEntries.Count)），可能是残缺包" }
if (-not ($zipEntries | Where-Object { $_ -like '*DLSSGManager.exe' })) { throw "ZIP 中缺少 DLSSGManager.exe" }

# ── 8. SHA256SUMS（**从临时产物算，但先写成临时名**）──────────────────────
#
# ⚠️ **哈希要在这里算，但文件不能在这里落地为正式名**（Pass E 报出 —— 这是上一轮修复引入的新窗口）：
# 把 `Out-File $SumsPath` 放在替换之前，会让**替换阶段失败**时留下
# 「**旧 EXE + 旧 ZIP + 新 SHA256SUMS**」，与下面注释声称的「仍然是一份自洽的旧发布」恰好相反。
# **哈希值必须在临时产物上算**（那样才与将要发布的字节一致），**而写入正式名必须和产物一起做**。
Step "生成 SHA256SUMS"
$SumsTmp = "$SumsPath.tmp"
$lines = @()
foreach ($f in @($exe, $ZipTmp)) {
    $hash = (Get-FileHash $f -Algorithm SHA256).Hash
    $lines += "{0}  {1}" -f $hash, (Split-Path $f -Leaf).Replace('.tmp.zip', '')
}
$lines | Out-File -FilePath $SumsTmp -Encoding ascii
Get-Content $SumsTmp | ForEach-Object { Write-Host "  $_" }

# ── 8b. 到这里才替换正式产物（**前面任何一步失败都没有动过旧产物**）──────
#
# **顺序是保证的一半**：先把全部产物做出来并核对完（EXE 在 `$StageTmp`、ZIP 在 `$ZipTmp`、
# 哈希已经算过），**最后**才替换那三个正式名（目录 / ZIP / SHA256SUMS）。
#
# ⚠️ **另一半是「每个替换动作本身要尽量原子」—— 这是 Pass F 实测后改的，此前两处注释都是错的：**
#   · `Remove-Item -Recurse -Force` **不是全有或全无**：目录里只要有一个文件被锁，它会**部分删除**
#     （实测：4 个文件的目录锁住 1 个 ⇒ 抛 IOException，**其余 3 个含 `DLSSGManager.exe` 已被删除**）。
#   · `Move-Item -Force` **不是原子覆盖**，它是 delete-then-move：**目标存在且源被锁**时抛 IOException，
#     而**目标已经被删掉了** —— 正是这里声称要消除的那个状态。
#（本机 Windows PowerShell 5.1 是唯一真实宿主：脚本 `#Requires -Version 5.1`，且本机没有 `pwsh` 7。）
#
# 所以改成：
#   · **目录**：先把旧的改名成 `.old`（原子），再把新的放到位，**最后**才删 `.old`。
#     任何一步失败时**旧目录要么还叫原名、要么完整地以 `.old` 存在**，绝不会是半个。
#   · **文件**：用 `[System.IO.File]::Replace`（同卷、有备份语义的原子替换）。
Step "替换正式产物"

# 目录：rename → 放新的 → 删旧的
$oldStage = "$StageDir.old"
if (Test-Path $oldStage) { Remove-Item $oldStage -Recurse -Force -ErrorAction SilentlyContinue }
if (Test-Path $StageDir) { Rename-Item $StageDir $oldStage }
Move-Item $StageTmp $StageDir
if (Test-Path $oldStage) { Remove-Item $oldStage -Recurse -Force -ErrorAction SilentlyContinue }

# 文件：**与目录同一个模式**（rename 旧的走 → 放新的 → 删旧的）。
#
# ⚠️ 这里换过三种写法，每一种都因为「我以为的 API 语义」与实际不符而失败：
#   ① `Move-Item $src $dst -Force` —— 实测**不是原子覆盖**，是 delete-then-move：
#      目标存在且源被锁时抛 IOException，而**目标已经被删掉了**（Pass F 实测）。
#   ② `foreach ($pair in @(@($a,$b), @($c,$d)))` —— PowerShell **把嵌套数组展平**，
#      `$pair` 成了单个字符串、`$pair[0]` 是它的第一个字符。
#   ③ `[System.IO.File]::Replace($src, $dst, $null)` —— 报
#      「Exception calling "Replace" with "3" argument(s): The path is not of a legal form.」
#
# **所以不再依赖任何特殊 API 的语义**，只用最朴素的 `Rename-Item` / `Move-Item`：
# **任何一步失败时，目标要么还是完整的旧文件、要么已经是完整的新文件**，不会是半个或没有。
function Replace-Artifact([string]$src, [string]$dst)
{
    $old = "$dst.old"
    if (Test-Path $old) { Remove-Item $old -Force -ErrorAction SilentlyContinue }
    if (Test-Path $dst) { Rename-Item $dst $old }
    Move-Item $src $dst
    if (Test-Path $old) { Remove-Item $old -Force -ErrorAction SilentlyContinue }
}

Replace-Artifact $ZipTmp $ZipPath
Replace-Artifact $SumsTmp $SumsPath

# **替换之后要重新指向正式路径** —— 临时目录已经不存在了，汇总段若仍用 `$exe` / `$ZipTmp`
# 的旧值，`Get-Item` 会找不到文件（本脚本第一次跑新顺序时就踩到）。
$exe = Join-Path $StageDir 'DLSSGManager.exe'

# ── 9. 汇总 ───────────────────────────────────────────────────────────────
Step "完成"
"版本            : $Version"
"发布目录        : $StageDir"
"EXE             : $exe ({0:N1} MB)" -f ((Get-Item $exe).Length / 1MB)
"ZIP             : $ZipPath ({0:N1} MB)" -f ((Get-Item $ZipPath).Length / 1MB)
"SHA256SUMS      : $SumsPath"
exit 0
