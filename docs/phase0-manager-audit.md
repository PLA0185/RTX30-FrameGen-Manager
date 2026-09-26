# Phase 0 审计：`BUNNY-19C/DLSSG-30s-manager` 现状架构分析

> **审计对象**：`_research/upstream/DLSSG-30s-manager/`（本地只读快照，未联网、未调用 GitHub API）
> **审计日期**：2026-09-26
> **口径**：所有论断均附 `相对路径:行号`。**代码行数与任务书所标数字有偏差，本文一律以本地实测为准**（任务书标 `ModFetcher.cs` 970 行 / `DeploymentService.cs` 939 行，实测一致；但任务书标 `Gpu.cs` 548 行、`test/Harness/Program.cs` 1927 行，实测分别为 548 行、**2297 行**）。行数统计含空行。
> **证据等级**：`[E1]` = 本地源码直接可验证；`[E2]` = 仓库内文档/工作流声明；`[未确认]` = 本快照无法验证（例：真实 mod 二进制不在仓库内）。

---

## 1. 当前架构总览

### 1.1 技术栈与规模

| 项 | 值 | 证据 |
|---|---|---|
| 目标框架 | `net8.0-windows`，WPF，`Nullable=enable` | `src/DLSSGManager/DLSSGManager.csproj:5-8` |
| 程序集版本 | `1.9.3` | `src/DLSSGManager/DLSSGManager.csproj:12` |
| 外部 NuGet 依赖 | **零**（csproj 无 `PackageReference`） | `src/DLSSGManager/DLSSGManager.csproj:19-21` |
| C# 文件数 / 总行数 | 29 个 / 7620 行 | 实测统计 |
| XAML 行数 | `MainWindow.xaml` 827 行 | 实测统计 |
| 测试夹具 | `test/Harness/Program.cs` **2297 行** | 实测统计 |

### 1.2 模块清单（文件 → 职责 → 行数 → 依赖方向）

| 文件 | 行数 | 职责 | 依赖（同项目内） |
|---|---|---|---|
| `src/DLSSGManager/Models.cs` | 306 | 数据模型：`GameEntry`/`GameProfile`/`DeploymentInfo`/`BackupItem`/`DeployedFile`/`AppData`/`GameStatus` | `Loc`、`Palette`、`ModFetcher`(注释级) |
| `src/DLSSGManager/Detection.cs` | 283 | 游戏与渲染目录发现：Steam 库解析、标记文件扫描、主 EXE 启发式 | `Loc` |
| `src/DLSSGManager/ModSource.cs` | 282 | 本地 mod 源建模：入口名清单、可用入口、INI 版本读取、导入用户代理 | `Loc`、`DeploymentService`、`ModFetcher` |
| `src/DLSSGManager/ModSourceLocator.cs` | 191 | mod 源目录定位与目标目录决策（绿色版 / 安装版 / 源码检出） | `AppPaths` |
| `src/DLSSGManager/ModFetcher.cs` | 970 | **下载器与更新系统**：多源下载、URL 策略、签名校验、发布 | `DeploymentService`、`ModSource`、`Loc` |
| `src/DLSSGManager/DeploymentService.cs` | 939 | **唯一写入游戏目录者**：部署/恢复/接管/状态评估、签名与哈希所有权判据 | `AntiCheat`、`ModSource`、`ModFetcher`、`Native`、`Loc` |
| `src/DLSSGManager/AntiCheat.cs` | 282 | 反作弊特征扫描与隔离残留识别 | `DeploymentService`、`Loc` |
| `src/DLSSGManager/Gpu.cs` | 548 | GPU 探测（Win32 显示 API + 注册表）、架构判定、驱动版本、显示名读写 | `AppPaths`、`Loc` |
| `src/DLSSGManager/Store.cs` | 249 | 应用路径（`AppPaths`）、库持久化（`LibraryStore`）、INI 模板渲染（`IniTemplate`） | 无（叶子） |
| `src/DLSSGManager/Shell.cs` | 231 | `PathGuard` 路径校验 + `ShellExecuteExW` 封装 | `AppPaths`、`Loc` |
| `src/DLSSGManager/Native.cs` | 58 | 进程路径查询、提权判定 | 无（叶子） |
| `src/DLSSGManager/Localization.cs` | 115 | 运行时字符串查找 `Loc` / `Languages` | `AppPaths`、`Strings` |
| `src/DLSSGManager/LocalizationAudit.cs` | 111 | 词条完备性自检 | `Strings` |
| `src/DLSSGManager/Strings.zh.cs` / `Strings.en.cs` | 各 424 | 中英文字符串表 | 无 |
| `src/DLSSGManager/MainWindow.xaml` + `.xaml.cs` + `.Actions.cs` + `.Protection.cs` | 827 + 854 + 561 + 103 = **2345** | **UI 层**（XAML 视图 + 事件处理 + 业务编排） | 几乎全部业务模块 |
| `src/DLSSGManager/App.xaml.cs` | 106 | 启动、`--fetch` 无界面下载开关、全局异常兜底 | `ModFetcher`、`ModSourceLocator` |
| `test/Harness/Program.cs` | 2297 | 控制台测试夹具（白名单编译模式） | 18 个 src 文件 |
| `installer/setup.iss` | 176 | Inno Setup 打包（随仓库分发） | — |
| `%APPDATA%\DLSSGManager\library.json` | — | 全部持久化状态（游戏列表、部署记录、界面偏好） | `Store.cs:36` |

### 1.3 模块依赖图（文本）

```
                          ┌──────────────────────────────┐
                          │  App.xaml.cs (启动/--fetch)   │
                          └───────────────┬──────────────┘
                                          │
        ┌─────────────────────────────────┴───────────────────────────────────┐
        │                       UI 层 (2345 行, 19.9% 的 .cs)                 │
        │  MainWindow.xaml.cs (854)  MainWindow.Actions.cs (561)              │
        │  MainWindow.Protection.cs (103)  MainWindow.xaml (827)              │
        │  SourcePickerDialog / GpuNameDialog                                  │
        └───┬───────────┬────────────┬───────────┬───────────┬────────────────┘
            │           │            │           │           │
            ▼           ▼            ▼           ▼           ▼
      ModSource    Deployment    Detection    AntiCheat     Gpu
      Locator      Service            │           │          │
            │           │            │           │          │
            │           ├────────────┴───────────┘          │
            ▼           ▼                                   ▼
        Store.cs    ModFetcher  ◄─── ModSource          Native.cs
      (AppPaths/        │              │                 Shell.cs
       Library/         │              │
       IniTemplate)     ▼              ▼
                    DeploymentService ─┴─► 依赖循环：ModSource ⇄ ModFetcher ⇄ DeploymentService
```

**必须点明的结构问题**：

1. **三个模块构成依赖环**：`ModSource.cs:103` 读 `ModFetcher.VersionMarkerName`；`ModSource.cs:266` 调 `DeploymentService.ReadSignerCertificate`；`ModFetcher.cs:206` 调 `DeploymentService.Sha256`；`ModFetcher.cs:914` 调 `DeploymentService.IsProjectSigned`；`DeploymentService.cs:924` 调 `ModFetcher.IsKnownCommunityBuild`；`DeploymentService.cs:889`（`Apply`）依赖 Models。`[E1]`
   ⇒ 下载层、部署层、源管理层无法分别编译/测试，这是新架构必须先打破的耦合。
2. **测试项目靠"文件白名单"而非项目引用**：`test/Harness/Harness.csproj:18-36` 逐个 `<Compile Include="../../src/DLSSGManager/XXX.cs" />` 列了 18 个文件。`[E1]`
   ⇒ 任何新模块若被 `DeploymentService` 之类引用，都必须同步加进这个列表；反之 `MainWindow*.cs` 不在列表内，天然不可测。这个模式必须换掉。

### 1.4 业务逻辑被塞进 UI 层的程度

**结论：占比约 20%，且不是"纯 UI"——UI 层承担了持久化、任务编排、安全决策三类职责。**

| 位置 | 行数 | 塞进去的业务逻辑 | 证据 |
|---|---|---|---|
| `MainWindow.xaml.cs` | 854 | 首次运行自动下载编排、mod 源可用性判定与徽章着色、语言/主题切换后的全局重绑定、GPU 文本重渲染、状态刷新并发互斥 | `:308-356`、`:462-492`、`:213-231`、`:265-278`、`:827-853` |
| `MainWindow.Actions.cs` | 561 | 部署/恢复/接管的事务编排与 `_busy` 全局互斥、批量部署的批量确认语义、扫描结果合并进库、提权重启 | `:39-86`、`:183-251`、`:385-444`、`:552-560` |
| `MainWindow.Protection.cs` | 103 | 反作弊拦截决策（何处弹窗、批量汇总、日志文案） | `:66-84`、`:90-102` |
| **合计** | **1518 / 7620 = 19.9%** | 其中真正"UI 专属"（控件赋值、对话框装配、`Visibility` 切换）估计不足一半 | 实测统计 |

关键证据：`MainWindow.Actions.cs:217-235` 在 `Task.Run` 的 lambda 里**直接遍历调用 `DeploymentService.Deploy` 并写日志**，把编辑逻辑和 UI 反馈耦合在同一段代码里；`MainWindow.xaml.cs:395-402` 直接把 `Gpu.WriteRegistryDisplayName` 的结果翻译成 `MessageBox`。`[E1]`
⇒ 新架构必须把这些搬进可测的服务层，UI 只保留"订阅视图模型 + 弹窗"。

**另有 0 个 NVAPI / DRS 相关代码**：全仓检索 `nvapi|NvAPI|DRS` 仅命中 `Strings.zh.cs:281-282` 与 `Strings.en.cs:281-282` 的两条**提示文案**（"本 Mod 需要 NVIDIA 驱动提供的 NGX/NVAPI/CUDA 接口"），**无任何 API 调用、无 profile 读写的代码路径**。`[E1]` 对本项目（要接管 NVIDIA Profile / Smooth Motion）而言，这是一块**完全空白的待建能力**。

---

## 2. 当前更新机制

### 2.1 完整源清单与顺序

源表定义在 `src/DLSSGManager/ModFetcher.cs:214-250`，按数组顺序即尝试顺序。`[E1]`

| # | Id | 官方? | URL 模板 | 类型 | 行号 |
|---|---|---|---|---|---|
| 1 | `raw` | ✅ | `https://raw.githubusercontent.com/{repo}/{ref}/{path}` | 逐文件 | `:226-227` |
| 2 | `ghproxy` | ❌ 镜像 | `https://gh-proxy.com/https://raw.githubusercontent.com/{repo}/{ref}/{path}` | 逐文件 | `:231-232` |
| 3 | `jsdelivr` | ❌ 镜像 | `https://cdn.jsdelivr.net/gh/{repo}@{ref}/{path}` | 逐文件 | `:235-236` |
| 4 | `ghfast` | ❌ 镜像 | `https://ghfast.top/https://raw.githubusercontent.com/{repo}/{ref}/{path}` | 逐文件 | `:240-241` |
| 5 | `codeload` | ✅ | `https://codeload.github.com/{repo}/zip/refs/heads/{ref}` | 整分支 zip | `:244-245` |
| 6 | `zipball` | ✅ | `https://api.github.com/repos/{repo}/zipball/{ref}` | 整分支 zip | `:248-249` |

- 仓库与引用是**代码内硬编码常量**：`RepoPath = "sdli1995/dlssg_for_sm86"`、`RepoRef = "main"`（`ModFetcher.cs:65-66`）。
- 模板替换为纯字符串 `Replace`（`ModFetcher.cs:253-257`）。
- 每个源内部**重试 2 次**，第二次前 `Task.Delay(2s)`（`ModFetcher.cs:585`、`:593-601`）。
- 用户可在 UI 里选定单源（`SourcePickerDialog`），选定后**不回落**（`ModFetcher.cs:303-313`、`:619-621`）；也可用环境变量 `DLSSGMANAGER_SOURCE` 做子串过滤（`:295`、`:315-326`）。

### 2.2 下载的文件清单（payload）

`ModFetcher.cs:84-95` 定义了**上游路径 → 本地路径**的固定映射（共 9 项，7 必选 + 2 可选）：

```
new("version.dll", "version.dll", true, true)              ← 必选 + 需签名
new("dlssg_sm86.ini", "dlssg_sm86.ini", true, false)
new("alternatives/winmm.dll",   "altnative/winmm.dll",   true, true)
new("alternatives/dinput8.dll", "altnative/dinput8.dll", true, true)
new("alternatives/dbghelp.dll", "altnative/dbghelp.dll", true, true)
new("alternatives/dxgi.dll",    "altnative/dxgi.dll",    true, true)
new("alternatives/d3d12.dll",   "altnative/d3d12.dll",   true, true)
new("README.md", "README.md", false, false)                ← 可选
new("THIRD_PARTY_NOTICES.txt", "THIRD_PARTY_NOTICES.txt", false, false)
```

`sourcePath` 与 `destinationPath` 之所以分开，是为了吸收上游布局变更（0.3.0 把 `altnative/` 改成了 `alternatives/`）——`ModFetcher.cs:79-82` 的注释说明了这一点。`[E1]`

> **注意**：`config/presets/` 的两档预设**不在 payload 里**，而 `docs/mod-files.md:14-16` 仍把它列为目录结构的一部分。这是文档残留（详见第 8 节）。

### 2.3 版本比较逻辑：**不存在**

**全仓检索 `System.Version` / `CompareTo` / `Version.TryParse` / `new Version`：零命中。** `[E1]`（检索范围 `src/` 全部 `.cs`）

唯一的 `Version.cs` 只做一件事：读**管理器自身**的 `AssemblyInformationalVersion` 并截掉 `+` 后的 git hash（`src/DLSSGManager/Version.cs:17-19`），**与 mod 版本无关**。

mod 版本的来源与去向：

| 环节 | 实现 | 证据 |
|---|---|---|
| 本地版本读取 | 优先读 INI **前 6 行**里的 `; Native x.y.z.` 横幅；否则读标记文件 `.manager-version` | `ModSource.cs:96-113`、`:148-164`、`:171-177`（正则 `Native\s+([0-9]+(?:\.[0-9]+)+)`） |
| 上游版本探测 | **两个 HTTP 探测**，非 API：① 仓库内 `dlssg_sm86.ini` 的横幅 ② 失败则 `README.md` 前 10 行的一级标题里抓第一个 `x.y.z` | `ModFetcher.cs:152-176`、`:183-194` |
| 探测地址 | `VersionProbeUrl` = raw 源的 `dlssg_sm86.ini`；`ReadmeUrl` = raw 源的 `README.md` | `ModFetcher.cs:136`、`:142` |
| 版本落盘 | 下载**成功后**把探测到的标签写进 mod 目录的 `.manager-version`（UTF-8 无 BOM），失败仅记日志 | `ModFetcher.cs:72`、`:662`、`:935-945` |
| 版本比较 | **无** | 零命中 |

**这是本审计最关键的结论之一**：所谓"更新"是**无条件覆盖**——`DownloadIntoAsync` 从不比较本地与远端版本，也不做"已是最新"的短路（`ModFetcher.cs:571-616`）。探测值 `detected` 只被用作**显示标签**传给下载器（`MainWindow.xaml.cs:644-648`、`App.xaml.cs:64-70`），且 `TryWriteVersionMarker` 写的是 `versionLabel ?? ""`（`ModFetcher.cs:662`），即**探测失败时会写入空标记，抹掉旧版本号**。`[E1]`

> 对任务书"更新系统为最高优先级功能 + 需要版本比较 + Provider 双分发模型"的要求，现有实现的差距是**结构性的**：需要引入独立的版本模型（`(版本号, 修复限定词, published_at)` 三元组）、Release API 解析、以及"检查 vs 执行"的分离。现有代码**没有一层可以复用**。

### 2.4 GitHub Release API：**未使用**

- `api.github.com` 在主机白名单内（`ModFetcher.cs:40`），但唯一用途是 `zipball` 端点（`ModFetcher.cs:249`）。
- 全仓检索 `releases`、`GitHubApi`、`/releases/`：仅命中注释与文档文案（`ModFetcher.cs:79`、`:220`、`ModSource.cs:93`、`Store.cs:179`），**无 API 调用**。`[E1]`
- ⇒ 现有代码**完全不认识 Release / tag / asset 概念**：没有 asset 名解析、没有排除词表、没有平台/架构过滤、没有 `published_at`。这些在本项目全部要新建。

### 2.5 缓存 / ETag / 条件请求：**全部没有**

全仓检索 `ETag` / `If-None-Match` / `If-Modified-Since` / `Cache-Control`：**零命中**。`[E1]`

`HttpClient` 构造里只有三条设置（`ModFetcher.cs:416-426`）：

```csharp
var handler = new HttpClientHandler
{
    AllowAutoRedirect = false,                       // 手动跟随，逐跳复检
    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
};
var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
client.DefaultRequestHeaders.UserAgent.ParseAdd("DLSSGManager/1.0");
```

- **无本地下载缓存**：staging 目录每次新建为 `%TEMP%\dlssg_<guid>`，`finally` 里整目录删除（`ModFetcher.cs:633`、`:677-680`）。
- **无增量**：每次更新把 9 个文件全部重下；整分支 zip 模式下一次拉 ~470 MB（`ModFetcher.cs:219-223` 注释自述）。
- **无断点续传**：`File.Create(target)` 从头写（`ModFetcher.cs:778`）。

---

## 3. 当前下载机制与安全

### 3.1 Host 白名单

`ModFetcher.cs:34-46`（7 项）：

```
"github.com", "codeload.github.com", "raw.githubusercontent.com", "api.github.com",
"cdn.jsdelivr.net", "gh-proxy.com", "ghfast.top"
```

策略实现（`ModFetcher.cs:346-358`）：

```csharp
if (!uri.Scheme.Equals(Uri.UriSchemeHttps, ...)) return false;     // 仅 HTTPS
if (!AllowedHosts.Contains(uri.Host, ...)) return false;
if (proxyRouted) return true;                                       // ← 系统代理时跳过 IP 校验
addresses = Dns.GetHostAddresses(uri.Host);
return addresses.Length > 0 && addresses.All(IsPublicAddress);
```

- **IP 段黑名单**做得比较完整：IPv4 覆盖 `0/8、10/8、127/8、169.254/16、172.16/12、192.168/16、192.0.0/24、192.0.2/24、198.18-19/24、198.51.100/24、203.0.113/24、100.64/10、≥224`，IPv6 覆盖 loopback / `fc00::/7` / `fe80::/10` / `ff00::/8` / `2001:db8::/32`（`ModFetcher.cs:380-414`）。
- **重定向手动跟随，最多 5 跳，每跳复检**（`ModFetcher.cs:429-460`）。
- ⚠️ **代理例外是逃生通道**：`ResolveProxyRouted` 只要发现系统代理会把请求送去另一个 authority，就**整体跳过 IP 校验**（`:365-378`）。在有 `.pac`/代理配置的机器上，"解析地址必须全部是公网"这条防线失效。
- ⚠️ **白名单缺 `github.com` 的实际用途**：`github.com` 在名单里但无模板使用它；新增 Release Asset 下载时必须补 `objects.githubusercontent.com`（GitHub Release asset 的真实 CDN），否则 302 目标会被 `IsAllowedAddress` 拒掉。**这是新架构必然踩到的坑。**

### 3.2 签名校验：**两条路径，只有一条完整——且下载路径用的是不完整的那条**

这是本次重构的重点，必须精确区分：

#### 路径 A：`DeploymentService.ReadSignerCertificate` → `IsSignatureIntact`（**完整**）

`src/DLSSGManager/DeploymentService.cs:76-93` 与 `:146-213`：

```csharp
public static X509Certificate2? ReadSignerCertificate(string path, out bool signatureIntact)
{
    signatureIntact = false;
    X509Certificate2 cert;
    try { cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)); }
    catch { return null; }
    signatureIntact = IsSignatureIntact(path);   // ← 真正的完整性校验
    return cert;
}
```

`IsSignatureIntact` 通过 P/Invoke 调 **`WinVerifyTrust`**（`wintrust.dll`，`:95-96`），用 `WINTRUST_ACTION_GENERIC_VERIFY_V2` + `WTD_UI_NONE` + `WTD_REVOKE_NONE`（`:126-133`、`:164-178`），结果解释为 **fail-closed**：

```csharp
if (result == S_OK) return true;
if (result == CERT_E_UNTRUSTEDROOT) return true;   // 自签名证书的唯一合法形态
if (result == TRUST_E_NOSIGNATURE || result == TRUST_E_BAD_DIGEST) { log; return false; }
AppPaths.Log($"签名校验返回未知结果 0x{result:X8}，按未签名处理：{path}");
return false;                                       // ← 未知结果一律视为未签名
```

并正确关闭 `WTD_STATEACTION_CLOSE` 释放句柄（`:180-183`）。**这是一份高质量实现，可原样提取复用。**

#### 路径 B：`ModFetcher.Verify`（**不完整——缺完整性校验**）

`src/DLSSGManager/ModFetcher.cs:817-871`：

```csharp
X509Certificate2? cert;
try { cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)); }
catch { unsigned.Add(...); continue; }              // 仅"有没有证书块"
using (cert)
{
    var subject = cert.Subject ?? "";
    if (!subject.Contains(ExpectedSignerSubject, ...)) unsigned.Add(...);       // 仅比对主题含 "DLSSG"
    else if (!string.Equals(cert.Thumbprint, PinnedCertThumbprint, ...)) pinMismatch.Add(...);
}
```

**它从未调用 `IsSignatureIntact` / `WinVerifyTrust`，也从未调用 `DeploymentService.IsProjectSigned`。** `[E1]`（检索确认：`IsSignatureIntact` 的唯一调用点是 `DeploymentService.cs:91`）

`DeploymentService.cs:46-53` 的注释**恰好预见了这个漏洞**，但只在自己的类里堵住了：

```csharp
/// <see cref="X509Certificate.CreateFromSignedFile"/> only extracts the certificate — it does not
/// check that the signature still matches the bytes. A tampered DLL would therefore look
/// "signed" if that were the only check, which matters because downloads may come from a mirror.
```

**后果（严重）**：`X509Certificate.CreateFromSignedFile` 只**解析** PKCS#7 签名块取出证书，不校验摘要。因此任何**主题含 `DLSSG`、指纹等于 pin** 的文件都会通过 `Verify`——包括：

1. 该证书签发的**任意其他文件**（含被替换的旧版本二进制、含注入内容后重新签名的 DLL）；
2. 若 mirror 场景下有人持有同一张自签名证书的私钥则更糟（自签名证书私钥只在上游手里，故此项风险依赖于密钥不泄漏）。

实际攻击面收敛为"**上游同一证书签名的其他文件**"与"**pin 比对失效时的任意 DLSSG 主题证书**"。但**"镜像返回与官方不同的已签名文件"这一威胁模型是模块头注释（`ModFetcher.cs:14-29`）明确声称已覆盖的**：

```
/// · The payload is verified after download: every proxy DLL must carry a valid Authenticode
///   signature. For third-party mirrors the signer must also match a pinned certificate thumbprint
```

声称 `valid Authenticode signature`，实现只做了"有证书块 + 主题片段 + 指纹"。**注释与实现不一致，且这是安全属性上的不一致。**

#### 证书指纹常量当前值

`src/DLSSGManager/ModFetcher.cs:58`：

```csharp
private const string PinnedCertThumbprint = "85BA66762F851E49148D706915D09026281418E6";
```

- 注释自述为 SHA-1 指纹，对应证书 `CN=DLSSG for SM86 (self-signed)`，并声明这是 0.3.0 起的值、旧的 `A994735E…` 属于 0.2.x（`ModFetcher.cs:48-57`）。
- **SHA-1 指纹本身是弱哈希**，且注释未说明为何用 SHA-1 而非 SHA-256（`X509Certificate2.Thumbprint` 是 SHA-1，这是 .NET 默认行为；更高安全性应改用 `GetCertHashString(HashAlgorithmName.SHA256)`）。`[E1]`
- **无法在本快照验证该常量与真实 mod 二进制是否一致**：仓库内无 `mod/` 目录，`src/DLSSGManager/mod`、`<repo>/mod` 均不存在（实测 `Test-Path` 为 `False`）。`[未确认]`
- **测试也不校验它**：`test/Harness/Program.cs:2270-2274` 只**打印**实际指纹并断言"可读取"，注释自称"pinned certificate must match what the shipped DLLs actually carry"，但**没有断言相等**：

```csharp
var thumb = CertificateThumbprint(realDll);
Console.WriteLine("      实际证书指纹: " + (thumb ?? "(无)"));
Check("证书指纹可读取", thumb is not null, realDll);     // ← 只测非 null
```

⇒ **证书轮换（上游换证书）不会让任何测试变红**，只会在镜像源上让用户下载全部失败。这是一个真实的运维盲区。

#### 官方源 vs 镜像源的差异化处理

`ModFetcher.cs:857-868`：

```csharp
if (pinMismatch.Count > 0)
{
    var detail = Loc.T("Fetch.PinDetail", Loc.Join(pinMismatch));
    if (!officialSource) return (false, Loc.T("Fetch.PinMismatch", detail));  // 镜像：拒绝
    AppPaths.Log(Loc.T("Fetch.PinWarning", detail));                          // 官方：仅警告
    return (true, Loc.T("Fetch.PinAccepted", ...));                           // ← 仍然放行
}
```

对 GitHub 官方源（`raw`/`codeload`/`zipball`）**指纹不符也放行**，理由是"上游可能换证书"（`:865-866`）。设计取舍可以理解，但结果是：**官方源上指纹 pin 完全不生效**，仅剩"主题含 DLSSG"这一条 6 字符的弱检查。而 `Subject.Contains("DLSSG")` 对自签名证书是**可任意伪造**的（攻击者自签一张 `CN=DLSSG` 即可，若同时命中官方源分支则直接通过）。

#### 其他签名相关位置

- `DeploymentService.IsProjectSigned`（`:54-66`）：`ReadSignerCertificate` + `signatureIntact` + `Subject.Contains("DLSSG")`。**用于部署/状态/清理判据，不含指纹比对**。
- `ModSource.ImportProxy`（`ModSource.cs:266-271`）：用户导入的 DLL 只**报告**签名情况，不拒收（`:216-218` 注释明确"Nothing about the file is verified"）。

### 3.3 SHA-256 的使用环节

`Sha256` 唯一实现在 `DeploymentService.cs:40-44`，`Convert.ToHexString(SHA256.HashData(stream))`。用途清单：

| 环节 | 位置 | 是否校验 |
|---|---|---|
| 部署时记录写入文件的哈希 | `DeploymentService.cs:499-500`、`:526-530` | 记录 |
| 部署前判定"同名文件是否我方"（防覆盖） | `DeploymentService.cs:428`、`:267-278` | 校验 |
| 状态检查：比对记录哈希 | `DeploymentService.cs:870-871` | 校验 |
| 恢复：按记录哈希判定可删 | `DeploymentService.cs:650`、`:298-309` | 校验 |
| 隔离残留识别 | `AntiCheat.cs:272` | 校验 |
| 社区构建识别（1 个固定哈希） | `ModFetcher.cs:106-113`、`:202-212` | 校验 |
| **下载内容校验** | — | **无** |

⇒ **下载阶段完全不做内容哈希校验**（`ModFetcher.cs` 中 `Sha256` 仅出现在社区构建识别路径）。`[E1]`

### 3.4 解压安全

`ModFetcher.cs:724-740`：

```csharp
progress?.Report(Loc.T("Fetch.Extracting"));
Directory.CreateDirectory(staging);
ZipFile.ExtractToDirectory(archive, staging, overwriteFiles: true);   // ← 唯一的解压调用
var inner = Directory.EnumerateDirectories(staging).FirstOrDefault();
if (inner is null) throw new InvalidOperationException(Loc.T("Fetch.BadArchive"));
foreach (var entry in Directory.EnumerateFileSystemEntries(inner)) { ... }
```

| 检查项 | 状态 | 说明 |
|---|---|---|
| Zip Slip（`..` 逃逸） | **依赖 BCL** | `ZipFile.ExtractToDirectory` 在 .NET 8 内部会规范化并拒绝越界条目；代码**没有自己的校验**。可接受但需在文档中明确依赖 |
| 符号链接逃逸 | **未处理** | 未检查 `ExternalAttributes` / `UnixFileMode`；.NET 的 `ExtractToDirectory` 会解析 symlink 条目（历史上有 CVE 记录）。`[E1]` 未见防护 |
| 解压炸弹 | **有上限** | `MaxArchiveBytes = 256 MiB`（`:63`），在 `Content-Length` 声明超大时先拒（`:694-695`），**流式写入时累计超限也拒**（`:710`）。⚠️ 但 256 MiB 是**压缩包字节数**上限，不是**解压后**体积上限——深度压缩的炸弹仍可能写满磁盘 |
| 条目数量上限 | **无** | — |
| 解压后文件名白名单 | **无** | 但后续 `Publish` 只从 staging 里按 `Payload` 固定名单取（`:879-893`），多出来的条目不会进入 mod 目录 |

`CopyInto` 有明确的逃逸防护（`ModFetcher.cs:947-959`）：

```csharp
var rootFull = Path.GetFullPath(destinationRoot).TrimEnd('\\') + "\\";
var destFull = Path.GetFullPath(destinationFile);
if (!destFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException(Loc.T("Fetch.EscapeAttempt"));
```

⚠️ 但这个判断是**字符串前缀比较**，`C:\mod` 与 `C:\modular` 的前缀关系被末尾补 `\` 化解，正确；不过它**不解析重解析点（junction/symlink）**——若 `destination` 内某子目录是指向别处的联接，写入仍会落到目标位置。`[E1]` 低风险场景。

### 3.5 下载过程的体积上限

- 整包：`MaxArchiveBytes = 256L * 1024 * 1024`（`ModFetcher.cs:63`），三处生效（`:694`、`:710`、`:789`）。
- ⚠️ 逐文件模式下 `total` 是**所有文件累计**（`:754`、`:787`），所以 9 个文件的合计上限也是 256 MiB；单文件无独立上限。`[E1]`

---

## 4. 部署事务性

### 4.1 `DeploymentService.Deploy`（`DeploymentService.cs:373-591`）逐环节对照

任务书要求的事务模型：`Stage → Verify → Backup → Install → Validate → Commit，失败 Rollback`。

| 环节 | 现状 | 证据 | 缺口 |
|---|---|---|---|
| **Stage** | **无**。直接写游戏目录：`File.Copy(source.DllPath(proxy), tmp)` → `File.Move(tmp, proxyDest, overwrite: true)`；INI 同理 | `:488-494` | ❌ 没有暂存区。写入前只做前置检查（源有效、目录存在、进程未运行、反作弊、可写、入口名可用、同名非我方文件占用）`:377-434` |
| **Verify** | **部分**。校验对象是 **mod 源**（`source.IsValid`，只验证"有 INI + 至少一个 DLL"），**不是待写入的字节**。不重算源 DLL 哈希、不做签名校验 | `:377-387`；`ModSource.cs:63-90` | ❌ 部署时不验证源文件完整性。若 mod 目录里的 DLL 被本地篡改，直接部署 |
| **Backup** | **仅对 INI**，且仅在"现有 INI 不是我们的"时备份 | `:483-486`、`:593-607` | ❌ 代理 DLL 不备份（但同名非我方文件会**拒绝部署**，见 `:428-434`，所以不构成覆盖风险）；❌ 备份**没有校验**：`File.Copy` 后只算哈希记录，不比对原文件哈希 |
| **Install** | 有，但**两次非原子操作**：DLL 与 INI 分别 `Copy+Move` | `:488-494` | ❌ 中间失败会留下"只有 DLL 没有 INI"或反之的半成品；`catch` 只删两个 `.dlssgtmp` 临时文件（`:579-588`），**不回滚已就位的那一个** |
| **Validate** | **无**。写入后只 `Sha256(proxyDest)` / `Sha256(iniDest)` 记录到 `DeploymentInfo`，不与任何期望值比对 | `:499-500`、`:511-531` | ❌ 没有"写完再读回校验"步骤 |
| **Commit** | 内存对象赋值：`game.Deployment = new DeploymentInfo{...}` | `:511-544` | ⚠️ 持久化发生在**UI 层之后**：`MainWindow.Actions.cs:73` 才 `LibraryStore.Save(_data)`。若此刻崩溃，磁盘上文件已改、记录未落盘 → 变成"未知来源的文件"（`Evaluate` 会判 `NotDeployed`+`ManualInstall`，`:844-847`） |
| **Rollback** | **无** | — | ❌ 全程无回滚。异常路径只清理临时文件 |

**一句话结论**：现有实现是 **`前置检查 → 就地写入 → 记记录`**，**不是事务**。它靠"写入前把所有能想到的风险都检查掉"来保证安全（这部分做得很细致），而不是靠"失败可回滚"。

### 4.2 `DeploymentService.Restore`（`:609-742`）逐环节对照

| 步骤 | 现状 | 证据 |
|---|---|---|
| 前置检查 | 目录存在、游戏进程未运行、目录可写 | `:613-630` |
| 删代理 | 遍历 `OwnedEntryNames(prev)`（6 个已知名 + 记录里的文件名），`IsOurs(path, RecordedHashFor(prev, name))` 为真才删；否则记"保留" | `:643-660` |
| 删 INI | `byHash = IsOurs(iniPath, prev.IniSha256)`；`byBanner = LooksLikeProjectIni(iniPath)`；二者其一即删 | `:664-683` |
| 删隔离残留 | `AntiCheat.FindQuarantinedCopies` + `IsOurs` 双重门禁 | `:687-702` |
| **回填备份** | `File.Copy(b.StoredPath, dest, overwrite: true)` | **`:715-716`** |
| 可选删日志目录 | `Directory.Delete(logs, recursive: true)` | `:722-730` |
| 清记录 | `game.Deployment = null` | `:732` |

#### ❌ **`Restore` 回填备份时不重新校验记录的 SHA256**

`Restore` 回填分支全文（`DeploymentService.cs:705-720`）：

```csharp
if (prev is not null && prev.Backups.Count > 0)
{
    foreach (var b in prev.Backups)
    {
        if (!File.Exists(b.StoredPath))
        {
            r.Note(Loc.T("Restore.BackupLost", b.FileName));
            continue;
        }
        var dest = Path.Combine(game.RenderDir, b.FileName);   // ← 无校验、无逃逸检查
        File.Copy(b.StoredPath, dest, overwrite: true);        // ← 直接覆盖
        removed++;
        r.Note(Loc.T("Restore.BackupRestored", b.FileName));
    }
}
```

`BackupItem` **记录了** `Sha256` 与 `Size`（`Models.cs:121-127`、`DeploymentService.cs:598-604`），但此分支**既不比对 `b.Sha256`，也不比对 `b.Size`**——这两个字段在恢复路径上**完全未被使用**（唯一读取处是序列化/反序列化本身）。

⇒ 若 `%APPDATA%\DLSSGManager\restore\...` 下的备份文件被替换或损坏，恢复会把**任意内容**以原始文件名覆盖回游戏目录。备份目录位于用户可写路径，且 `library.json` 里的 `StoredPath` 是绝对路径（`DeploymentService.cs:596`），**该路径本身也被无条件信任**：

```csharp
var stored = Path.Combine(restoreFolder, Path.GetFileName(path));   // 写入时：取文件名，安全
// 恢复时：File.Copy(b.StoredPath, dest)  ← StoredPath 来自 JSON，未与 RestoreRoot 校验是否同源
```

⇒ **`StoredPath` 可指向任意路径**（改 `library.json` 即可让恢复从任意位置拉文件覆盖进游戏目录），构成"配置文件驱动的任意文件写入"。这一点在文件级威胁模型里属于**中危**（需要本地写 `%APPDATA%` 权限），但对一个"会写别人游戏目录"的工具而言，判据应该更严。

#### ⚠️ 路径拼接的逃逸防护

| 位置 | 拼接 | 防护 |
|---|---|---|
| 部署代理 | `Path.Combine(game.RenderDir, proxy)`（`:427`） | `proxy` 只能来自 `source.AvailableProxies`（`ModSource.ProxyCandidates` ∪ `altnative\*.dll` 的 `Path.GetFileName`），**实际不可注入** |
| 部署 INI | `Path.Combine(game.RenderDir, ModSource.IniName)`（`:436`） | 常量，安全 |
| **恢复备份** | `Path.Combine(game.RenderDir, b.FileName)`（`:715`） | ❌ **`b.FileName` 来自 JSON，无 `..`/分隔符过滤、无 `GetFileName` 归一化、无前缀校验** |
| 隔离残留 | `File.Delete(copy)`，`copy` 来自 `Directory.EnumerateFiles(renderDir, baseName + ".*")`（`AntiCheat.cs:261-272`） | 安全（受枚举目录约束） |
| mod 发布 | `ModFetcher.CopyInto` 有前缀校验（`:947-959`） | 安全 |

⇒ **`Restore` 的备份回填是唯一存在路径逃逸面的写入点**：若 `library.json` 中某个 `BackupItem.FileName` 为 `..\..\somewhere\evil.dll`，`Path.Combine` 会产生逃逸路径并被 `File.Copy` 接受（`File.Copy` 会创建目录吗？不会——目录不存在则抛异常；但若目标目录已存在，写入就成功）。**判为中危：需要本地写 `library.json` 的能力，但后果是写到游戏目录之外。**

### 4.3 所有权判据 `IsOurs` 如何工作

`DeploymentService.cs:263-278`：

```csharp
public static bool IsOurs(string path, string? recordedHash)
{
    if (!File.Exists(path)) return false;
    if (IsProjectSigned(path)) return true;                                    // ① 签名路径
    if (!string.IsNullOrEmpty(recordedHash))
        try { return string.Equals(Sha256(path), recordedHash, ...); } catch { return false; }  // ② 哈希路径
    return false;
}
```

三层判据体系（按严格度递减）：

1. **`IsProjectSigned`**（`:54-66`）= 有证书块 + `WinVerifyTrust` 判定完整 + `Subject.Contains("DLSSG")`。**注意：不含指纹比对**，所以任何自签 `CN=*DLSSG*` 的文件都会被认作"我方的"。对本机游戏目录场景可接受（攻击者若能往游戏目录写文件，本身已有更强能力）。
2. **记录哈希**（`:267-278`、`:298-309`）：按 `DeploymentInfo.Files[].Sha256` 或旧字段 `ProxySha256` / `IniSha256` 比对。
3. **社区构建固定哈希**（`ModFetcher.cs:106-113`）：仅 1 个哈希 `65E6F9…C2B`。

**入口名集合的全集**（`:287-295`）：

```csharp
var names = new List<string>(ModSource.KnownProxyNames);        // 6 个：version/winmm/dinput8/dbghelp/dxgi/d3d12 + winhttp
if (prev is not null) names.AddRange(prev.Files.Select(f => Path.GetFileName(f.FileName)));
return names.Where(n => !string.Equals(n, ModSource.IniName, ...)).Distinct(...);
```

⇒ 集合 = `KnownProxyNames`（`ModSource.cs:34-35`，即 6 个候选 + 已废弃的 `winhttp.dll`）∪ 记录里出现过的文件名（覆盖用户导入的自定义名）。

**已知的设计取舍与残留风险**：

- INI 的所有权靠**内容启发式**：`LooksLikeProjectIni`（`:748-762`）检查前 4 行是否含 `Native x.y` 或全文是否含 `dlssg_sm86` 子串。**第二个条件是子串匹配**——任何提到 `dlssg_sm86` 的第三方 INI 都会被判定为"我方的"并在恢复时删除。`:744-746` 的注释声称"Deliberately stricter than a bare keyword search"，但第二个分支实际上就是关键词搜索。**注释与实现不一致（低危，误删风险）。**
- **多代理并存语义已经反转**：0.3.3 起同目录多个本项目代理被视为**设计内**（互为 standby，`Deployment.cs:450-475`、`:831-840`），旧 `winhttp.dll` 仍会被 `PruneSupersededEntries` 按"我方签名"清理（`ModFetcher.cs:903-926`）。

---

## 5. 游戏检测与 API 判定

### 5.1 Steam 库发现（`Detection.cs:43-79`）

1. 枚举所有**固定盘 + 可移动盘**（`DriveType.Fixed` / `Removable`，`:50`）。
2. 每个盘拼 5 个候选子路径：`Steam`、`SteamLibrary`、`Games\Steam`、`Program Files (x86)\Steam`、`Program Files\Steam`（`:51`）。
3. 额外硬编码 `C:\Program Files (x86)\Steam`（`:58`，与候选表重复，冗余无害）。
4. 判定标准：该目录下存在 `steamapps\libraryfolders.vdf`（`:62`）。
5. 用**正则**（非 VDF 解析器）抽 `"path" "<值>"`，并把 `\\` 还原为 `\`（`:71-75`）。

### 5.2 Steam 游戏扫描（`Detection.cs:82-119`）

- 遍历每个库的 `steamapps\appmanifest_*.acf`（`:91`）。
- 用正则从 ACF 取 `name` 与 `installdir`（`:121-125`）；`installdir` 为空则跳过（`:103`）。
- 游戏根目录 = `<lib>\steamapps\common\<installdir>`，不存在则跳过（`:104-105`）。
- 命中判定 = `FindRenderTarget(gameRoot)`（`:108`）非 null。
- 命名取自 `appmanifest` 的 `name`，不是文件夹名（`:111`，注释 `:81` 说明动机）。

### 5.3 "渲染 EXE" 如何认定：**只看文件名 + 大小**

`Detection.cs:261-282`：

```csharp
exes = new DirectoryInfo(dir).GetFiles("*.exe")
    .Where(f => !ExeNoise.Any(n => f.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
    .OrderByDescending(f => f.Length)                        // ← 只看大小
    .ToList();
...
var preferred = exes.FirstOrDefault(f =>
    f.Name.Contains(folder, ...) ||                          // ← 文件名含所在文件夹名
    f.Name.Contains("Shipping", ...) ||                      // ← UE 惯例
    f.Name.Contains("Win64", ...));                          // ← UE 惯例
return (preferred ?? exes[0]).FullName;                      // ← 否则取最大的
```

- 黑名单 `ExeNoise`（`Detection.cs:36-41`，15 项）：`unitycrashhandler`、`crashhandler`、`crashreport`、`launcher`、`unins`、`setup`、`vcredist`、`dxsetup`、`easyanticheat`、`battleye`、`be_service`、`notification_helper`、`installer`、`updater`、`report`。
- **无 PE 头解析、无版本信息、无导入表检查、无图标/清单检查**。`[E1]`
- **无 "渲染 EXE vs 启动器 EXE" 的语义区分**——`launcher.exe` 只被黑名单排除，"主程序"与"启动器"的区别靠文件名猜测。
- 从标记文件向**上最多 3 层**找 EXE（`ResolveFromMarker` `:211-229`），找不到则把标记所在目录当渲染目录（`:228`，`ExePath = ""`）。

### 5.4 DX11 / DX12 / Vulkan 判定：**全仓零命中**

检索 `DX11|DX12|D3D11|D3D12|Vulkan|GraphicsApi|d3d11`（`src/` 全部文件）：`[E1]`

- `d3d12` 的全部命中都是**代理入口 DLL 的名字**（`ModSource.cs:23`、`ModFetcher.cs:92`）或注释/文案，**没有一处是 API 类型判定**。
- `d3d11` / `Vulkan` / `DX11` **完全零命中**。
- `GameEntry` 中不存在任何 API 字段（见第 6 节）。

⇒ **没有任何 DirectX / Vulkan 判定能力。** 代理入口名的选择（`version.dll` 优先）也**不依据游戏的图形 API**，纯按"安全名优先"的静态顺序（`ModSource.cs:14-23`）。

### 5.5 商店覆盖：**仅 Steam**

检索 `Epic|GOG|Galaxy|Ubisoft|Origin|EA App|Bethesda|Rockstar|Battle.net|XboxGames|MSStore|WindowsApps`：**零命中**。`[E1]`

- 唯一的商店级发现逻辑是 Steam（`Detection.cs:43-119`）。
- 其余入口是**用户手动选目录**：`OpenFolderDialog`（`MainWindow.xaml.cs:762`、`MainWindow.Actions.cs:318`、`:453`）。
- 另外 `ScanFolder` 支持任意目录树递归，`MaxDepth = 6`（`Detection.cs:34`、`:147-170`），跳过以 `.` 开头与 `$RECYCLE.BIN`（`:167`）。

### 5.6 标记文件与"手装 mod"识别

`Detection.cs:23-32`：标记 = `nvngx_dlssg.dll`（游戏自带帧生成负载）**或** `dlssg_sm86.ini`（本项目 INI，用于识别手装）。

```csharp
public const string DlssgMarker = "nvngx_dlssg.dll";
public const string ModIniMarker = "dlssg_sm86.ini";
public static readonly string[] Markers = { DlssgMarker, ModIniMarker };
```

⇒ 后果：**"目录里有 nvngx_dlssg.dll"被同时用作两个语义**——(a) 游戏支持帧生成；(b) 这是渲染目录。`Strings.zh.cs:120` 的文案也基于此。对新架构而言，(a) 需要更严谨的判定（该 DLL 的版本/来源），(b) 不足以保证"这就是渲染目录"（例如游戏在 `bin\x64` 与 `_retail_` 各放一份时的歧义）。

### 5.7 逐项反查：本机商店/API 事实

| 检查项 | 结论 | 证据等级 |
|---|---|---|
| 是否有 DX11/DX12/Vulkan 判定 | **否** | `[E1]` 零命中 |
| 是否覆盖非 Steam 商店 | **否** | `[E1]` 零命中 |
| 是否识别游戏引擎架构（UE/Unity 等） | **部分**：仅靠 EXE 名含 `Shipping`/`Win64` 与文件夹名做**启发式**，无引擎标识解析 | `Detection.cs:275-281`；`[E1]` |
| 是否解析 PE 头 / 版本资源 | **否** | `[E1]` |
| 是否读取游戏自身配置（`steam_appid.txt`、注册表等） | **否**，仅 `appmanifest_*.acf` + `libraryfolders.vdf` | `[E1]` |

---

## 6. 数据模型缺口

### 6.1 `Models.cs` 现有字段

**`GameEntry`**（`Models.cs:160-283`）：

| 字段 | 类型 | 行号 | 备注 |
|---|---|---|---|
| `Id` | `string` (GUID N) | `:177` | |
| `Name` | `string` | `:178` | |
| `RenderDir` | `string` | `:181` | 真实渲染目录 |
| `ExePath` | `string` | `:184` | 可选，仅用于启动按钮 |
| `PreferredProxy` | `string` | `:187` | 默认 `"自动"`（常量 `AutoProxy :167`） |
| `Notes` | `string` | `:189` | **扫描时被塞入商店名**（`MainWindow.Actions.cs:409`） |
| `Profile` | `GameProfile` | `:190` | |
| `Deployment` | `DeploymentInfo?` | `:191` | |
| `Protection` | `ProtectionReport?`（`[JsonIgnore]`） | `:200-211` | |
| `Status` / `StatusDetail` | `GameStatus` / `string`（`[JsonIgnore]`） | `:233`、`:235` | |
| 派生只读属性 | `HasKernelAntiCheat`、`ShowAntiCheatBanner`、`AntiCheatTitle`、`AntiCheatBody`、`StatusText`、`StatusColor`、`Subtitle` | `:214`、`:224`、`:226`、`:229`、`:238`、`:255`、`:266` | |

**`GameProfile`**（`Models.cs:41-118`）——10 个持久化字段，全部是**mod INI 的键**：

| 字段 | 默认 | 行号 | 对应 INI 键 |
|---|---|---|---|
| `Enabled` | `true` | `:55` | `Enabled` |
| `OptimizedTier` | `1` | `:62` | `Optimized` |
| `Optimized`（`bool?`，遗留） | `null` | `:69` | — 仅用于旧库迁移 |
| `Preset` | `"Auto"` | `:72` | `Preset` |
| `Router` | `"SM86"` | `:75` | `Router`（0.2.x） |
| `KernelImage` | `"PTX"` | `:78` | `KernelImage`（0.2.x） |
| `HardwareBilinear` | `false` | `:81` | `HardwareBilinear`（0.2.x） |
| `MaxGeneratedFrames` | `3` | `:85` | `MaxGeneratedFrames` |
| `LogLevel` | `1` | `:88` | `Level` |
| `Diagnostics` | `false` | `:91` | 追加 `[Diagnostics]` 段 |

**`GameStatus`**（`Models.cs:26-38`）——**5 个值**：

```csharp
NotDeployed,   // 目录里没有我方文件
Deployed,      // 代理与 INI 都在，且哈希与记录一致
Modified,      // 文件在，但与我方写入的不符
Missing,       // 有记录，但文件没了
Unknown,       // 文件在，但无法确认渲染目录
```

### 6.2 对照任务书要求的 `GameInfo`：**缺失 10 项中的 9 项**

任务书要求的字段：`GameName` / `Store` / `InstallPath` / `LauncherExe` / `RendererExe` / `GraphicsApi` / `Engine` / `Architecture` / `AntiCheat` / `InstalledPatches` / `VerifiedInstallPlan`。

| 要求字段 | 现状 | 缺口说明 | 证据 |
|---|---|---|---|
| `GameName` | ✅ `Name` | 概念等价（但来源是文件夹名/ACF，非游戏元数据） | `Models.cs:178` |
| `Store` | ❌ **无结构字段** | 商店名被写进自由文本 `Notes`（"Steam" 或本地化串），之后无法机读 | `MainWindow.Actions.cs:409`；`Detection.cs:112`；`Detection.cs:138` |
| `InstallPath` | ⚠️ **仅有 `RenderDir`** | 没有"游戏根目录"概念；`RenderDir` 是**渲染 EXE 所在目录**，两者在多级目录游戏里不同（异环 `Client\WindowsNoEditor\HT\Binaries\Win64`，`README.md:20`） | `Models.cs:181` |
| `LauncherExe` | ❌ 无 | `ExePath` 只有单值语义，且被注释为"用于启动按钮"，未区分启动器/渲染器 | `Models.cs:184`、`:183` |
| `RendererExe` | ⚠️ 部分（`ExePath`） | 命名不表达"渲染器"语义，且可能为 `""`（`Detection.cs:228`） | `Models.cs:184` |
| `GraphicsApi` | ❌ **无**（第 5.4 节已证全仓无 API 判定） | | — |
| `Engine` | ❌ 无 | 引擎信息仅存在于启发式字符串里（`Shipping`/`Win64`） | `Detection.cs:275-281` |
| `Architecture` | ❌ 无（`GameEntry` 上无） | GPU 侧有 `GpuInfo.HardwareFamily`（`Gpu.cs:16`），但**不属于游戏模型**，且全局单值，无法表达"每个游戏需要哪个 SM" | `Gpu.cs:10-20` |
| `AntiCheat` | ⚠️ 有但不持久 | `Protection` 标了 `[JsonIgnore]`，每次检查重扫；无"已确认的厂商/版本"留档 | `Models.cs:199`；`DeploymentService.cs:826` |
| `InstalledPatches` | ❌ 无 | `DeploymentInfo.Files` 只记录本管理器写的文件，不含"该游戏上已应用的其他 patch" | `Models.cs:138-158` |
| `VerifiedInstallPlan` | ❌ 无 | 没有"计划 → 校验 → 执行"的对象；`Deploy` 的决策全部是**过程内局部变量**（`proxy`、`redundantProxies`、`backups`） | `DeploymentService.cs:417-454` |

### 6.3 对照任务书要求的 `GameStatus`：**命名与语义都不匹配**

| 任务书要求 | 现状对应 | 差距 |
|---|---|---|
| `Installed` | ≈ `Deployed` | 概念接近，但现状是"记录+哈希一致"，不是"安装计划完成" |
| `Loaded` | ❌ **无** | 没有"游戏已加载过该 mod"的证据链（现状完全不看 mod 的日志 `dlssg_sm86\logs`，只在 `LatestLogFile` 里用于"打开日志"按钮：`DeploymentService.cs:930-938`） |
| `Requested` | ❌ 无 | 无"用户已请求但未应用"的中间态 |
| `Applied` | ❌ 无 | 无"已写入但未验证生效"的中间态 |
| `Verified` | ❌ 无 | `Deployed` 只证明"文件是我们写的、没被改"，不证明"帧生成真的在工作" |

⇒ 任务书的状态机是**面向"可验证生效"**的（Requested → Applied → Verified），现状的状态机是**面向"文件一致性"**的。两者不可映射，必须重建。

---

## 7. 缺陷与安全隐患清单

严重度定义：**高** = 直接导致安全问题或数据丢失 / **中** = 在特定条件下造成错误行为或不可恢复状态 / **低** = 维护性、一致性、健壮性问题。

### 高

| # | 缺陷 | 位置 | 说明 | 修复建议 |
|---|---|---|---|---|
| **H-1** | **下载校验不验证签名完整性**——`Verify` 只用 `X509Certificate.CreateFromSignedFile` + `Subject.Contains("DLSSG")` + `Thumbprint`，**从不调用 `WinVerifyTrust`** | `ModFetcher.cs:836`、`:846-850`（对比正确的实现 `DeploymentService.cs:91`、`:146-213`） | 模块头注释声称"must carry a valid Authenticode signature"（`ModFetcher.cs:22-25`），实现只检查证书块存在性。**篡改字节后签名摘要失效不会被发现**；同一证书签名的任意其他文件也会通过 | 统一到一个 `AuthenticodeVerifier`：`WinVerifyTrust`（fail-closed）+ 指纹（SHA-256，非 SHA-1）+ **强制内容 SHA-256 白名单**（来自 Release API 的 asset 名+大小+SHA-256 三元组）。下载路径与本地路径**共用同一个校验器** |
| **H-2** | **官方源上指纹 pin 完全失效** | `ModFetcher.cs:857-868` | 指纹不符时，非官方源拒绝、**官方源仅记警告后放行**。而 `Subject.Contains("DLSSG")` 可被任意自签证书满足 | 官方源也应 fail-closed；把"上游换证书"处理成**显式的人机确认流程**（或从 Release 元数据读取期望指纹），不要静默放行 |
| **H-3** | **指纹常量无人校验，且测试只测"可读取"** | `ModFetcher.cs:58`（值 `85BA6676…`）；`test/Harness/Program.cs:2270-2274` | 证书轮换/上游换构建后，所有镜像源下载会失败，而**测试全绿**。仓库内无 mod 二进制（实测 `mod/` 不存在），本快照**无法确认**常量与真实文件是否一致 `[未确认]` | 指纹进配置/Release 元数据而非编译常量；测试**断言相等**，并把它作为发布门禁的一环 |
| **H-4** | **恢复回填不校验记录的 SHA-256，且 `StoredPath`/`FileName` 未受约束** | `DeploymentService.cs:705-720`（哈希字段 `Models.cs:125`、`:133` 被记录但从不在此处读取）；`Path.Combine(game.RenderDir, b.FileName)` `:715` | ① 备份文件被替换/损坏 → 任意内容被覆盖回游戏目录；② `library.json` 可被改造成"从任意路径拉文件写入游戏目录"；③ `b.FileName` 含 `..` 时可逃逸出渲染目录 | 回填前**必须**校验 `Sha256(StoredPath) == b.Sha256 && Size == b.Size`，不一致即拒绝并报错；`StoredPath` 必须校验位于 `AppPaths.RestoreRoot` 之下；`FileName` 必须过 `Path.GetFileName` + 前缀校验 |

### 中

| # | 缺陷 | 位置 | 说明 | 修复建议 |
|---|---|---|---|---|
| **M-1** | **部署非事务**：无 Stage/Validate/Rollback，DLL 与 INI 分两步就地写入 | `DeploymentService.cs:488-494`、`:577-588` | 第二步失败留下半成品（只有 DLL 或只有 INI），且不回滚第一步。`Evaluate` 会把它判成 `Missing`/`Modified`，用户看到的是"部署失败"但目录已被改 | 引入 `InstallPlan`（列出全部目标路径+内容+哈希）→ 全部写入 `*.dlssgtmp` → 逐项校验 → 原子 `Move` 替换 → 失败逐项回滚。计划本身持久化（对应任务书的 `VerifiedInstallPlan`） |
| **M-2** | **记录落盘与文件写入不同步** | `DeploymentService.cs:511-544`（内存）vs `MainWindow.Actions.cs:73`（落盘） | 服务层不负责持久化，崩溃窗口内会出现"磁盘已改、记录未写"→ 文件变成无主，`Restore` 只能靠签名启发式清理 | 服务层在事务提交点**自己**持久化（`Save` 成功才算 Commit），UI 只读取结果 |
| **M-3** | **部署时不验证待写入字节** | `DeploymentService.cs:377-387` | 只验证 `source.IsValid`（"有 INI + 至少一个 DLL"，`ModSource.cs:88`）。mod 目录内的 DLL 若被本地替换（无签名/异签名），会被原样部署进游戏 | 部署前对源 DLL 做与下载同一套校验（`WinVerifyTrust` + 指纹 + 哈希），并对**手装/导入**的入口提供显式的"我确认此文件"确认 |
| **M-4** | **无版本比较、无"已是最新"短路** | 全仓 `System.Version`/`CompareTo` 零命中 | 用户每次点更新都会重下 ~101 MB；`TryWriteVersionMarker(versionLabel ?? "")` 在探测失败时**写入空标记**，抹掉已知版本号（`ModFetcher.cs:662`） | 建立版本模型与比较器；"检查"与"下载"分离；探测失败时**保留**旧标记而不是写空 |
| **M-5** | **代理例外使 IP 校验可被绕过** | `ModFetcher.cs:346-351`、`:365-378` | 系统配置代理时整体跳过"解析地址必须全部公网"的校验。DNS 污染/内网劫持场景下防线失效 | 代理场景下改为校验**代理地址**而非目标地址；或要求 HTTPS + 证书固定（PIN）并记录例外 |
| **M-6** | **Release Asset 下载路径尚不存在，白名单会挡掉 302 目标** | `ModFetcher.cs:34-46`（无 `objects.githubusercontent.com`）、`:429-460`（逐跳复检） | 新架构按 Release Asset 分发时，`github.com/.../releases/download/...` 会 302 到 `objects.githubusercontent.com`，**当前白名单会直接拒绝** | 更新白名单并在测试中覆盖该域名；`TestUrlPolicy` 需新增用例 |
| **M-7** | **反作弊仅按文件名匹配，无内容/版本判定** | `AntiCheat.cs:78-109`（8 组特征 + 通配符）、`:115`（任意 `*.sys`） | 误报（把用户自己的驱动文件名判为 AC）与漏报（改名后的 AC）都不难触发；`*.sys` 规则使任何在游戏目录放驱动文件的游戏都被标记 | 增加 PE 版本信息/签名者/文件大小特征；引入"确认/忽略"的持久化状态（现状每次都重扫，`DeploymentService.cs:826`） |
| **M-8** | **`PickMainExe` 无 PE 校验，"渲染 EXE"靠大小猜测** | `Detection.cs:261-282` | 只排除 15 个文件名片段后取**最大**的 exe。对多 exe 游戏（启动器 + 渲染器）判定不可靠，且把"渲染目录"的判定权交给一个猜想 | 解析 PE 头（子系统/架构/导入表）+ 版本资源；把 `LauncherExe` 与 `RendererExe` 分离建模 |
| **M-9** | **UI 层承担持久化与事务编排** | `MainWindow.Actions.cs:217-235`、`:73`；`MainWindow.xaml.cs:395-402` | 服务层无法脱离 WPF 使用；同一段代码既在后台线程跑部署、又在 UI 线程写日志弹窗；`_busy` 布尔是全应用互斥原语 | 抽出 `DeploymentCoordinator`/应用服务层，UI 只订阅视图模型 |

### 低

| # | 缺陷 | 位置 | 说明 | 修复建议 |
|---|---|---|---|---|
| L-1 | 解压无符号链接防护、无解压后体积上限 | `ModFetcher.cs:726`、`:63`、`:694-710` | 256 MiB 只约束压缩包字节数；符号链接条目未检查 | 改用逐条目解压并校验 `FullName` 前缀 + 跳过重解析点 + 统计解压后总字节 |
| L-2 | `CopyInto` 前缀校验不解析重解析点 | `ModFetcher.cs:947-959` | junction/symlink 场景下前缀校验通过但实际写到别处 | 写入前解析并比对最终真实路径 |
| L-3 | 无缓存/无 ETag/无断点续传 | `ModFetcher.cs:416-426`、`:633`、`:778` | 每次全量重下；网络抖动即从头开始 | 条件请求 + 文件级缓存 + Range 续传 |
| L-4 | `LooksLikeProjectIni` 的子串分支过宽 | `DeploymentService.cs:748-762` | 注释称"stricter than a bare keyword search"，第二个分支实质就是关键词搜索；引用 `dlssg_sm86` 的第三方 INI 会被误删 | 只保留横幅/结构判据，或要求 INI 内有明确的生成标记（例如管理器写入的 `; Generated by` 行） |
| L-5 | `.manager-version` 无版本格式校验 | `ModFetcher.cs:935-945`、`ModSource.cs:96-113` | 读到什么就显示什么（含空串），无 SemVer 归一化 | 统一版本模型并校验格式，非法值视为未知 |
| L-6 | 全量状态检查成本高：每次 `Evaluate` 都 `WinVerifyTrust` + 全盘哈希，且检查全部 6 个入口名 | `DeploymentService.cs:807-816`、`:817-884`、`:835-837`；`MainWindow.xaml.cs:827-853` | 15 MB × 6 的文件读两遍（签名 + 哈希）；列表刷新在多游戏时线性放大 | 缓存"文件 → (size, mtime, 校验结论)"，仅在属性变化时复检 |
| L-7 | 依赖环（三个模块互相引用） | `ModSource.cs:103`、`:266`；`ModFetcher.cs:206`、`:914`；`DeploymentService.cs:924` | 无法分层测试与独立演进 | 抽出 `Core.Abstractions`（哈希、签名、路径策略），上层依赖抽象 |
| L-8 | 测试项目靠文件白名单复制源码 | `test/Harness/Harness.csproj:18-36` | 新增被引用模块必须手工登记；`MainWindow*` 天然不可测 | 改为项目引用 + 把 UI 逻辑抽到可测的服务层 |
| L-9 | `Detection.DetectRenderDir_Click` 找到目标后仍用原 root 走后续流程 | `MainWindow.Actions.cs:476-484` | 记录了"定位到 X"日志，却调用 `AttachFolder(game, root)`（依赖 `ResolveRenderDir` 再解析一次）；结果一致但逻辑绕行，易在重构中出错 | 直接用 `hit.RenderDir` |
| L-10 | 文档与实现多处矛盾（见第 8 节） | — | 尤其**指纹常量**与**下载源顺序**两处会直接误导使用者 | 见第 8 节 |

---

## 8. 文档与实现不符之处

每条给出 `文件:行号 ×2`（文档侧 × 代码侧）。

| # | 文档说 | 代码做 | 文档位置 | 代码位置 | 严重度 |
|---|---|---|---|---|---|
| **D-1** | 证书指纹记录值为 `A994735E6A7E9AA31FA926B3023B7C487DAB4850`（"Native 0.2.4 的五个 DLL 共用"） | 常量是 `85BA66762F851E49148D706915D09026281418E6`，注释明确说 `A994735E…` 是 0.2.x 的旧值、已不再属于 payload | `docs/mod-files.md:90` | `src/DLSSGManager/ModFetcher.cs:58`（注释 `:48-57`） | **高**：文档给的是**已失效的 pin**，照此核对会误判全部下载为"合规"或"不合规" |
| **D-2** | 下载源顺序为 `codeload → GitHub API → raw → jsDelivr CDN` | 实际顺序 `raw → gh-proxy → jsDelivr → ghfast → codeload → zipball`（逐文件优先于整包） | `docs/mod-files.md:44` | `src/DLSSGManager/ModFetcher.cs:214-250` | **中**：误导排查方向（用户以为先走 codeload） |
| **D-3** | 允许域名只有 5 个：`github.com`、`codeload.github.com`、`raw.githubusercontent.com`、`api.github.com`、`cdn.jsdelivr.net` | 白名单共 7 个，另含 `gh-proxy.com`、`ghfast.top` | `docs/mod-files.md:77` | `src/DLSSGManager/ModFetcher.cs:34-46` | 中：安全审计文档漏报两个第三方域名 |
| **D-4** | mod 目录含 `config/presets/` 两档预设，且 `altnative/` 下有 `winhttp.dll` | `Payload` 只下载 9 项，**不含 presets**；`winhttp.dll` 已被 0.3.0 移除并被 `PruneSupersededEntries` 主动清理 | `docs/mod-files.md:9-16` | `src/DLSSGManager/ModFetcher.cs:84-95`、`:903-926` | 中 |
| **D-5** | "1. **必须存在**：`version.dll`、`dlssg_sm86.ini` 与 `altnative/` 下的**四个**备用入口" | 必选备用入口是**五个**（winmm、dinput8、dbghelp、dxgi、d3d12） | `docs/mod-files.md:88` | `src/DLSSGManager/ModFetcher.cs:88-92`、`ModSource.cs:22-23` | 低 |
| **D-6** | "哈希固定在管理器源码里（`ModFetcher.Extras`）"；"把 `SelfRef` 常量指向本次发布的 tag" | 不存在 `Extras` 类型也不存在 `SelfRef` 常量；哈希在 `KnownCommunityBuildHashes`，仓库引用在 `RepoPath`/`RepoRef`（**且是分支 `main`，不是 tag**） | `extra-proxies/README.md:26`、`:43`（`:42`） | `src/DLSSGManager/ModFetcher.cs:106-109`、`:65-66` | 中：文档描述的更新流程无法执行 |
| **D-7** | "安装时可直接下载 Mod 文件（勾选「Mod 文件」选项组）——此时安装程序已提权" | 安装器**不下载**：只有一条 `Filename: …{AppShortName}.exe; Description: {cm:LaunchAfterInstall}; Flags: nowait postinstall skipifsilent`，**无 `--fetch` 参数、无联网步骤** | `.github/workflows/release.yml:128-132` | `installer/setup.iss:98`（全文无 `--fetch`）、`setup.iss:7` 注释"不随安装包分发"、`README.md:26`"安装过程也不联网"、`docs/mod-files.md:42`"安装阶段**不下载**" | **中**：Release 正文与实际行为矛盾 |
| **D-8** | mod 文件体积"约 **101 MB**" | 安装器文案、Release 正文、下载器注释三处都说"约 **75 MB**" | `README.md:26`、`CONTRIBUTING.md:21`、`README.en.md:26` | `installer/setup.iss:7`、`:77`、`:82`、`:187`；`.github/workflows/release.yml:144`；`ModFetcher.cs:35`、`:222`；`test/Harness/Program.cs:111`、`:303` | 低 |
| **D-9** | "**恢复**只删签名和哈希都对得上的文件" | 签名路径**不含指纹比对**（`IsProjectSigned` 只查主题含 `DLSSG`）；INI 还有"横幅启发式"分支（不满足"签名和哈希都对得上"即删） | `README.md:41`、`README.en.md:41`、`.github/workflows/release.yml:147-148` | `DeploymentService.cs:54-66`（无 Thumbprint）、`:664-683`（byBanner 分支） | 中：用户对安全属性的预期高于实现 |
| **D-10** | "**1. 必须签名**：每个 DLL 都要带项目证书的有效 Authenticode 签名" | `Verify` 用 `CreateFromSignedFile`（不校验签名有效性）；正确实现 `IsSignatureIntact` 存在于另一模块但**未被调用** | `docs/mod-files.md:89`、`ModFetcher.cs:22-25`（模块注释） | `ModFetcher.cs:836`、`:846-850` | **高**：与 H-1 同一根因，文档 + 代码注释双重高估了校验强度 |
| **D-11** | "记录值为 `A994735E…`（Native 0.2.4 的五个 DLL 共用）" 与 "0.3.0 发布 6 个入口" 并存，未说明 0.2.x 的四个备用入口（含 `winhttp`）与 0.3.0 的五个不同 | 代码里两套清单都存在：`ProxyCandidates`（6 个候选，含 d3d12）与 `KnownProxyNames`（+winhttp 的扫描集） | `docs/mod-files.md:21`、`:88-90` | `ModSource.cs:22-35` | 低（但会误导入口名相关排查） |
| **D-12** | `docs/mod-files.md:44` 描述"点工具条上的「**从 GitHub 更新 Mod 文件**」" | 实际按钮文案由字符串表决定（`Fetch.*` / `UpdateMod`），且现在会先弹出 `SourcePickerDialog`（选源对话框）才下载 | `docs/mod-files.md:44` | `MainWindow.xaml.cs:507-519`（`picker.ShowDialog()`）、`Strings.zh.cs` 对应键 | 低 |

**反向印证**：`CONTRIBUTING.md:5` 明确写"本项目的代码、文案和文档由 AI 生成，维护者负责真机实测与发布"，`README.md:11` 同。上表的文档漂移（尤其 D-1/D-10）正是这类项目的典型失效模式——**文档描述的是"设计意图"，代码演化后没有回写文档**。

---

## 9. 可直接复用 / 必须重写 / 应删除

### 9.1 可直接复用（提取为独立组件，几乎不用改逻辑）

| 资产 | 位置 | 复用理由 |
|---|---|---|
| **`WinVerifyTrust` 完整实现** | `DeploymentService.cs:95-213` | fail-closed 语义正确、`CERT_E_UNTRUSTEDROOT` 与 `TRUST_E_*` 处理到位、正确 `CLOSE` 状态句柄、非 PE/空文件不抛异常。**这是全仓库质量最高的安全代码**，应提取为 `AuthenticodeVerifier` 并**成为唯一签名入口**（修 H-1） |
| **URL 策略与 IP 段黑名单** | `ModFetcher.cs:333-414` | IPv4/IPv6 私网段覆盖完整；重定向逐跳复检（`:429-460`）。需补：Release asset 域名、代理例外收紧（M-5/M-6） |
| **`PathGuard`** | `Shell.cs:14-108` | 控制字符/引号/绝对路径/长度/存在性校验，配合 `ShellExecuteExW` 单路径无参数调用（`Shell.cs:151-178`），无命令注入面 |
| **`Detection` 的 Steam 库解析** | `Detection.cs:43-125` | VDF/ACF 正则解析虽朴素但可用、无外部依赖。**可直接复用于新 Provider 的 Steam 部分** |
| **`AntiCheat` 特征表与扫描框架** | `AntiCheat.cs:78-153` | 8 组厂商特征 + 文件夹/文件统一匹配 + 向上 3 层的祖先扫描 + 卷根排除（`:124-127`、`:155-167`）。特征表需要扩，框架可留 |
| **`IniTemplate` 键值渲染** | `Store.cs:184-248` | "只改写模板里存在的键"这一策略正确且必要（跨 schema 兼容），可直接复用并扩展为通用 INI 写入器 |
| **`Gpu` 的架构判定与 INF 间接串解析** | `Gpu.cs:105-168`（PCI ID → 架构）、`:288-348`（`@oemXX.inf,%token%;fallback` 解析） | 判定以**硬件 ID 为准、名称仅做交叉校验**（`:154-168`）是正确取舍；INF `[Strings]` 解析对"恢复真实显卡名"必不可少 |
| **`LibraryStore` 的原子写入** | `Store.cs:105-123` | `写 .tmp → File.Move(overwrite)` 是标准做法；`Normalize`（`:125-148`）提供了旧库迁移样板 |
| **测试夹具的骨架** | `test/Harness/Program.cs:20-105`、`:267-298`、`:305-346` | `AppPaths.RootOverrideVariable` 隔离数据目录（`:30`）、`Check/Section/SkipWithoutModFiles` 三件套、`MakeSyntheticModSource`/`MakeGameDir` 合成夹具——这套模式**可以且应该沿用**（详见第 10 节） |
| **本地化运行时切换机制** | `Localization.cs:27-53`、`TrExtension.cs` | 通过 `PropertyChanged("Item[]")` 一次性刷新全部 XAML 绑定，设计巧妙；`Loc.T` 的三段回退（当前语言 → 英文 → 键名）不会静默空白 |

### 9.2 必须重写

| 资产 | 位置 | 为什么必须重写 |
|---|---|---|
| **整个更新/版本系统** | `ModFetcher.cs:152-176`（探测）、`:571-616`（下载编排）、`:935-945`（版本标记） | 无版本模型、无比较、无 Release/Asset 概念、无 Provider 抽象、无缓存/条件请求。任务书要求的"最高优先级功能"在当前结构里**没有落点**，只能新建 |
| **`ModFetcher.Verify`（校验流程）** | `ModFetcher.cs:817-871` | 必须重写为：`WinVerifyTrust`（复用 9.1）+ SHA-256 内容校验（对 Release asset 的 `size`/`digest`）+ 指纹比对（SHA-256，且官方源也 fail-closed）+ 逐文件结果聚合 |
| **部署事务** | `DeploymentService.cs:373-591`、`:609-742` | 需要 `Stage → Verify → Backup → Install → Validate → Commit → Rollback` 全套；现状只有"检查 + 就地写"。**建议重写为 `InstallPlan` 驱动**（对应 `VerifiedInstallPlan` 字段要求） |
| **恢复路径** | `DeploymentService.cs:705-720` | 必须补哈希校验与路径约束（H-4）；现有实现是"信任 JSON 里的任意路径" |
| **`GameEntry` / `GameProfile` 数据模型** | `Models.cs:160-283`、`:41-118` | 缺 9 个必需字段（第 6 节）；`Store`/`LauncherExe`/`GraphicsApi` 等需要成为一等字段；`GameStatus` 需换成 `Requested/Applied/Verified` 状态机 |
| **游戏身份与 API 判定** | `Detection.cs:172-282`、`:261-282` | 需要 PE 解析、DX11/DX12/Vulkan 判定、Engine 识别、商店 Provider 化（Steam 之外的覆盖） |
| **UI 中的编排层** | `MainWindow.Actions.cs:39-306`、`:385-444`；`MainWindow.xaml.cs:308-356`、`:462-492` | 必须抽成服务层才能测试与复用；`_busy` 布尔互斥也应换成显式的作业队列 |
| **测试项目的编译方式** | `test/Harness/Harness.csproj:18-36` | 文件白名单不可持续；改为项目引用 + 独立测试项目 |
| **`ModSource` 的可用入口合成** | `ModSource.cs:61`、`:75-88` | `AvailableProxies` 把"自带入口"与"用户导入"混成一个列表，且导入项**不做任何校验**（`:216-218` 自述）。新架构需要区分"受信任（有签名/哈希）"与"用户自担风险"两类，并要求显式确认 |

### 9.3 应删除

| 资产 | 位置 | 理由 |
|---|---|---|
| `Optimized`（`bool?`）遗留字段 | `Models.cs:69`、`Store.cs:134-138` | 0.3.3 已用 `OptimizedTier` 取代；迁移逻辑本身可留，但**字段与新库格式应分离**，不要继续写进同一模型 |
| `winhttp.dll` 相关兼容路径 | `ModSource.cs:26-35`、`ModFetcher.cs:903-926` | 0.3.0 已移除该入口，清理逻辑的存在只为历史遗留。新架构**不做向后兼容到 0.2.x**时可直接删除（`docs/mod-files.md:21` 也确认会被清理） |
| `repo` 内的 `extra-proxies/d3d12.dll`（10 MB 二进制） | `extra-proxies/d3d12.dll` | 授权状态明确（`extra-proxies/README.md:28-32`"不声明任何授权"、`THIRD_PARTY_NOTICES.txt:4-11`"不在本项目授权范围"）。**本项目的许可证策略不允许随仓库分发第三方二进制**，且上游已自带 d3d12 入口。保留的用途（识别旧构建）只需那个 SHA-256 常量，不需要文件本身 |
| `Sources` 中的整分支 zip 回退（`codeload`/`zipball`） | `ModFetcher.cs:243-249` | 0.3.3 起整包 ~470 MB（`ModFetcher.cs:219-223` 自述）。新架构按 Release Asset 分发后，这两个源既低效又扩大了校验面，应删除或降级为"最后手段 + 显式提示" |
| `--scan` 里的硬编码 `"version.dll"` 探测 | `test/Harness/Program.cs:255` | `AntiCheat.FindQuarantinedCopies(g.RenderDir, "version.dll", null)` 在扫描模式下写死入口名，对导入的自定义入口无效；属于测试代码的过时假设 |
| `PathGuard` 之外的 `ModSourceLocator.CandidateFolders` 中的 repo 搜索 | `ModSourceLocator.cs:128-139`、`:145-159` | "向上 6 层找仓库根"是为 `dotnet run` 场景服务的开发期便利，**发行版不应具备"向上搜索并下载到无关父目录"的行为**（`:22` 用 `.git`/`*.sln` 作标记，工业环境可能误命中） |

---

## 10. 测试夹具

### 10.1 规模与组织方式

- `test/Harness/Program.cs` = **2297 行**（实测；任务书标 1927 行）。
- 组织方式：**手写断言 + 分节标题**，无测试框架依赖。

```csharp
private static void Check(string name, bool condition, string? detail = null);   // :267-279
private static void Section(string title);                                        // :294-298
private static bool SkipWithoutModFiles(string section);                          // :285-292
```

- 入口 `Main`（`:20-105`）顺序调用 **24 个 `Test*` 方法**（`:64-88`），每个方法内部先 `Section(...)` 再逐条 `Check(...)`；末尾打印 `===== 通过 N · 失败 M · 跳过 K =====` 并以退出码表达结果（`:104`）。
- **三个非测试模式**（`:33-42`）：`--scan`（只读真实机扫描，`:215-263`）、`--fetch`（跑真实下载器，`:159-211`）、`--sources`（列出源并校验地址策略，`:113-148`）。
- **测试段分组**（24 段，按 `Section` 标题）：

| # | 分组 | 方法 | 行号范围 |
|---|---|---|---|
| 1 | Mod 文件源识别 | `TestModSource` | `:350-375` |
| 2 | INI 渲染 | `TestIniRendering` | `:377-434` |
| 3 | 界面多语言 | `TestLocalization` | `:436-569` |
| 4 | 版本号 | `TestVersionLabel` | `:571-581` |
| 5 | 下载进度与暂停 | `TestDownloadProgress` | `:583-624` |
| 6 | 显卡探测 | `TestGpuProbe` | `:626-803` |
| 7 | 部署 → 恢复（干净目录） | `TestDeployRestore` | `:805-851` |
| 8 | 保护其他 Mod 的文件（不得误删） | `TestForeignFileProtection` | `:853-920` |
| 9 | 五个入口名全被占用 | `TestProxyOccupation` | `:922-951` |
| 10 | 添加代理 DLL（本地入口） | `TestProxyImport` | `:953-1061` |
| 11 | 游戏目录只应存在一个本项目代理 | `TestSingleProxyInvariant` | `:1063-1142` |
| 12 | 自定义命名代理（导入 DLL 全周期） | `TestCustomNamedProxy` | `:1144-1224` |
| 13 | 接管手工安装 | `TestAdopt` | `:1226-1262` |
| 14 | 游戏探测 | `TestDetection` | `:1264-1312` |
| 15 | Mod 文件源定位 | `TestModSourceLocator` | `:1314-1445` |
| 16 | Shell 路径校验 | `TestPathGuard` | `:1447-1506` |
| 17 | 反作弊检测与隔离清理 | `TestAntiCheat` | `:1508-1708` |
| 18 | 库文件持久化 | `TestPersistence` | `:1710-1802` |
| 19 | 界面主题 | `TestThemes` | `:1804-2001` |
| 20 | 下载源选择 | `TestSourceSelection` | `:2003-2059` |
| 21 | 下载 URL 策略 | `TestUrlPolicy` | `:2061-2107` |
| 22 | 社区构建识别（按哈希） | `TestCommunityBuildRecognition` | `:2109-2169` |
| 23 | 识别手工安装的 d3d12.dll | `TestHandInstalledExtra` | `:2171-2229` |
| 24 | 下载内容签名校验 | `TestSignatureVerification` | `:2242-2275` |

### 10.2 覆盖到的场景（做得好的部分）

- **数据隔离**：`Environment.SetEnvironmentVariable(AppPaths.RootOverrideVariable, …)` 在任何读取之前设置（`:30`），把 `library.json`/日志/备份全部重定向到临时目录，绝不污染真实数据。**这是最值得继承的设计。**
- **合成夹具**：`MakeSyntheticModSource`（`:305-324`）造一个"形状正确但文件是随机字节"的 mod 源，让不关心真实二进制的用例（反作弊门禁、入口占用）能跑；`MakeGameDir`（`:333-346`）造"大 exe + 标记 DLL"的假游戏目录。
- **异步门控**：`DownloadGate` 的行为被单独测（`:583-624`，暂停/恢复/取消打断）。
- **安全属性**：`TestUrlPolicy`（`:2061-2107`）+ `--sources`（`:113-148`）覆盖"HTTP 拒绝、非白名单域拒绝、环回/内网拒绝"。
- **数据不丢**：`TestForeignFileProtection`（`:853-920`）专门验证"别的 mod 的 `dxgi.dll` 不被覆盖/删除/改内容"，并验证重新部署后备份记录结转（`:906-919`）。
- **签名负例**：`TestSignatureVerification` 有篡改字节 → 断言 `IsProjectSigned == false`（`:2257-2261`），非 PE（`:2264-2266`）、空文件（`:2267`）、不存在文件（`:2268`）都不抛异常。

### 10.3 系统性盲区

| # | 盲区 | 证据 | 后果 |
|---|---|---|---|
| **B-1** | **下载校验路径完全未被测**：`ModFetcher.Verify`（`ModFetcher.cs:817-871`）是 `private`，测试只测了 `DeploymentService.IsProjectSigned`（另一条实现） | `Program.cs:2248-2274` 全部调用 `IsProjectSigned`；`Verify` 的唯一调用点是 `ModFetcher.cs:646`（生产路径） | **H-1 因此长期不可见**：被篡改的下载文件永远走不到断言里 |
| **B-2** | **指纹 pin 未断言**：只打印 + 断言"可读取" | `Program.cs:2270-2274`（注释声称 "must match"，代码 `Check("证书指纹可读取", thumb is not null, …)`） | 证书轮换不会被测试发现（H-3） |
| **B-3** | **无"版本比较"用例**（因为功能不存在） | 无对应 `Section` | 新架构的更新检查必须从零建测试 |
| **B-4** | **无"失败回滚"用例**：没有模拟"写入第二个文件时磁盘满/文件被锁" | 部署类的用例都在理想路径上（`:805-851`、`:1063-1142`） | M-1 的非事务性不可见 |
| **B-5** | **无恢复时校验备份哈希的用例**：没有"备份文件被替换/损坏后恢复" | `:1226-1262`（接管+恢复）只跑正常路径 | H-4 不可见 |
| **B-6** | **无路径逃逸用例**：`library.json` 里塞 `..\` 的 `FileName`/`StoredPath` 无人测 | `TestPersistence`（`:1710-1802`）只测正常往返与迁移 | H-4 的第二、三个子问题不可见 |
| **B-7** | **未覆盖 codeload/zipball 整包路径**：`FetchArchiveAsync`（`ModFetcher.cs:685-746`）、`PruneSupersededEntries`（`:903-926`）、`Publish`（`:879-893`）无单测 | 无对应 `Section`；`--fetch` 默认走 `AutoSourceId`（先逐文件源） | 整包路径的扁平化、逃逸防护、旧入口清理全靠人工验证 |
| **B-8** | **反作弊特征表未被穷举**：8 组特征只测了部分代表项 | `:1508-1708` | 特征表扩充无回归保护 |
| **B-9** | **`Evaluate` 的状态矩阵不完整**：未覆盖"记录存在但目录不存在""两个入口并存 + 其中一个被改"等组合 | `:805-851`、`:1063-1142` | 状态机回归无保护 |
| **B-10** | **UI 层零覆盖**：`MainWindow*`（1518 行）不在 `Harness.csproj` 的编译列表内 | `Harness.csproj:18-36`（无 `MainWindow*.cs`） | 编排逻辑改动无测试护栏 |
| **B-11** | **假阳性断言**：部分 `Check` 恒为真 | `Program.cs:718`：`Check("HAGS 状态读取不抛异常", true)` ← 条件写死 `true` | 该行**不测任何东西**，是无效断言 |
| **B-12** | **测试依赖"本机是否已下载 mod"**：无 mod 文件时 6 个段整体跳过 | `:49`、`:55-60`、`:285-292` | CI 上装包测试可能大规模跳过而"看起来全绿"（工作流注释 `release.yml:32` 也承认这一点） |
| **B-13** | **真实机扫描结果无断言**：`--scan` 只打印 | `:215-263` | 真机兼容数据不进回归 |

### 10.4 新架构可否复用这个模式

**可以，但必须改造三点。**

**可复用的部分（建议保留）**：

1. **数据隔离样板**：`AppPaths.RootOverrideVariable` + 在 `Main` 最开始设置（`Program.cs:30`）。新架构应保留"环境变量覆盖数据根"这一能力（也是 `--fetch` / 无人值守安装的前提，`App.xaml.cs:53-96`）。
2. **三件套断言 API**：`Check/Section/SkipWithoutModFiles`。语义清晰、输出可读、退出码可被 CI 直接消费（`:104`）。
3. **合成夹具**：`MakeGameDir`（假游戏目录）与 `MakeSyntheticModSource`（假 mod 源）的思路——**新架构甚至应该反过来**：把"假二进制带真签名"作为标准夹具（见下），而不是"随机字节"。
4. **非测试模式的价值**：`--scan`（只读真机体检）、`--sources`（地址策略公示）——这两个模式应保留并强化，因为新架构的最大风险恰好在真机与网络策略上。

**必须改造的部分**：

1. **编译方式**：丢掉文件白名单（`Harness.csproj:18-36`），改为对 **Core / Deployment / Fetch 三个库项目**的 `ProjectReference`；`MainWindow*` 不进测试项目，UI 逻辑抽到可测服务层。这是解决 B-10 的唯一途径。
2. **补上被跳过的关键路径**：至少新增
   - `Fetch` 校验路径（把 `Verify` 提为可测的 `IPayloadVerifier`，直接对 staging 目录调用，绕过网络，覆盖 B-1）；
   - 指纹断言（B-2）；
   - 版本比较表驱动用例（B-3）；
   - **故障注入**：写第二个文件时抛异常的 `IFileSystem` 假实现（B-4），备份文件被篡改（B-5），`library.json` 注入 `..\`（B-6）；
   - codeload 整包路径的**离线**用例：用本地构造的 zip 走 `FetchArchiveAsync` 的解析与扁平化（B-7）。
3. **测试夹具升级为"真签名二进制"**：新架构应自带一套**用测试专用自签证书签名的极小 PE**，让"签名有效 / 签名被篡改 / 指纹不符 / 指纹相同但内容不同"四种情形都能被断言。现有夹具用随机字节（`Program.cs:320`），只能测"不是签名文件"，无法测"是签名文件但不是我们要的那一个"——**而这正是 H-1/H-2 的核心场景**。

**结论**：模式（手写断言 + 分节 + 环境隔离 + 合成夹具）**可复用**；但其"覆盖范围"和"编译方式"**必须重做**，否则同样的盲区会原样继承。

---

## 对重构的 5 条最重要结论

**1. 更新系统在当前代码里没有可复用的落点——它必须从零建，且必须带内容级校验。**
现无版本模型、无比较逻辑、无 Release/Asset 概念、无条件请求（`ModFetcher.cs:152-176`、`:571-616`；`System.Version` 全仓零命中）。新的更新系统必须以 `(版本号, 修复限定词, published_at)` 三元组为内部模型、以 `SHA-256` 为内容身份，并提供**受信任下载（Release asset 强制 digest 校验）× 用户自担风险（本地文件/社区构建）**双分发语义。当前实现里唯一可作为不变量继承的是：**"验证通过前不写目标目录"**（`ModFetcher.cs:644-654` 的 staging 模式）。

**2. 签名校验链是断裂的，且断裂点是文档与代码注释都声称"已覆盖"的地方——这是重构的第一优先级。**
`DeploymentService` 有一份高质量、fail-closed 的 `WinVerifyTrust` 实现（`DeploymentService.cs:95-213`），但**下载路径根本没用它**：`ModFetcher.Verify` 只做 `CreateFromSignedFile` + 主题片段 + 指纹（`ModFetcher.cs:836`、`:846-850`），不校验摘要；且官方源上指纹比对失败也放行（`:857-868`）。必须收敛为**唯一校验器**：`WinVerifyTrust`（fail-closed）+ SHA-256 指纹 + Release 元数据 digest，且三处调用点（下载 / 部署前 / 所有权判定）全部走它。同时把指纹从编译常量移入可验证的配置/元数据，并让测试**断言相等**而不是"可读取"（`Program.cs:2270-2274`）。

**3. 部署不是事务，恢复会盲信 JSON 里的路径与未校验的备份——两者都是"会写别人游戏目录"的工具不能接受的状态。**
`Deploy` 是"前置检查 + 就地写 + 记记录"，无 Stage/Validate/Rollback，DLL 与 INI 分两步非原子写入（`DeploymentService.cs:488-494`、`:577-588`）；`Restore` 回填备份时**不校验 `BackupItem.Sha256`**（字段存在于 `Models.cs:125`，恢复路径 `:705-720` 从不读取），且 `Path.Combine(game.RenderDir, b.FileName)` 与 `b.StoredPath` 都未受约束（`:715`、`:596`）。重构必须引入 `VerifiedInstallPlan`（计划 → 校验 → 执行 → 验证）与"回填前校验哈希 + 路径必须位于 `RestoreRoot` 之下"的硬门禁。

**4. 数据模型与状态机与目标形态不同构，不是加字段能补上的。**
`GameEntry` 缺 `Store`/`LauncherExe`/`RendererExe`/`GraphicsApi`/`Engine`/`Architecture`/`InstalledPatches`/`VerifiedInstallPlan`（`Models.cs:160-283`）；`GameStatus` 的 5 个值是"文件一致性"语义，无法映射到任务书要求的 `Requested → Applied → Verified`"可验证生效"语义（`Models.cs:26-38`）。同时**全仓没有任何 DX11/DX12/Vulkan 判定**（零命中），"渲染 EXE"仅靠文件名黑名单 + 体积排序猜测（`Detection.cs:261-282`），商店覆盖只有 Steam（`Detection.cs:43-119`）。这些都要新建：PE 解析 + API 判定 + 引擎识别 + Provider 化的商店发现。

**5. 可复用的是"底层原语 + 测试模式"，不能复用的是"编排层与 UI 层"。**
值得整体提取的原语：`WinVerifyTrust` 校验器、URL/IP 策略、`PathGuard`+`ShellExecuteExW`、Steam 库解析、`IniTemplate` 的"只改存在的键"策略、`Gpu` 的硬件 ID 判定与 INF `[Strings]` 解析、`LibraryStore` 的原子写。必须重写的是：更新/版本系统、部署事务与恢复、`GameEntry` 模型、游戏身份判定，以及塞了 1518 行（19.9% of 7620）业务编排的 `MainWindow*`。测试方面，`test/Harness/Program.cs` 的**数据隔离 + 分节断言 + 合成夹具**模式可继承，但它现在有 13 个系统性盲区——其中"下载校验路径完全未被测"（`Harness.csproj:18-36` 不含 `MainWindow*`，且 `Verify` 是 private）与"指纹只打印不断言"这两条，正是 H-1/H-3 能长期存活的原因。新测试体系必须：改为项目引用、新增签名/指纹/版本/回滚/路径逃逸用例，并用**带真签名的极小 PE 夹具**取代随机字节（`Program.cs:320`）。

---

## 附录 A：本次审计未确认事项

| # | 事项 | 为什么未确认 | 建议确认方式 |
|---|---|---|---|
| U-1 | 真实 mod 二进制的证书指纹是否等于 `85BA6676…` | 仓库内无 `mod/` 目录，任何 `mod/` 均不存在（实测 `Test-Path` 为 `False`），本地无上游文件 | 在有 mod 文件的机器上跑 `Harness.exe`（会打印实际指纹，`Program.cs:2273`），或 `Get-AuthenticodeSignature` |
| U-2 | `KnownCommunityBuildHashes` 的 `65E6F9…` 是否与 `extra-proxies/d3d12.dll` 一致 | 审计遵守"不修改 `_research/`"，未对 10 MB 二进制做哈希（只读计算可行但未执行） | `Get-FileHash extra-proxies/d3d12.dll`，或读 `THIRD_PARTY_NOTICES.txt:11` 交叉核对（文档声称一致） |
| U-3 | `Publish(staging, destination)` 中 `CopyInto` 的一次真实逃逸行为 | 需要构造恶意 staging 才能触发，本次为静态审计 | 单元测试（见 B-7） |
| U-4 | `Detection.FriendlyName` 对真实游戏目录的命名效果 | 需要真实的多层目录样本 | `--scan` 模式的实测输出（`Program.cs:215-263`） |
| U-5 | 各下载源当前可用性 | 本次审计禁止联网（配额已耗尽） | `Harness.exe --sources`（`:113-148`，只校验地址策略不下载） |

## 附录 B：证据索引（本文引用的一手位置）

| 主题 | 关键位置 |
|---|---|
| 下载源表 | `src/DLSSGManager/ModFetcher.cs:214-250` |
| Payload 清单 | `src/DLSSGManager/ModFetcher.cs:84-95` |
| 指纹常量 | `src/DLSSGManager/ModFetcher.cs:58` |
| 有缺陷的校验 | `src/DLSSGManager/ModFetcher.cs:817-871`（调用点 `:646`） |
| 正确的校验 | `src/DLSSGManager/DeploymentService.cs:95-213`（调用点 `:91`、`:56`） |
| URL/IP 策略 | `src/DLSSGManager/ModFetcher.cs:333-414`；重定向 `:429-460` |
| 解压与逃逸防护 | `src/DLSSGManager/ModFetcher.cs:685-746`、`:947-959` |
| 部署 | `src/DLSSGManager/DeploymentService.cs:373-591` |
| 恢复 | `src/DLSSGManager/DeploymentService.cs:609-742`（盲信点 `:705-720`） |
| 所有权判据 | `src/DLSSGManager/DeploymentService.cs:263-278`、`:287-309`、`:905-925` |
| 状态评估 | `src/DLSSGManager/DeploymentService.cs:817-884` |
| 数据模型 | `src/DLSSGManager/Models.cs:26-38`、`:41-118`、`:160-283` |
| 检测 | `src/DLSSGManager/Detection.cs:43-125`、`:147-170`、`:261-282` |
| 反作弊 | `src/DLSSGManager/AntiCheat.cs:78-109`、`:129-153`、`:250-281` |
| GPU | `src/DLSSGManager/Gpu.cs:105-168`、`:288-348`、`:378-438` |
| 路径/外壳 | `src/DLSSGManager/Shell.cs:14-108`、`:151-230` |
| 持久化/INI | `src/DLSSGManager/Store.cs:36-53`、`:105-148`、`:184-248` |
| UI 编排 | `src/DLSSGManager/MainWindow.Actions.cs:39-144`、`:183-306`、`:385-444`；`MainWindow.xaml.cs:308-356`、`:827-853` |
| 测试夹具 | `test/Harness/Program.cs:20-105`、`:267-346`、`:2242-2275`；`test/Harness/Harness.csproj:18-36` |
| 文档矛盾源 | `docs/mod-files.md:44`、`:77`、`:88-90`；`extra-proxies/README.md:26`、`:42-43`；`.github/workflows/release.yml:128-132`；`installer/setup.iss:7`、`:77`、`:98` |
