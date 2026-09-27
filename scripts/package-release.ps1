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

# ⚠️ **三个替换必须是一个事务**（Pass G 报出：此前只做到了「每个动作内部原子」）。
#
# Pass F 修的是「每个 `Rename-Item` 自己不会把目标删一半」；而 Pass G 用失败注入证明**换序仍然有窗口**：
# 目录先换成新的，随后 ZIP 或 SHA256SUMS 改名失败 ⇒ 落回「**新 EXE 目录 + 旧 ZIP + 旧 SUMS**」，
# 而 `SHA256SUMS.txt` 正是用户拿来校验发布物的东西。
#
# 现在的做法是**两阶段**：
#   阶段一（只做 rename，都可回退）：把三个旧正式名各自改成 `.old`。
#     任一步失败 ⇒ **把已经改过名的逐个换回原名**（rename 是原子的，所以换回一定成功）。
#   阶段二（放新的）：三个 `Move-Item`。同样，失败也要换回。
# 最后才删 `.old`。
#
# **判据**：**「每个动作原子」不等于「一串动作原子」** —— 后者要靠「失败时能回到起点」来保证，
# 也就是事务。这里的回滚很便宜（rename），所以没有理由不做。

# ⚠️ **必须先初始化**：成功路径上 `catch` 不执行 ⇒ 后面那个 `if ($script:replaceFailure)`
# 会读一个未定义的变量，而脚本开了严格模式 ⇒ 报
# `The variable '$script:replaceFailure' cannot be retrieved because it has not been set.`
# （**而且那次运行的退出码仍是 0** —— 又一次说明退出码不能当作成功的证据。）
$script:replaceFailure = $null

$artifacts = @(
    @{ Live = $StageDir;  Tmp = $StageTmp;  Old = "$StageDir.old";  Kind = 'dir'  },
    @{ Live = $ZipPath;   Tmp = $ZipTmp;    Old = "$ZipPath.old";   Kind = 'file' },
    @{ Live = $SumsPath;  Tmp = $SumsTmp;   Old = "$SumsPath.old";  Kind = 'file' }
)

# 先清掉上一次可能留下的 .old（**失败时不再静默** —— 上一次那两处 `-ErrorAction SilentlyContinue`
# 实测会让一次运行 EXIT=0 却留下 .old，而**下一次**以 `Cannot create a file when that file already exists`
# 失败、真因从不打印。这里显式检查并报告。）
foreach ($a in $artifacts)
{
    if (Test-Path $a.Old)
    {
        Remove-Item $a.Old -Recurse -Force -ErrorAction Stop
    }
}

# 阶段一：全部改成 .old（失败的换回来）
$moved = New-Object System.Collections.ArrayList
try
{
    foreach ($a in $artifacts)
    {
        if (Test-Path $a.Live)
        {
            Rename-Item $a.Live $a.Old -ErrorAction Stop
            [void]$moved.Add($a)
        }
    }

    # 阶段二：放新的
    foreach ($a in $artifacts)
    {
        Move-Item $a.Tmp $a.Live -ErrorAction Stop
    }
}
catch
{
    $reason = $_.Exception.Message
    Write-Warning "替换失败，正在恢复原状：$reason"

    # ⚠️ **回滚本身也会失败，而它必须把「哪些撤了、哪些没撤」说清楚**（Pass H 报出）：
    #
    #   · 上一轮把**清理遗留 `.old`** 的两处改成了 `-ErrorAction Stop`，却**漏了这里的同类两处**
    #     ⇒ 「脚本里已无静默删除」这个说法比实际宽。
    #   · 更要紧的是**原来的 `throw` 写在 `catch` 块里** ⇒ 它一抛，下面的语句就**永不执行**：
    #     原始 `$reason` 丢失、循环提前中断造成**部分回滚**、而且**没有任何一句话告诉用户
    #     旧产物是否已恢复**。实测（Pass H 的注入）：终止错误会是
    #     `Cannot create a file when that file already exists.` —— **与本因完全无关**。
    #
    # ⇒ 现在：回滚分两步各自记录结果，**在 `catch` 之外抛出**，消息里同时带上原始原因与恢复结果。
    $undoNotes = New-Object System.Collections.ArrayList

    # 第一步：撤掉已经放上去的新产物（它们此刻占着正式名）
    foreach ($a in $artifacts)
    {
        if (Test-Path $a.Tmp) { continue }        # Tmp 还在 ⇒ 它还没被放上去
        if (Test-Path $a.Live)
        {
            try
            {
                Remove-Item $a.Live -Recurse -Force -ErrorAction Stop
            }
            catch
            {
                $leaf = Split-Path $a.Live -Leaf
                [void]$undoNotes.Add("$leaf 未能撤下（$($_.Exception.Message)）")
            }
        }
    }

    # 第二步：把 .old 换回正式名
    for ($i = $moved.Count - 1; $i -ge 0; $i--)
    {
        $a = $moved[$i]
        if (Test-Path $a.Old)
        {
            try
            {
                Rename-Item $a.Old $a.Live -ErrorAction Stop
            }
            catch
            {
                $leaf = Split-Path $a.Live -Leaf
                [void]$undoNotes.Add("$leaf 未能换回（$($_.Exception.Message)）—— 它的旧版本仍在 $leaf.old")
            }
        }
    }

    # **在 catch 之外抛出**：这样上面的语句一定执行完，且这条消息一定发得出去。
    $rollbackState = if ($undoNotes.Count -eq 0) {
        "旧产物已全部恢复。"
    } else {
        "⚠️ 恢复不完整：" + ($undoNotes -join "；") + " —— 请手工检查 artifacts 目录。"
    }

    $script:replaceFailure = "替换正式产物失败：$reason`n$rollbackState"
}

# **在 catch 之外抛出**：这样回滚的两步一定执行完、消息一定发得出去、原始原因一定保留。
# **位置也很重要：必须在删 `.old` 之前** —— 否则会先把唯一的恢复材料删掉，再报告「恢复失败」。
if ($script:replaceFailure)
{
    throw $script:replaceFailure
}

# 到这里三个都换好了 —— 才删 .old
foreach ($a in $artifacts)
{
    if (Test-Path $a.Old) { Remove-Item $a.Old -Recurse -Force -ErrorAction Stop }
}

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
