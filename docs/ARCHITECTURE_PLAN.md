# ARCHITECTURE_PLAN — RTX 30 Frame Generation Manager

> 任务书：`RTX30_Frame_Generation_Manager_DSH_Task_v2.md` ｜ 阶段：**Phase 0 产出**
> 事实依据：[`docs/RESEARCH_NOTES.md`](RESEARCH_NOTES.md) ｜ 源码审计：[`docs/phase0-manager-audit.md`](phase0-manager-audit.md)
>
> 本文件覆盖任务书 **§11 要求的 20 项内容**，逐项对应。**本阶段不写实现代码。**

---

## 0. 设计原则（先于一切）

| # | 原则 | 落地方式 |
|---|---|---|
| P1 | **更新能力是核心功能，不是附加项** | `UpdateService` 独立成型，UI 与 Provider 均不得内联更新逻辑 |
| P2 | **不凭猜测实现** | 每项能力标注 `[E1]`/`[E2]`/`[E3]` 证据等级；不足则标 `Unknown` / `Experimental` / `Needs Validation` |
| P3 | **RTX 30 Smooth Motion 属社区实验路线，不因官方未支持而放弃**（§10.5 B、§10.7） | 标记 `Undocumented` + `Community Verified` + `Experimental`，**继续实现** |
| P4 | **单一真源** | 安全策略、版本解析规则、兼容性等级各自只有一处定义，文档由代码生成 |
| P5 | **先建 Core，再下沉逻辑** | 原项目 `ModFetcher ↔ DeploymentService ↔ ModSource` 存在循环依赖，拆程序集前必须先抽 Core |
| P6 | **失败必须可回滚，且保留可用的旧版本** | 事务化安装 + 精确恢复 |
| P7 | **绝不打包、镜像或转发上游二进制** | 只做「查询 → 校验 → 用户端下载 → 调用」 |

---

## 1. 当前项目架构分析（§11.1）

### 1.1 现状

原项目 `BUNNY-19C/DLSSG-30s-manager`：C# / WPF / **.NET 8**，MIT，单程序集 `DLSSGManager`，源码约 **9,000 行**（含 848 行字符串表），测试夹具 `test/Harness/Program.cs` 约 **2,300 行**。

| 模块 | 行数规模 | 职责 | 可复用性 |
|---|---|---|---|
| `DeploymentService.cs` | 939 | **唯一写游戏目录的地方**；部署/恢复/接管/所有权判定/WinVerifyTrust | 判据可复用，事务骨架需重建 |
| `ModFetcher.cs` | 970 | 上游 payload 获取、6 源降级、签名校验、版本标记 | 思路可复用，须重构 |
| `Gpu.cs` | 548 | GPU/驱动探测、HAGS、显卡名读写 | **可复用** |
| `MainWindow.xaml.cs` + `.Actions.cs` + `.Protection.cs` | ~1,500 | UI + **业务编排** | **必须重写** |
| `Detection.cs` | ~300 | Steam 扫描、渲染 EXE 启发式 | 可复用，需扩充 |
| `AntiCheat.cs` | ~300 | 反作弊文件名匹配 | 可复用，需扩充 |
| `Models.cs` / `Store.cs` | ~470 | 数据模型与持久化 | **必须重写** |
| `Shell.cs` / `PathGuard` | ~200 | 外壳调用信任边界 | **可复用** |

**依赖关系问题**：`ModFetcher ↔ DeploymentService ↔ ModSource` **三方循环引用**，当前仅因单程序集编译而合法。**一旦按本方案的模块边界拆分程序集，会立即编译失败** —— 这是 §17 阶段划分必须「先建 Core」的直接原因。

### 1.2 与目标架构的差距

| 维度 | 现状 | 目标 |
|---|---|---|
| Provider 抽象 | **无**，逻辑写死在 `ModFetcher` | `IPatchProvider` + 3 个实现 |
| 版本比较 | **无**（全仓库无 `System.Version`/`CompareTo`） | Provider 专属解析器 + 语义化比较 |
| Release API | **未使用** | `IReleaseSource` 统一封装 |
| 运行状态 | 只有文件层 `GameStatus` | `Installed/Loaded/Requested/Applied/Verified` |
| Graphics API | **无判定** | 四级判定链 |
| NVIDIA Profile | **无** | `NvidiaProfileService` |
| 游戏数据库 | 无 | 兼容性数据库（六档等级） |

---

## 2. 已发现问题（§11.2）

> 完整清单与 `文件:行号` 证据见 [`docs/phase0-manager-audit.md`](phase0-manager-audit.md)，此处只列影响架构决策的条目。

| # | 问题 | 等级 | 对架构的要求 |
|---|---|---|---|
| **A1** | **下载路径与部署路径的签名校验强度不一致**：`DeploymentService.IsSignatureIntact` 正确使用 `WinVerifyTrust`（区分 `TRUST_E_BAD_DIGEST` 与自签名），但 `ModFetcher` 侧仅做证书提取 + Subject/Thumbprint 比对 | **高** | 必须收敛为**唯一** `SignatureVerifier`，两条路径共用 |
| **A2** | 无版本比较 → 更新永远手动；无 Release API、无 digest、无缓存条件请求 | 高 | `UpdateService` + `IReleaseSource` + `ReleaseCache` |
| **A3** | 部署缺乏完整事务（只满足 Stage + 部分 Backup） | 高 | 事务化安装器（§13、§14） |
| **A4** | `MainWindow` 承载约 1,500 行业务编排，且测试夹具以 `<Compile Include>` 白名单编译，**UI 层逻辑零覆盖** | 高 | 逻辑必须下沉到可测试的 Core |
| **A5** | `GameEntry` 缺 `GraphicsApi` 等关键字段；`GameStatus` 无运行态 | 中 | 重写数据模型（§5） |
| **A6** | 硬编码 `RepoPath` / `RepoRef = "main"`，上游改布局即静默失效 | 中 | Provider 化 + 结构变化检测（§6.4） |
| **A7** | 承载第三方镜像（`gh-proxy.com` / `ghfast.top`）作为默认降级源 | 中 | 镜像默认关闭；启用时强制完整校验 |
| **A8** | 文档与实现存在多处漂移（证书指纹 `docs/mod-files.md:90` 记的 `A994735E…` 已失效，代码实为 `ModFetcher.cs:58` 的 `85BA6676…`；允许主机列表；payload 体积；签名校验描述），共 **12 处** | 中 | 单一真源 + 由代码生成文档 |
| **A9** | **白名单缺 `objects.githubusercontent.com`**，而 Release Asset 下载会 302 跳到该域 ⇒ 一旦改用 Release Asset 分发**必然被自己的安全策略拒绝** | **高** | 白名单必须补入该域，并补 `TestUrlPolicy` 用例（§8.2、§15.1） |
| **A10** | `Deploy` 中 DLL 与 INI **分两步非原子写**（`DeploymentService.cs:488-494`）；`Restore` 回填时**从不读取 `BackupItem.Sha256`**（字段在 `Models.cs:125`，恢复路径 `:705-720` 未使用），且路径拼接**无逃逸约束** ⇒ 配置驱动的任意文件写入 | **高** | 事务化安装（§13/§14）+ 路径规范化校验（§8.3） |
| **A11** | 版本探测失败时写入**空版本标记**，抹掉已知版本号（`ModFetcher.cs:662`） | 中 | 版本写入必须区分「未知」与「已知为空」 |
| **A12** | 测试夹具存在**恒为真的无效断言**（`test/Harness/Program.cs:718` 的 `Check("...", true)`），且 `Verify` 为 private **完全未被测试**（测试覆盖的是另一实现 `IsProjectSigned`） | 中 | 测试必须断言行为而非存在性；核心校验函数必须可测 |

---

## 3. 新架构设计（§11.3）

### 3.1 分层

```
┌─────────────────────────────────────────────────────────────┐
│ Presentation (WPF)                                          │
│   Dashboard · GameLibrary · GameDetails · Updates           │
│   Downloads · Settings · Diagnostics                        │
│   —— 只做绑定与命令转发，禁止业务编排 ——                     │
├─────────────────────────────────────────────────────────────┤
│ Application (编排层, 可测试, 无 UI 依赖)                     │
│   UpdateService · InstallOrchestrator · GameLibraryService  │
│   DiagnosticService · CompatibilityService                  │
├─────────────────────────────────────────────────────────────┤
│ Core (纯逻辑, 无 IO 副作用可注入)                            │
│   GameDetection · GraphicsApiDetection · InstallPlanner     │
│   Versioning · Security · BackupRestore · NvidiaProfile     │
│   Logging                                                   │
├─────────────────────────────────────────────────────────────┤
│ Providers (可插拔, 仅依赖 Core 接口)                         │
│   DlssgSm86Provider · MfgSmoothProvider                     │
│   ExternalSmoothMotionProvider                              │
├─────────────────────────────────────────────────────────────┤
│ Infrastructure                                              │
│   HttpSource · SignatureVerifier · FileSystem · Storage     │
└─────────────────────────────────────────────────────────────┘
```

**依赖方向严格单向**：`Presentation → Application → Core ← Providers`，`Infrastructure` 被 Core 以接口注入。**Providers 不得引用 Application 或 Presentation。**

### 3.2 与任务书 §12 建议架构的对应

任务书建议的 `Core/Providers/Storage/UI` 结构被完整采纳，仅做两点明确化：
- 把「编排逻辑」（`UpdateService` 等）单列为 **Application 层**，避免它退化成第二个 `MainWindow`；
- `Storage` 下沉为 Infrastructure 的具体实现，接口留在 Core。

---

## 4. 模块边界（§11.4）

每个模块必须可单独测试。**Input / Output / Error states / Rollback / Tests** 五要素在编码前逐模块确定（任务书 §46）。

| 模块 | Input | Output | 主要 Error states |
|---|---|---|---|
| `GameDetection` | 扫描根、商店配置 | `IReadOnlyList<GameInfo>` | `NoLibraryFound`、`AccessDenied` |
| `GraphicsApiDetection` | `GameInfo` | `GraphicsApi`（含 `Unknown`） | `Unknown`（**不得猜**） |
| `InstallPlanner` | `GameInfo` + `Provider` + `Environment` + `CompatibilityData` | `InstallPlan` | `Unsupported`、`UnknownApi`、`ProxyConflict`、`AntiCheat`、`ProviderUnavailable` |
| `Versioning` | tag / name / asset 名 / 时间 | `VersionCandidate` | `Unparseable`、`Ambiguous` |
| `Security` | 文件路径 / URL | `VerificationResult` | `BadDigest`、`UntrustedRoot`、`HostNotAllowed`、`ZipSlip` |
| `BackupRestore` | 目标目录 + 文件清单 | `BackupSet` | `HashMismatch`、`EscapeAttempt` |
| `NvidiaProfile` | profile 名 / 设置对 | `ProfileSnapshot` | `NotPrivileged`、`ProfileMissing`、`DriverUnsupported` |
| `UpdateService` | Provider 集合 + 策略 | `UpdateReport` | `Offline`、`RateLimited`、`StructureChanged` |

---

## 5. 数据结构（§11.5）

### 5.1 `GameInfo`（满足 §15 全部字段）

```
GameInfo
├─ GameName            string
├─ Store               StoreKind      (Steam | Epic | GamePass | Manual | Other)
├─ InstallPath         string
├─ LauncherExe         string?        # 可能是 launcher，不是渲染进程
├─ RendererExe         string?        # 真正渲染的 EXE —— 补丁必须放到它旁边
├─ GraphicsApi         GraphicsApi    (DX11 | DX12 | Vulkan | Unknown)
├─ Engine              string?        # UE / Unity / 自研 …（Unknown 允许）
├─ Architecture        string?        # x64 / x86（32 位标题不支持）
├─ AntiCheat           AntiCheatReport
├─ InstalledPatches    IReadOnlyList<InstalledPatch>
└─ VerifiedInstallPlan InstallPlan?
```

**关键约束**：`LauncherExe` 与 `RendererExe` **必须分开**——任务书 §15 明确要求区分，且证据表明「直启 exe 正常、经 Steam 启动崩溃」是真实故障模式（MFG `#4`）。

### 5.2 `InstallPlan`

```
InstallPlan
├─ ProviderId           string
├─ ReleaseRef           ReleaseRef          # tag + assetId + digest
├─ RendererExe          string
├─ GraphicsApi          GraphicsApi
├─ LoadMode             LoadMode            (DirectProxy | AsiLoader | MultiProxy)
├─ ProxyDll             string?             # version.dll / winmm.dll / …
├─ AsiFile              string?             # new.asi 等（名称须以 Provider 证据为准）
├─ Files                IReadOnlyList<PlannedFile>
├─ NvidiaProfileChanges IReadOnlyList<ProfileChange>
├─ LaunchArguments      string?
└─ Evidence             EvidenceTier        # 该方案的置信来源
```

### 5.3 状态机（满足 §25 五态）

```
NotInstalled → Installed → Loaded → Requested → Applied → Verified
                   │           │          │          │
                   └───────────┴──────────┴──────────┴──→ Failed / Unknown
```

| 状态 | 判定依据 | 数据来源 |
|---|---|---|
| `Installed` | 文件存在且 SHA-256 与记录一致 | 本地哈希 |
| `Loaded` | 目标进程加载了代理 DLL/ASI | 进程模块枚举 + 补丁日志 `runtime_redirect` |
| `Requested` | 补丁日志出现 `install.active=true` | `loader_*.jsonl` |
| `Applied` | `backend_install.status=0` 且出现 `kernel_create`/`evaluate` | `loader_*.jsonl` / `backend_*.jsonl` |
| `Verified` | 上述全部成立**且**与已知良好配置匹配 | 综合判定 |

> ⚠️ **官方判定口径**（上游原文）：*"check both `install.active=true` and the matching `backend_install.status=0` … `image=ptx_sm86` on its own does not mean a kernel was created or executed."*
> **仅文件复制成功永远不等于安装成功**（任务书 §52.9）。

### 5.4 兼容性数据库记录

按 `(GPU, driver, game, API, storefront, launch method)` **六元组**记录，等级六档（参考 `xikarioz/COMPATIBILITY.md`，并叠加任务书 §24 的准入条件）：

| 等级 | 准入条件（任务书 §24） |
|---|---|
| `PROJECT_VALIDATED` | 本项目测试通过 |
| `COMMUNITY_CONFIRMED` | GitHub Issue 有可靠成功验证 + 诊断充分 |
| `COMMUNITY_REPORTED` | 用户报告合理但缺仪表化证据 |
| `EXPERIMENTAL` | 架构兼容、未验证 |
| `UNTESTED` | 无数据 |
| `FAILED` | 报告不工作 |

**单一报告永不得晋升等级**（与 xikarioz 口径一致）。

---

## 6. Provider 设计（§11.6）

### 6.1 统一契约

```csharp
interface IPatchProvider
{
    string Id { get; }
    string DisplayName { get; }
    EvidenceTier Tier { get; }              // 本 Provider 整体的置信等级

    Task<ReleaseInfo?> CheckLatestAsync(bool forceRefresh, CancellationToken ct);
    InstalledVersion GetInstalledVersion(GameInfo game);

    Task<DownloadResult> DownloadAsync(ReleaseInfo release, CancellationToken ct);
    VerificationResult VerifyPackage(string path, ReleaseInfo release);

    InstallPlan BuildInstallPlan(GameInfo game, EnvironmentInfo env);
    Task<InstallResult> InstallAsync(InstallPlan plan, CancellationToken ct);
    Task<RestoreResult> RestoreAsync(GameInfo game, CancellationToken ct);
}
```

### 6.2 **两种分发模型必须同时支持**（本项目最大的架构约束）

调研确证上游存在**两种互不兼容的分发方式**：

| 模型 | 代表 | 事实 | 抽象 |
|---|---|---|---|
| **A. git 树逐文件** | `sdli1995/dlssg_for_sm86` | 7 个 Release **全部 `assets=0`**，二进制只在 git 工作树 | `IGitTreeSource` |
| **B. Release Asset** | MFG / xikarioz | 二进制只通过 Release Asset 分发 | `IReleaseAssetSource` |

```csharp
interface IPayloadSource                     // 两者的共同抽象
{
    Task<ReleaseInfo?> ResolveAsync(bool forceRefresh, CancellationToken ct);
    Task<DownloadResult> FetchAsync(ReleaseInfo release, IPayloadSink sink, CancellationToken ct);
}
```

**模型 A 的下载策略（实测依据）** `[E1]`：

| 优先级 | 路径 | 理由 |
|---|---|---|
| 1 | `raw.githubusercontent.com/{repo}/{ref}/{path}` **逐文件** | 授权源、字节精确、无中间缓存 |
| 2 | `codeload.github.com/{repo}/zip/refs/tags/{tag}` | 限流兜底（**仅在逐文件全失败时**） |

> 之所以**不优先用归档**：整分支 zip 携带 `310.1/` 与 `archive/` 历史产物，体积远大于实际 payload（≈180 MB）；且归档会引入与运行无关的历史 DLL。

### 6.3 三个 Provider 的定位

| Provider | 上游 | 模型 | 加载方式 | 备注 |
|---|---|---|---|---|
| `DlssgSm86Provider` | `sdli1995/dlssg_for_sm86` | A | Direct Proxy（`version.dll` + `dlssg_sm86.ini`，备选入口） | 仅 D3D12；Vulkan 上游已放弃 |
| `MfgSmoothProvider` | MFG 版 | B | Direct Proxy 或 ASI | **激活依赖 NVIDIA Profile**（自动化目标） |
| `ExternalSmoothMotionProvider` | `xikarioz/Smooth-Motion-RTX30` | B | **调用官方安装器**（不打包） | 专有 EULA；只能「链接 + 校验 + 调用」 |

**Provider 的许可证与「是否写 profile」准入规则**（来自交叉验证线）：

| Provider | 许可证 | 可否作可分发 Provider | 是否写 profile |
|---|---|---|---|
| `sdli1995/dlssg_for_sm86` | GPLv3（仅 README 声明，无 LICENSE 文件） | ⚠️ 只读参考（按更保守处理） | ❌ |
| MFG 版 | **无许可证**（保留全部权利） | ❌ **零复用**（且历史报毒记录） | ❌ 依赖 NVPI |
| `xikarioz/Smooth-Motion-RTX30` | **专有 EULA** | ❌ **零复用、禁捆绑、禁静默安装** | ❌ |
| `RankFTW/RHI` | **GPL-3.0** | ⚠️ 只提炼设计，**禁复制代码** | ✅ 是 |
| `ItsAdeline/NVSmooth30` | **MIT** | ✅ 可作**用户显式同意后下载**的可选 Provider（保留 ©2026 ItsAdeline） | ❌ 依赖 NVPI |
| `Aryoksini/DLSS5-Feeder` | **MIT** | ✅ 同上（保留 ©2026 Jean-Laurent ROUZIES） | ❌ 依赖 NVPI |

> **Provider 元数据必须记录**：许可证、**是否写 profile**、是否需要管理员、是否触碰游戏进程。
> ⚠️ **路线 A（profile 写入）与路线 B（二进制适配）必须分开呈现，而不是二选一** —— 社区证据表明 RTX 30 上两者是**组合关系**（见 `RESEARCH_NOTES.md` §2.2 证据 4）。

### 6.4 Release 结构变化检测（任务书 §8）

Provider 每次解析后做**结构断言**；不满足即 **停止自动更新** 并提示用户：

```
检测项：必需文件/资产是否齐全 → 命名模式是否仍匹配 → 目录布局是否仍匹配
不匹配 ⇒ UpdateState.StructureChanged
        ⇒ 不下载、不安装、不回退猜测
        ⇒ 记录日志 + UI 提示 + 保留上一可用版本
```

---

## 7. 更新系统设计（§11.7）

### 7.1 组件

```
UpdateService
├─ IReleaseSource            # GitHub REST Release API 封装（缓存 + 条件请求 + 退避）
├─ ReleaseCache              # 本地缓存，见 §7.3
├─ IVersionResolver          # Provider 专属版本解析（§20）
├─ UpdatePolicy              # 自动检查/下载/安装开关与频率
└─ UpdateNotifier            # 更新通知
```

### 7.2 三种触发（任务书 §3）

| 触发 | 行为 | 默认 |
|---|---|---|
| **启动时自动检查** | 距上次检查超过阈值才联网 | **开**，阈值默认 **6 小时** |
| **手动检查（单个 Provider）** | 立即重新查询上游 | 按钮 |
| **全部检查** | 遍历所有 Provider | 按钮 |

**自动下载**默认 **关**；**自动安装**默认 **关**（任务书 §3 明确建议）。
**绝不无提示覆盖游戏目录**（任务书 §3）。

### 7.3 缓存结构（任务书 §9）

```
ReleaseCacheEntry
├─ ProviderId · LatestReleaseId · LatestTag · LatestAssetId
├─ Digest · PublishedAt · CheckedAt
└─ ETag / Last-Modified        # 条件请求，节省配额
```

**缓存失效条件**（调研得出的硬性要求）：
1. 超过 TTL
2. 用户手动强制刷新
3. **上游 `SHA256SUMS` 内容变化** —— 上游会**原地重建 Release 并更新校验和** `[E1]`
4. 检测到 asset 被撤走 `[E1]`

### 7.4 配额保护

GitHub 未认证配额仅 **60 次/小时**，且**多进程共享** `[E1]`。因此：
- 同一 Provider 在 TTL 内**只发 1 次** `/releases` 请求
- 使用 `If-None-Match` / `If-Modified-Since`
- 命中 403/429 时指数退避，并将 UI 置为「暂时无法检查」而**不**标记为「无更新」

---

## 8. 安全设计（§11.8）

### 8.1 签名校验 —— **单一实现，两条路径共用**

> **A1 是本项目要修的首要缺陷**：原项目 `DeploymentService` 已正确实现 `WinVerifyTrust`，但下载路径未复用同等强度校验。

```csharp
interface ISignatureVerifier
{
    SignatureVerdict Verify(string path, SignaturePolicy policy);
}
```

**判定顺序（严格 fail-closed）**：

```
WinVerifyTrust
    ├─ S_OK                     → 取证书
    ├─ CERT_E_UNTRUSTEDROOT     → 取证书（自签名证书链必然失败；须由指纹白名单裁决）
    ├─ TRUST_E_BAD_DIGEST       → 拒绝（文件已被篡改）
    ├─ TRUST_E_NOSIGNATURE      → 拒绝
    └─ 其它任何返回码            → 拒绝并记录原始码
```

随后：`Subject 匹配` **AND** `Thumbprint ∈ TrustedCertificateThumbprints`。

**指纹集合**（满足任务书 §31 的「兼容旧版」要求）：

| 指纹 | 归属 |
|---|---|
| `85BA66762F851E49148D706915D09026281418E6` | `CN=DLSSG for SM86 (self-signed)`，0.3.x 现行 `[E1]` |
| `A994735E6A7E9AA31FA926B3023B7C487DAB4850` | 0.2.x 历史指纹，用于识别旧安装 `[E3]` |

⚠️ **指纹不匹配时的行为必须收紧**：原项目对「官方源」仅记录警告即放行 —— 这会**把完整性降级为 Subject 子串匹配**。本方案改为：
**任何源出现未知指纹 → 拒绝下载**，并在 UI 提供「本次信任该证书」的显式用户确认（记录到本地信任库），而非静默放行。

### 8.2 下载安全（任务书 §34）

| 控制 | 规则 |
|---|---|
| 允许主机 | `github.com`、`api.github.com`、`raw.githubusercontent.com`、`codeload.github.com`、`objects.githubusercontent.com` |
| 第三方镜像 | **默认关闭**；用户显式开启后仍必须执行 Hash + Signature + Release metadata 三重校验 |
| 传输 | 仅 HTTPS；**每一跳重定向都重新校验主机与解析 IP** |
| IP 校验 | 拒绝环回 / 内网 / CGNAT / 链路本地 / 组播 / 保留地址 |
| 体积上限 | 单文件与总量均设上限 |

### 8.3 归档安全（任务书 §35）

- 防 Zip Slip（条目路径规范化后必须仍在目标目录内）
- 防绝对路径、防 `../`
- 限制条目数、单文件大小、总解压大小
- **仅在临时目录解压**；`下载 → 直接覆盖游戏目录` **禁止**

### 8.4 完整性

- 所有下载包 / DLL / ASI / Backup / 已安装文件**全部记录 SHA-256**（任务书 §32）
- **恢复备份前重新计算**；不一致 → **停止恢复**
- 优先使用 GitHub Release 的 `digest` 字段（实测各仓库均已返回 `sha256:…`）`[E1]`

---

## 9. 游戏识别方案（§11.9）

### 9.1 商店优先级（任务书 §15）

`Steam` → `手动添加` → `Epic` → `Game Pass` → 其他（后续扩展）

### 9.2 渲染 EXE 判定

原项目仅靠「文件名黑名单 + 大小排序」启发式，**证据不足**。本方案采用**分级判定 + 证据落库**：

```
1. 已验证数据库命中（同一游戏 + 同一商店 + 同一启动方式）
2. 商店清单/清单文件解析（如 Steam appmanifest、启动项配置）
3. 进程观察（用户启动游戏后记录实际渲染进程）
4. 静态启发式（排除 launcher/updater/redist，偏好 Shipping/Win64/Binaries 路径）
5. 用户显式确认
无法确定 ⇒ RendererExe = Unknown，要求用户确认（不得猜测）
```

**必须区分**：`LauncherExe` / 真实游戏 EXE / Renderer EXE / 子进程 / 商店包装器。

### 9.3 Graphics API 判定（任务书 §16）

判定优先级（**严格按此顺序**）：

```
Verified Database
  ↓ 未命中
Runtime Detection      # 运行时加载的模块（d3d11.dll / d3d12.dll / vulkan-1.dll）
  ↓ 未命中
Static Detection       # PE 导入表、游戏配置文件、引擎结构、附带 DLL
  ↓ 未命中
User Confirmation
  ↓ 用户不确定
Unknown                # 显示 Unknown，绝不猜
```

`Unknown` 时 `InstallPlanner` 必须返回 `UnknownApi` 错误，而不是默认挑一个 API。

---

## 10. NVIDIA Profile 设计（§11.10）

### 10.1 定位与合规声明

**目标**：普通用户无需打开 NVIDIA Profile Inspector（任务书 §21）。

**必须内建的事实性声明**（按 §10.5 D 的标记要求）：

| 项 | 状态 | 依据 |
|---|---|---|
| NVAPI / DRS 调用方式 | **官方公开** | `nvapi_QueryInterface` + 数字函数 ID 属官方机制 `[E1]` |
| 6 个 Smooth Motion Setting ID | **`Undocumented` + `Community Verified`** | 官方 DRS 头文件零命中；但 **两个独立社区来源数值一致** `[E1]` |
| RTX 30 上使用 | **`Experimental` / 社区路线** | 官方仅声明 RTX 40+ |

### 10.2 设置表（**已获 4 个独立来源交叉验证**：NPI / RHI / NVPIRevamped / DLSS5-Feeder）

| 设置 | ID | 取值 | 证据 |
|---|---|---|---|
| Smooth Motion 开关 | `0xB0D384C0` | `Off=0` / `On=1`（**per application**） | **4 源一致** `[E1]` |
| 启用的 API | `0xB0CC0875` | 位掩码 `DX12=1` / `DX11=2` / `Vulkan=4`，默认 `7` | **4 源一致**，位掩码语义获 2 源独立印证 `[E1]` |
| Flip Metering 0 | `0xB03A4546` | `Off=0` / `On=0xFFFFFFFF` | 3 源一致（RHI 命名为 `FlipPacingFs`）`[E1]` |
| Flip Metering 1 | `0xB03A4547` | `Off=0` / `On=1` / `ForceOn=0xFFFFFFFF` | 3 源一致（RHI 命名为 `FlipPacingWin`）`[E1]` |
| Debug Log Level | `0xB053C379` | 单一来源，诊断用 | NPI 命名表 `[E3]` |
| **Debug Bars** | `0xB01B8B02` | 2 源确认；**可用于确认 Smooth Motion 是否真的在运行** | NPI + DLSS5-Feeder `[E1]` |

> ⭐ **`0xB01B8B02`（Debug Bars）具有超出诊断的用途**：独立的第三方项目以它作为**判定 Smooth Motion 是否真正生效的可靠手段**（屏幕上出现彩色条即表示正在生成帧）。
> 这为本项目 §5.3 的 `Applied` / `Verified` 状态提供了一个**能覆盖到驱动内部行为的外部证据源**——补丁日志看不到驱动层的呈现路径，而它能。
> 因此运行验证必须采用**多信号交叉**（补丁日志 + 进程模块 + Debug Bars 用户确认 + 兼容性库历史），**任何单一信号都不得直接判定 `Verified`**。

> ⚠️ **严禁混淆**：官方 `VSYNCSMOOTHAFR_ID = 0x101AE763`（*Vertical Sync - Smooth AFR Behavior*）与 Smooth Motion **无关**。

### 10.3 读写与恢复语义（**三态区分**）

**核心要求（任务书 §23）**：安装前读取原值，卸载时**恢复原值**，禁止一律改成 `Off`。

| 原状态 | 判定方式 | 恢复方式 |
|---|---|---|
| **ABSENT**（原本未设置） | `EnumSettings` 中**不存在**该 ID | **`DeleteProfileSetting`** —— ⚠️ **绝不可用 `SetSetting(id, 0)`**，那会把「未设置」永久污染成「显式为 0」 |
| **显式为 0** | 存在且值为 0 | `SetSetting(id, 0)` |
| **非 0** | 存在且值非 0 | `SetSetting(id, 原值)` |

> **反面案例**：参考项目 RHI 在 `DlssPresetService.Reset.cs` 中自述「无法删除 raw 写入的设置、只能置 0」——**本项目不得照抄这一处理**。

**权限**：官方 DRS 文档**对管理员权限零声明** `[E1]`。因此**必须运行时探测**（尝试写入并按错误码判定），不得写死「需要提权」的假设。

### 10.4 基础设施约束

- 通过 `nvapi64.dll` 运行时 `DllImport` / `QueryInterface`，**不分发任何 NVAPI 二进制**
- **禁止**直接改写注册表或 `nvdrsdb*.bin`（格式未公开，损坏会导致用户全部 profile 归零）
- 整库备份使用官方 `SaveSettingsToFile` / `LoadSettingsFromFile`
- 优先复用受支持的调用序列：`CreateSession → LoadSettings → FindProfileByName / CreateProfile → GetSetting / SetSetting / DeleteProfileSetting → SaveSettings`

### 10.5 实现级陷阱清单（提炼自社区实现的失败经验，**本项目必须逐条规避**）

| # | 陷阱 | 规避方式 |
|---|---|---|
| 1 | **封装库会静默忽略新 ID**（参考项目注释自述 `NvAPIWrapper` 无法写入这些设置） | **使用裸 NVAPI**（`nvapi_QueryInterface` + 函数指针），不依赖高层封装 |
| 2 | **必须先 `NvAPI_Initialize` 才能取函数指针** | 初始化前置，且检查返回码 |
| 3 | **写入后必须显式 `DRS_SaveSettings`**，否则不落盘 | 事务内提交 |
| 4 | **函数 ID 存在版本差异** → 采用**「主 ID + 回退 ID」双表** | 集中定义 + 单元测试锁定；运行时逐个探测。已获两个独立来源印证的取值：`DRS_SetSetting` 主 `0x8A2CF5F5` / 回退 `0x577DD202`；`DRS_GetSetting` 主 `0xEA99498D` / 回退 `0x73BF8338`；`DRS_DeleteProfileSetting` 主 `0xD20D29DF` / 回退 `0xE4A26362`（**回退值与官方头文件一致，主值来源为社区注释，实现前须实测确认**） |
| 5 | ⚠️ **无法删除 → 置 0，会把「未设置」永久污染成「显式为 0」** | **必须用 `DeleteProfileSetting`**（§10.3 三态） |
| 6 | **这些设置对部分枚举接口不可见** → 备份/导出会漏 | 备份必须同时记录「存在性」与「值」，并用官方 `SaveSettingsToFile` 做整库级兜底 |
| 7 | ⚠️ **ULL 联动缺口**：NVIDIA App 开启 Smooth Motion 时会**连带开启 Ultra Low Latency**，而直接写 DRS **不会** | **必须自行处理该联动**（读取时可据此交叉校验；写入时按需补偿），否则行为与用户手工操作不一致 |
| 8 | **NVAPI 调用会阻塞 UI 线程** | 后台线程 + 超时 + 可取消 |
| 9 | **结构体偏移硬编码极易随驱动演进而写坏相邻字段** | 使用官方 `NVDRS_SETTING_V1` 结构定义 + `Marshal.StructureToPtr`；**禁止照抄固定字节偏移**（社区实现曾硬编码 12,320 字节结构体与偏移 4100/8220） |
| 10 | **进制字面量转录事故**（社区实现曾出现 C# `0x8A2CF5F5` 与脚本回退值 `0x8A24D5F5` 不一致） | 常量**单点定义**、禁止跨语言重复书写；用测试校验 |

> ⭐ **第 7 条（ULL 联动）是从社区失败经验中提炼出的、最容易被忽略的行为差异**：它会导致「本工具配置的结果」与「用户照文档手工操作的结果」不一致，从而让用户怀疑工具失效。必须显式建模。

---

## 11. Smooth Motion 自动配置流程（§11.11）

任务书 §20 的十步流程落地为**可中断、可回滚、每步留证**的编排：

```
1  检测 GPU                     → NvAPI_GPU_GetFullName / 适配器信息
2  检测 NVIDIA 驱动             → 驱动版本 + 分支；判定 Profiler 可用性
3  检测 Renderer EXE            → §9.2 分级判定
4  检测 Graphics API            → §9.3 四级判定（Unknown 则中止）
5  兼容性裁决                   → 查兼容性数据库 → 得到 EvidenceTier 与推荐 LoadMode
6  确定补丁加载方式             → DirectProxy / AsiLoader / MultiProxy（代理冲突检测见 §11.1）
7  安装补丁                     → 事务化：Stage → Verify → Backup → Install → Validate → Commit
8  配置 NVIDIA Profile          → 读取原值 → 写入 → 保存快照（三态记录）
9  运行验证                     → 启动游戏 → 解析日志判定 Loaded/Requested/Applied
10 保存成功方案                 → 写入 compatibility database（含全部指纹）
```

**代理冲突检测（任务书 §19）**：安装前扫描 `version.dll` / `winmm.dll` / `dinput8.dll` / `dbghelp.dll` / `dxgi.dll` / `d3d12.dll` / `winhttp.dll`，
判定每个文件是「本工具安装 / 其他 Mod / 游戏原文件 / 未知」；**未知文件禁止覆盖**；无安全入口时**停止自动安装**并显示 `Proxy Conflict`。

> **调研要点**：上游 `dlssg_sm86.ini:3-4` 说明「多个代理共存不冲突（先加载者为 active，其余仅转发），**但一个就够了**」；而 `dxgi.dll` / `d3d12.dll` 属渲染热路径、加载顺序敏感，**只在工具类代理都未被加载时才用一个**。
> 因此本项目的默认策略是**部署单个经判定最可能被加载的入口**，并在 `Verified` 失败时提供「多入口回退」作为用户可选的修复手段（见 U-02）。

---

## 12. UI 页面结构（§11.12）

任务书 §38 要求「简洁、清楚、状态一眼可见、按钮少、高级设置隐藏」。

| 页面 | 内容 |
|---|---|
| **Dashboard** | GPU / 驱动、各 Provider 的 `Installed` / `Latest` / 状态、`[检查所有更新]` |
| **GameLibrary** | 游戏列表：API、各补丁状态、兼容性等级 |
| **GameDetails** | 渲染 EXE、API、各补丁版本与状态、`NVIDIA Profile ✓ / Loaded ✓ / Verified ✓`、操作按钮（自动配置 / 检查更新 / 更新 / 修复 / 恢复 / 诊断） |
| **Updates** | 每个 Provider 的 Installed / Latest / `Update Available` / Last checked / `[更新]` / `[检查所有更新]` |
| **Downloads** | 进行中的下载、校验结果、失败原因 |
| **Settings** | 自动检查开关与频率、自动下载、Release Channel（默认 Stable）、镜像开关（默认关）、语言与主题 |
| **Diagnostics** | 日志、`Create Diagnostic Package`（**不含补丁二进制**）、Profile 快照与恢复入口 |

**高级选项默认折叠**（任务书 §43）：Proxy DLL、ASI Mode、Graphics API、NVIDIA Profile、Flip Pacing、Low Latency、Launch Arguments、Provider、Logs、Release Channel。

**状态语必须精确**（任务书 §37）：禁用「Safe」，改用 **`No known anti-cheat detected`**。

---

## 13. Update 流程（§11.13）

```
Check Release          → IReleaseSource（缓存优先，条件请求）
   ↓
Compare Installed      → IVersionResolver（§20），而非字符串比较
   ↓
Display Changelog      → 展示版本变化 + 资产选择理由 + 证据等级
   ↓
【用户确认】            → 默认不自动安装
   ↓
Download Asset         → 到临时目录；进度可取消
   ↓
Verify Digest          → GitHub digest / 官方 SHA256SUMS
   ↓
Verify Package         → 签名（§8.1）+ 归档安全（§8.3）
   ↓
Stage                  → 解压/展开到暂存区并再次校验
   ↓
Backup                 → 记录将被替换文件的 SHA-256 与原值
   ↓
Install                → 仅写入计划内文件
   ↓
Validate               → 哈希复核 + 结构断言
   ↓
Commit                 → 提交事务，更新安装记录
   ↓
失败 ⇒ Rollback        → §14
```

**关键约束**：
- **保留旧版直到新版验证成功**（任务书 §56）
- 更新内容以「提交信息 + 修复清单」展示；MFG 的多个 2.8.2 包**必须并列展示修复清单**，不得静默选包
- **升级默认只替换补丁文件，不整文件覆盖用户 INI**（任务书 §28）—— 上游明确「升级 = 覆盖 `version.dll`，INI 一般无需改动」

**配置迁移**（任务书 §28）：
```
旧 INI + 旧 Provider 版本 + 旧 Proxy + 新版本配置格式
  → 格式未变：保留用户设置
  → 发生 breaking change：键级迁移 + 明确提示 + 迁移前备份
```
已知破坏性变更实例：`Optimized` 由 `bool` 变 `0–3` 整数；`altnative/` → `alternatives/`；`winhttp.dll` 被移除。

---

## 14. Rollback 流程（§11.14）

| 触发场景 | 动作 |
|---|---|
| 下载失败 / Hash 错误 | 丢弃临时文件，**不触碰游戏目录** |
| Release 结构变化 | 停止自动更新，提示用户，保留旧版 |
| 安装中途失败 | 用备份**逐个还原**已替换文件；校验还原后哈希 |
| 运行验证失败 | 保留新版但置为 `Failed`，提供一键回滚到旧版 |
| 用户主动回滚 | 从 `BackupSet` 还原 + 恢复 NVIDIA Profile 原值 |

**硬性要求**：
1. **保留旧版**，不破坏原本可工作的安装（任务书 §29）
2. 备份还原前**重新计算 SHA-256**，不一致则**停止还原**
3. Profile 恢复必须按 §10.3 的三态语义执行，**不得一律写 `Off`**
4. 回滚本身也要留日志与最终状态

---

## 15. 测试方案（§11.15）

测试必须与功能同步设计（任务书 §47）。夹具模式沿用原项目的「合成夹具 + 场景分组」，但**必须修复其三个系统性盲区**：下载校验、真实下载、`library.json` 注入。

### 15.0 夹具设计的两条硬性要求（审计得出的直接结论）

**① 签名夹具必须是「带真实签名的极小 PE」，不能是随机字节。**
原夹具用随机字节构造（`test/Harness/Program.cs:320`），这**只能测出「不是签名文件」，无法测出「是签名文件但不是我们要的那一个」**——而这恰是签名校验链缺陷（A1）的核心场景。
⇒ 新夹具必须提供三档样本：
| 样本 | 构造方式 | 期望 |
|---|---|---|
| 未签名 PE | 极小合法 PE，无签名块 | 拒绝（`TRUST_E_NOSIGNATURE`） |
| **有效签名 + 篡改字节** | 取真签名样本，翻转 `.text` 段 1 字节，**保留 Certificate Table** | 拒绝（**`TRUST_E_BAD_DIGEST`**） |
| 有效签名 + 未知指纹 | 有效签名但指纹不在白名单 | 拒绝（**不得静默放行**） |

**② 测试必须断言行为，禁止出现恒为真的断言。**
原夹具存在 `Check("...", true)` 这类**恒为真**的无效断言（`Program.cs:718`），以及只断言「指纹可读取」而不断言相等的用例（`:2274`）⇒ 证书轮换、指纹变更都不会让任何测试变红。
⇒ 新夹具的每个断言必须能在实现被破坏时失败；CI 中应包含**变异测试自检**（故意破坏一处实现，确认至少有一个用例变红）。

### 15.1 Security

| 用例 | 期望 |
|---|---|
| 正常签名 | 通过 |
| **DLL 被修改 1 字节（保留签名块）** | **`WinVerifyTrust` 返回 `TRUST_E_BAD_DIGEST` → 拒绝** |
| Thumbprint 不在白名单 | 拒绝（**不得静默放行**） |
| Hash 错误 | 拒绝 |
| Zip Slip（`../`、绝对路径） | 拒绝 |
| 超大文件 / 超多条目 | 拒绝 |
| 非 HTTPS | 拒绝 |
| 非白名单 Host | 拒绝 |
| **`set -` 已知对照符号验证检索面** | 元测试：确保「不存在」类断言有效 |

### 15.2 Deployment

`version.dll` 空闲 / 被占用 / 自动换其他入口 / 所有入口冲突 / Backup / Restore / **失败安装回滚** / **Restore 时哈希不符必须中止**

### 15.3 Profile

创建 Profile / 命中已有 Profile / 设置 Smooth / 修改 API / 保存 / **恢复原值（含 ABSENT 三态）** / 权限不足的降级路径

### 15.4 Update

无更新 / 有更新 / 手动强制检查 / 自动检查 / **GitHub API 不可用** / 下载失败 / Hash 错误 / **Asset 缺失** / **Tag 非 SemVer** / **同版本不同 Asset** / **Release 被重新上传** / Prerelease 忽略 / Release cache 命中

> 上述每一项都能在调研中找到对应的真实案例（见 `RESEARCH_NOTES.md` §4.2 的 T1–T9）。

---

## 16. 许可证风险（§11.16）

| 风险 | 说明 | 对策 |
|---|---|---|
| **GPL-3.0 污染** | `RankFTW/RHI` 为 GPL-3.0，与本项目 MIT **不兼容** | **禁止复制其任何源代码**；只学习思路（§10.6） |
| **上游二进制再分发** | `sdli1995` 无 LICENSE 文件（README 称 GPLv3，且声明 NVIDIA 材料 *not relicensed*）；MFG **无任何 LICENSE** | **禁止打包**其任何二进制；只允许用户端下载 |
| **专有 EULA 越界** | `xikarioz` EULA 3(a) 禁止「以其他方式使第三方可获得」、3(b) 禁止 bundle | 只提供**官方链接**；不做镜像、不静默安装、不捆绑、不改名 |
| **NVIDIA 材料** | 内嵌 `nvngx_dlssg.dll`、重编译内核资源 | 一律不打包；由用户端在其自身驱动许可下获取 |
| **NVAPI 再分发** | `NVIDIA/nvapi` 为 `NOASSERTION` | 运行时使用驱动提供的 `nvapi64.dll`，**不分发任何 NVAPI 二进制** |

**产出物**：`THIRD_PARTY_NOTICES.md`，逐条区分 `Bundled dependency` / `Downloaded external tool` / `Reference-only project`（任务书 §51）。

---

## 17. 开发阶段划分（§11.17）

沿用任务书 §53 的 11 个 Stage，并**按调研结论补入两项前置约束**：

| Stage | 内容 | 本方案补充的硬性约束 |
|---|---|---|
| **Stage 1** | 调研 + 架构规划 | ← **当前阶段**；产出本文件与 `RESEARCH_NOTES.md` |
| **Stage 2** | 整理原 BUNNY 项目 | ⚠️ **必须先抽 Core 再拆程序集**（存在三方循环引用，A4）<br>⚠️ **先解决 .NET SDK 缺失（R-01）**，否则无法构建与测试<br>修复项：签名校验统一（A1）、备份校验（A10）、下载器、部署事务<br>**可直接复用的 10 项资产**（审计结论）：`WinVerifyTrust` 校验器、URL/IP 策略、`PathGuard`+`ShellExecuteExW`、Steam 库解析、`IniTemplate` 的「只改存在的键」、`Gpu` 的 PCI ID 判定与 INF 解析、`LibraryStore` 原子写、测试夹具的数据隔离模式 |
| **Stage 3** | Provider Framework | 必须同时支持**两种分发模型**（§6.2）；迁入 `DlssgSm86Provider` |
| **Stage 4** | UpdateService | 版本解析（§20）、Release 缓存（§7.3）、digest 校验、更新通知 |
| **Stage 5** | Game Detection + InstallPlanner | `RendererExe` 分级判定（§9.2）、Graphics API 四级判定（§9.3）、代理冲突检测 |
| **Stage 6** | NVIDIA Profile Service | **先独立测试**；三态恢复（§10.3）；权限运行时探测 |
| **Stage 7** | Smooth Provider | Check / Download / Verify / Install / Update / Restore |
| **Stage 8** | 自动 Smooth 配置 | §11 的十步编排端到端 |
| **Stage 9** | 游戏兼容数据库 | 六元组 × 六档等级；Ground Branch 为首批自测目标 |
| **Stage 10** | UI 重构 | 核心稳定后再做；逻辑不得再进 code-behind |
| **Stage 11** | 完整回归测试 | §15 全量 |

**每个 Stage 完成后必须输出**（任务书 §54）：`Implemented` / `Tests` / `Known Issues` / `Remaining Risks` / `Files Changed`。

**禁止**（任务书 §52）：把 Smooth Motion 二进制塞进程序、无许可证复制代码、为支持更多游戏猜配置、覆盖未知 DLL、改 DriverStore / 驱动文件、关闭或绕过 Anti-Cheat、把安装成功等同于生效、写死 Release URL、写死 Asset 名、把逻辑写进 `MainWindow`、打补丁式修复架构问题。

---

## 18. 风险与未知事项（§11.18）

**完整风险登记册见 [`RESEARCH_NOTES.md` §8](RESEARCH_NOTES.md)，未知事项见其 §6。** 此处只列对架构有决定性影响者：

| ID | 项 | 对架构的影响 |
|---|---|---|
| **R-01** | ~~本机**无 .NET SDK**~~ → **已消解**（2026-09-26，.NET 8 SDK **8.0.425** 已装） | ~~阻塞 Stage 2~~ → **不再阻塞**；构建 0 警告 / 0 错误，Harness 343 通过 / 0 失败 |
| **R-03** | Release 列表顺序与时间字段均不可靠 | 决定了 `IVersionResolver` 必须显式实现，不可依赖 API 顺序 |
| **R-05** | 驱动更新会破坏已装配置 | 安装记录必须保存驱动指纹；驱动变更时主动提示 |
| **R-11** | Undocumented Setting ID | 决定了 Profile 层必须「写入前读原值 + 精确恢复 + 失败回退」 |
| **U-01** | 上游「发布包」与 git 仓库布局不一致 | 影响「部署几个代理」；**须实机验证**后再固化 |
| **U-02** | 单代理 vs 多代理是否等价 | 影响 `InstallPlanner` 的 `LoadMode` 策略 |

### 18.1 按 §10.7 的「是否可阻止功能」裁决

针对 **RTX 30 Smooth Motion 的 NVIDIA Profile 写入**，逐条核对 §10.7 的六种可阻止情形：

| 情形 | 是否命中 | 理由 |
|---|---|---|
| ① 无法找到任何可复现的技术依据 | **否** | 已取得 6 个 Setting ID 的双源交叉验证 `[E1]`，且两个社区项目已实际实现 |
| ② 所有已知来源相互矛盾且无法验证 | **否** | 两个独立来源数值**完全一致**；差异仅在命名（Flip Metering vs Flip Pacing），不影响 ID |
| ③ 许可证明确禁止计划中的使用方式 | **否** | 本方案只做「读取/写入用户本机驱动 profile」，不复制任何第三方代码、不再分发二进制 |
| ④ 需要破坏或绕过 Anti-Cheat | **否** | 方案明确**不**关闭、不绕过、不 patch 任何反作弊 |
| ⑤ 必须修改 NVIDIA DriverStore / 驱动文件且无安全可逆方案 | **否** | **「写 DRS profile」与「改 DriverStore」是两件不同的事**：DriverStore 指驱动包仓库（微软明确其不应被程序化修改），而 DRS 的写入对象是 **NVIDIA driver profile database**（`%ProgramData%\NVIDIA Corporation\Drs\`），二者不是同一对象。且官方提供 `NvAPI_DRS_SaveSettingsToFile` / `DeleteProfileSetting`，构成**安全可逆方案**。本方案明确**不修改任何驱动文件、不删除驱动包** |
| ⑥ 会导致不可接受的数据损坏或系统风险 | **否**（附条件） | 条件：严格遵守 §10.3 三态恢复、禁止直接改写 `nvdrsdb*.bin`、提供失败回退。旁证：社区记录中的**严重故障（崩溃、黑屏）全部来自进程注入路线**，而 profile 写入的最坏情况是「设置无效」 |

⇒ **该功能不满足任何一条阻止条件，应继续实现**，状态标记为 `Undocumented` + `Community Verified` + `Experimental`。

---

## 19. Release API 方案（§11.19）

### 19.1 数据来源

**优先** GitHub REST Release API `GET /repos/{owner}/{repo}/releases?per_page=100`：

| 字段 | 用途 |
|---|---|
| `id` | Release 唯一标识（应对「同 tag 重新上传」） |
| `tag_name` | 版本锚点（**但不总是版本号**） |
| `name` | MFG 场景的**实际版本来源** |
| `published_at` / `created_at` | 辅助排序（**可互相倒挂，不得单独依赖**） |
| `prerelease` / `draft` | 渠道过滤（默认只用 stable） |
| `assets[].id` / `.name` / `.size` / `.digest` / `.browser_download_url` | 资产选择与校验 |

### 19.2 硬性规则（全部来自实测）

| # | 规则 | 依据 |
|---|---|---|
| 1 | **不得**假设列表首项是最新 | T1：MFG 最新版在 index 1 |
| 2 | **不得**单独依赖 `created_at` 或 `published_at` 排序 | T2/T3：顺序不一致且可倒挂 |
| 3 | **不得**用 `/releases/latest` 作为唯一入口 | T4：对无 asset 仓库返回 `assets=[]` |
| 4 | **必须**过滤平台与预发布 | T5：会命中 Linux 包 / alpha |
| 5 | **不得**用 asset 名反推版本 | T6/T7：名可滞后、同名可不同内容 |
| 6 | **必须**容忍 asset 被撤走 | T8 |
| 7 | **必须**在下游变更时使缓存失效 | T9：上游会原地重建并更新校验和 |
| 8 | **必须**显式分页 | `per_page` 过小会静默截断（本项目实际踩坑：26 被读成 15） |
| 9 | **必须**处理 403/429 | 未认证配额仅 60/h，共享 |

### 19.3 无 Release 资产的兜底（`sdli1995` 场景）

```
1. 检查 Release 是否存在           → 仅为「版本号」来源
2. 检查 assets 是否为空            → 空则切换到 git 树模型
3. 以 tag（或 main）为 ref，逐文件 raw 下载
4. 版本号交叉校验                  → README 首行的 `- 0.3.5 Version` 与 tag 比对
5. 结构断言                        → 必需文件齐全才算解析成功
```

---

## 20. Provider 版本解析方案（§11.20）

**根本原则**：**禁止** `new Version(tag)`，**禁止**全局统一解析器。每个 Provider 实现自己的 `IVersionResolver`。

### 20.1 通用输出模型

```
VersionCandidate
├─ Version        SemanticVersion      # 归一化后的可比较版本
├─ Variant        string?              # 功能线/构建变体（如 310.9 / 310.1）
├─ Qualifiers     string[]             # 限定词（GP8/GP9/Fixes1/Xbox/GamePass/Driver-61714 …）
├─ BuildRef       string               # tag 或 commit
├─ ReleaseId      long?
├─ AssetId        long?
├─ AssetName      string?
├─ Digest         string?
├─ PublishedAt    DateTimeOffset
└─ Confidence     Confirmed | Inferred | Ambiguous
```

### 20.2 各 Provider 的解析规则

| Provider | 版本来源 | 规则 |
|---|---|---|
| **`DlssgSm86Provider`** | tag（`0.3.5`）**且** README 首行 `- X.Y.Z Version` 交叉校验 | 两者不一致 ⇒ `Ambiguous`，停止自动更新并告警。另需解析**构建变体**：根目录 = 最新（310.9），`310.1/` = 旧版 |
| **`MfgSmoothProvider`** | **tag 不可用**（`smxbox`/`smfix`/…） | 从 `name` 用 `Smooth Motion (\d+\.\d+(\.\d+)?)` 提取版本；从 asset 名用 `SmoothMotion-(\d+\.\d+\.\d+)-R(\d+)-GP(\d+)-?(.*)\.zip` 提取 `R`/`GP`/后缀；**版本号在 4 个 Release 重复 ⇒ 必须以 `(版本, 限定词, published_at)` 三元组区分** |
| **`ExternalSmoothMotionProvider`** | **只信 tag** | `prerelease == false` 且 `tag ≈ ^v\d+\.\d+\.\d+$` 中 `published_at` 最大者；`linux-*` 隔离为独立产品线；**asset 名不得作为版本源** |

### 20.3 Asset 选择规则（`SelectReleaseAsset()`，Provider 私有）

**`DlssgSm86Provider`（git 树模型）**：按固定清单逐文件解析，**源路径 → 本地路径**分离，以容忍上游目录改名：
```
version.dll                  → version.dll
dlssg_sm86.ini               → dlssg_sm86.ini
alternatives/winmm.dll       → altnative/winmm.dll
alternatives/dinput8.dll     → altnative/dinput8.dll
alternatives/dbghelp.dll     → altnative/dbghelp.dll
alternatives/dxgi.dll        → altnative/dxgi.dll
alternatives/d3d12.dll       → altnative/d3d12.dll
```
（该映射的必要性已被上游 `0.2.4 → 0.3.0` 的真实改名验证：`altnative/` → `alternatives/`、移除 `winhttp.dll`、新增 `d3d12.dll` 与 `dbghelp.dll`）

**`MfgSmoothProvider`**：

```
纳入：prerelease == false
  AND name  ≈ ^Smooth Motion \d+\.\d+\.\d+
  AND asset ≈ ^SmoothMotion-\d+\.\d+\.\d+-R\d+-GP\d+.*\.zip$
强制排除（asset 名）：Manual | Benchmark | Liquid-Glass | Proxy-Alternatives | ^asi | alt
强制排除（name）：Benchmark | Manual | ALTERNATIVE PROXIES | ASI Version | Version 1 | Version 2
强制排除（name 前缀）：^DLSS MFG        # 旧功能线
→ 命中多个（例如 4 个 2.8.2 包）时：禁止静默选包，
  必须并列展示各自修复清单并提示「上游未说明累积关系」
```

**`ExternalSmoothMotionProvider`**：
```
首选：SmoothMotionSM86-Setup.exe
次选：SmoothMotionSM86-<version>-win64.zip
必取：SHA256SUMS*.txt         # 用于校验
排除：linux-* 与 prerelease（除非用户显式选择 alpha）
```

### 20.4 去重与身份

- **唯一键** = `repo + tag + asset 名 + size`
- **内容身份** = `SHA-256`
- **禁止**用 asset 名去重（已实测存在同名不同内容：xikarioz `v0.4.2` 与 `v0.4.3`）
- 解析器必须容忍：Release 正文提到但实际不存在的 asset；`name` 与 asset 的修复序号矛盾（`Fixes 2` vs `Fixes1`）

---

## 21. 交付确认与下一步

### 21.1 本阶段交付

| 文件 | 内容 |
|---|---|
| [`docs/RESEARCH_NOTES.md`](RESEARCH_NOTES.md) | 事实层：研究过的仓库/文档/Release/Issues、关键结论、交叉验证、许可证、风险、未知事项 |
| [`docs/ARCHITECTURE_PLAN.md`](ARCHITECTURE_PLAN.md) | 本文件：§11 的 20 项设计 |
| [`docs/phase0-manager-audit.md`](phase0-manager-audit.md) | 原管理器源码审计（附 `文件:行号` 证据） |

### 21.2 进入 Stage 2 的前置条件

1. ~~**解决 R-01**：本机无 .NET SDK~~ → ✅ **已完成**（2026-09-26）：.NET 8 SDK **8.0.425** 已装，`build -c Release` 0 警告 / 0 错误，Harness 343 通过 / 0 失败
2. **实机消解 U-01 / U-02**：代理部署数量与方式 → 设计见 **§37**，**单代理与多代理的等价性仍标 `Unresolved / Needs Validation`**，待 §37.6 的最小真机实验（属 Stage 8）
3. **建立 A1 的失败测试**：篡改样本必须被 `WinVerifyTrust` 拒绝
4. **确认 §18.1 的裁决**：RTX 30 Smooth Motion 按社区实验路线继续实现
5. ~~**交付仓库**（原 R-15）~~ → ✅ **已完成**：`PLA0185/RTX30-FrameGen-Manager`，基线提交 `b0a6424` 已推送
6. ~~**HAGS 环境前置**（原 R-14）~~ → ✅ **已满足**（`HwSchMode = 2`）；**运行时生效待实机确认**，检测策略见 §36.1

### 21.3 本阶段明确未做的事

- 未编写任何实现代码
- 未下载任何补丁二进制本体
- 未修改任何游戏目录或 NVIDIA Profile
- Ground Branch 的兼容性结论**尚未产出**（上游零命中，只能实机自测）

---
---

# ═══ 以下为 v5 增量设计（任务书 v5 §59–§97）═══

> **修订说明**：任务书 **v5 完整包含 v2 的全部内容（逐行比对确认无删除、无修改）**，仅追加 §59–§97。
> 因此 §1–§21 的**调研结论与架构设计全部继续有效**，本节只补充 v5 新增要求，**不重写既有设计**。
> v5 优先级高于此前所有版本；若冲突以 v5 为准。

## 22. v5 差异映射总览

| v5 章节 | 主题 | 状态 |
|---|---|---|
| §59 · §60 | GitHub 最终交付、最终结束条件 | 🆕 新增 |
| §61 · §62 · §64 · §65 · §82 | **Self Update（核心功能）** | 🆕 新增 |
| §63 | 上游补丁始终保持最新（Runtime Update Discovery） | 🆕 新增 |
| §66 · §67 · §68 · §96 | 自更新测试与验收补充 | 🆕 新增 |
| **§69** | **Latest Available ≠ Latest Compatible** | 🆕 **新增（§24）** |
| §70 | 兼容性矩阵（12 维度） | ⚠️ 扩展既有六元组 |
| §71 | Install Recipe 系统 | 🆕 新增 |
| §72 | Schema Version 与迁移 | 🆕 新增 |
| §73 · §74 · §75 | Operation Journal / 单实例 / 游戏运行状态 | 🆕 新增 |
| **§76 · §77** | **实机验证边界（防虚报）** | 🆕 **新增（§33）** |
| §78 · §79 | UAC 最小权限 / Portable·Installed | 🆕 新增 |
| §80 | Fork 与上游维护策略 | 🆕 新增 |
| §81 | Provider Metadata 安全 | 🆕 新增 |
| §83 · §84 · §85 | Safe Mode / 离线 / Rate Limit | 🆕 新增 |
| §86 · §87 · §88 | 隐私脱敏 / 临时文件 / Backup 生命周期 | 🆕 新增 |
| §89 · §90 · §91 | 更新回退 / Provider 健康 / Feature Flag | 🆕 新增 |
| §92 · §93 · §94 · §95 | UI 状态统一 / 首启向导 / README / 文档分离 | 🆕 新增 |
| §97 | 最终原则补充 | 🆕 新增 |

**与既有设计的冲突项共 8 处**，均在 §23–§31 中给出调整方案，**不推翻既有架构**。

---

## 23. Self Update 系统（v5 §61、§62、§64、§65、§82）

### 23.1 **两套更新系统必须独立**（v5 §62）

```
Update
├─ SelfUpdateService      ← 本软件自身（新增）
│  ├─ GitHub Release（本项目仓库）
│  ├─ Current / Latest App Version
│  ├─ Updater（独立进程）
│  └─ Rollback
│
└─ PatchUpdateService     ← 第三方补丁（已有 §7）
   ├─ DlssgSm86Provider
   ├─ MfgSmoothProvider
   └─ ExternalSmoothMotionProvider
```

**可共享**：`GitHubClient`、`DownloadService`、`HashVerification`、`ReleaseCache`。
**必须独立**：**安装逻辑、回滚逻辑、版本规则**。

> ⚠️ **这是对 §7 的调整**：原设计只有一个 `UpdateService`。现要求拆为两个并列服务，`GitHubClient` 等基础设施下沉共享。

### 23.2 自更新源与判定（v5 §61.2、§61.3、§64）

- **只信本项目 GitHub Release**（`GitHub Releases API`：`id` / `tag` / `published_at` / `prerelease` / `assets[].digest` …）
- **禁止**用 `main` 分支最新 Commit 判断普通客户端是否需要更新（v5 §61.2、§61.14）
- **`Push Commit` ≠ 客户端可更新**：只有**正式 Release** 才进入默认更新通道（v5 §64）
- 默认渠道 **Stable only**；`Update Channel` 可选 Prerelease/Experimental，**普通用户默认不收到测试版**（v5 §61.4）

### 23.3 检查策略（v5 §61.5、§61.6）

| 触发 | 行为 |
|---|---|
| 启动时自动检查 | **带缓存**：距上次检查超过 **6–12 小时**才联网；缓存 `LastSelfUpdateCheck` / `LatestReleaseId` / `LatestVersion` / `LatestAssetId` / `LatestDigest` |
| 手动检查 | `[检查软件更新]`（设置页 / 关于页 / 更新中心）→ `forceRefresh = true`，绕过缓存 |

### 23.4 **独立 Updater 进程**（v5 §61.11、§61.12）

> 主程序无法可靠覆盖自己正在使用的 EXE/DLL，因此必须由独立进程完成替换。

```
Manager 下载新版 → 验证 → 启动独立 Updater
   → Manager 退出 → Updater 等待进程退出
   → 备份旧程序 → 替换新版 → 启动新版 → 验证 → 删除旧备份
```

**Updater 要求**：小、简单、独立、少依赖。
**事务性**：`Download → Verify → Stage → Backup Current App → Replace → Launch New App → Verify → Commit`；失败 ⇒ `Rollback Previous App Version`，**不允许留下半更新状态**。

### 23.5 自更新校验与资源识别（v5 §61.9、§61.13）

- **必须**：`GitHub Release digest`、`SHA256`、`HTTPS`、`Host allowlist`、`Expected asset`
- 未来若发布包签名：增加 `Authenticode` / `Certificate Thumbprint` / `WinVerifyTrust`
- **禁止**把 `Source code.zip` 当作软件更新；`SelfUpdateProvider` 必须明确识别正式资产：
  ```
  RTX30FrameGenerationManager-<version>-Setup.exe
  RTX30FrameGenerationManager-<version>-Portable.zip
  SHA256SUMS.txt
  ```
  命名规则在发布流程中**固定**。

### 23.6 失败行为与强制更新（v5 §61.15、§61.16）

失败（GitHub 不可用 / 下载失败 / digest 不符 / 包无效 / Updater 失败 / 新版启动失败）必须：**保持当前版本可用 → 不删除旧程序 → 回滚 → 明确错误 → 写日志**。
**默认禁止 Forced Update**，除非未来存在严重安全问题；正常流程为「提示 → 用户确认 → 更新」。

### 23.7 Self Update 后的配置迁移（v5 §82）

新版首次启动：`检测 Schema → 迁移 → 验证 → 正常启动`；覆盖 `Settings` / `Database` / `Provider Metadata` / `Install Record` 四类。
**迁移失败 → 进入 Safe Mode**（§28.4）。

### 23.8 GitHub Actions 与发布流程（v5 §61.8、§65）

```
Tag: v*  →  dotnet restore → dotnet test → dotnet publish
        →  Package → SHA256SUMS → Create Release → Upload Assets
```
至少输出 `Installer` / `Portable package` / `SHA256SUMS.txt`。
**测试失败必须禁止发布。**

---

## 24. **版本策略：Latest Available ≠ Latest Compatible**（v5 §69）⭐

### 24.1 两个必须分开的概念

```
Latest Available   上游最新发布版本
Latest Compatible  当前用户环境下最新兼容版本
```

> **绝对禁止**为了「始终保持最新」而盲目升级到不兼容当前驱动 / 游戏 / API 的版本。

**v5 给出的实例**：上游最新 `2.9.0` 要求 `Driver 617.14+`，而用户为 `616.92` ⇒ `Latest Available = 2.9.0`，**`Latest Compatible = 2.8.2`**。UI 必须同时显示两者。

> ⚠️ **这不是假设** —— 本项目调研已证实同类事实：MFG 的 `smdriverupdate` 包明确绑定 `617.14`；xikarioz 按 `NvPresent` 二进制 SHA-256 白名单放行（616.64 golden / 591.86 static / **616.92 未授权**）。

### 24.2 更新决策前置检查（v5 §69.1）

更新前必须检查：`GPU` · `Driver` · `Graphics API` · `Game` · `Provider` · `Installed Version` · `Target Version` · `Compatibility Data`。
只有判定 `Compatible`，**或用户明确选择 `Experimental`**，才允许安装。

### 24.3 版本固定（v5 §69.2）

高级设置提供 **`Pin Version` / `Hold Updates`**。
用途：某游戏只在某版本稳定、新版本出现回归、用户暂不想升级。
**固定后自动检查仍可报告新版本，但不得自动覆盖用户固定版本。**

> **对既有设计的调整**：§20 的版本解析只解决「哪个是最新版」；本节新增「哪个是**该用户可用的**最新版」。`IVersionResolver` 的输出需进入**兼容性裁决层**后才能成为安装目标。

---

## 25. 兼容性矩阵（v5 §70）

**对既有六元组的扩展** —— 从 6 维升至 **12 维**：

```
GPU · Driver · Graphics API · Game · Store · Renderer EXE
Provider · Provider Version · Install Mode · Proxy/ASI · Launch Mode · Validation State
```

```json
{
  "game": "Ground Branch",
  "rendererExe": "GroundBranch-Win64-Shipping.exe",
  "gpuArchitecture": "SM86",
  "driver": "616.92",
  "api": "DX12",
  "provider": "mfg-smooth",
  "providerVersion": "2.8.2",
  "installMode": "asi",
  "status": "user-verified"
}
```

**硬性约束**：**不得**把「某版本 + 某驱动测试成功」扩大解释为「这个游戏所有版本都支持」。

---

## 26. Install Recipe 系统（v5 §71）

**目标**：把每个游戏的特殊安装逻辑**从代码中抽离为数据**。

```
InstallRecipe
├─ Renderer EXE · Graphics API · Provider · Provider Version Range
├─ Required Files · Proxy Strategy · ASI Strategy
├─ NVIDIA Profile Changes · Launch Arguments
└─ Validation Steps · Rollback Steps
```

### 26.1 Recipe 证据（v5 §71.1）

每条 Recipe 必须记录：`EvidenceSource` · `EvidenceType` · `LastVerified` · `ValidationLevel`。
来源类型：`Official Provider README` · `GitHub Release` · `GitHub Issue` · `GitHub Discussion` · `Community Reproduction` · `Local User Test` · `Project Maintainer Test`。

### 26.2 验证等级（v5 §71.2，8 档）

```
Known · Community Verified · User Verified · Project Verified
Experimental · Unknown · Broken
```

> **与既有设计的对齐**：`RESEARCH_NOTES.md` §5.4 采用的是 xikarioz 的 6 档（`PROJECT_VALIDATED` / `COMMUNITY_CONFIRMED` / `COMMUNITY_REPORTED` / `EXPERIMENTAL` / `UNTESTED` / `FAILED`）。
> **采用 v5 的 8 档为主**，并保留映射：`PROJECT_VALIDATED→Project Verified`、`COMMUNITY_CONFIRMED→Community Verified`、`COMMUNITY_REPORTED→Known`、`UNTESTED→Unknown`、`FAILED→Broken`。
> **禁止**只用 `Working / Not Working` 二分。

---

## 27. 数据 Schema 与迁移（v5 §72）

### 27.1 必须带 `schemaVersion` 的数据

`Settings` · `Game Database` · `Install Records` · `Compatibility Database` · `Provider Metadata` · `Release Cache` · `Backup Metadata`

```json
{ "schemaVersion": 3 }
```

### 27.2 迁移规则（v5 §72.1）

```
旧 Schema → Migration → 新 Schema
```
**禁止**「格式变了 → 直接清空用户数据」。
迁移失败必须：**保留旧数据 → 创建备份 → 停止危险写入 → 提示恢复/重试**。

---

## 28. 可靠性与并发（v5 §73、§74、§75、§83、§84、§85）

### 28.1 Operation Journal（v5 §73）

应对 `程序崩溃 / 断电 / 强制结束 / Windows 重启`，**不能只依赖内存状态**。

```
OperationId · Game · Action · Stage · BackupPath
FilesChanged · ProfileChanges · StartedAt
```

启动时若发现 **Incomplete Operation** ⇒ **自动进入 Recovery**，**而不是继续覆盖文件**。

### 28.2 单实例与操作锁（v5 §74）

**必须防止同时开两个 Manager** 造成「同时更新 / 同时写 Profile / 同时改游戏目录」。
要求：`Single Instance` + 对 `Game Install` / `Patch Update` / `Self Update` / `Restore` 使用**明确的操作锁**。

### 28.3 游戏运行状态（v5 §75）

**禁止在目标游戏运行时覆盖 `DLL` / `ASI` / `INI`**：

```
检测游戏进程 → 运行中 → 要求退出 → 确认进程结束 → 执行修改
```
除非某 Provider 明确支持运行时操作。

### 28.4 Safe Mode（v5 §83）

触发场景：配置损坏 / 上次更新未完成 / Provider metadata 损坏 / UI 启动异常 / 数据迁移失败。
Safe Mode 至少允许：**查看日志 · 恢复配置 · 恢复上一版本 · 重置缓存 · 禁用远程 Provider metadata**。

### 28.5 网络异常与离线（v5 §84）

> **GitHub 不可用不能导致软件无法启动。**

`timeout` / `rate limit` / `DNS failure` / `proxy unavailable` ⇒ **使用缓存 → 正常进入程序 → 显示「更新检查失败」**，而不是阻塞启动。
**系统代理**：优先尊重 Windows 系统代理，不自行实现复杂代理；不可用时给出明确诊断。

### 28.6 Rate Limit（v5 §85）

GitHub API 请求**集中管理**，避免「每个游戏 / 每个 Provider / 每个页面」重复请求。

要求：`GitHubClient` + `ReleaseCache` + `Request Deduplication` + `CancellationToken`。

> 与 §7.4 的既有设计一致，本节补充**去重**与**统一客户端**要求。

---

## 29. 安全与隐私增强（v5 §81、§86、§87、§88）

### 29.1 Provider Metadata 安全（v5 §81）⭐

若采用 `providers.json` / `compatibility.json` / `recipes.json` 远程更新，这些文件必须：

```
Schema Version · Source Repository Allowlist · Provider ID Allowlist
File Type Validation · URL Validation · Hash Validation
```

**§81.1 禁止远程元数据执行代码**：远程 JSON **只能描述** `Version` / `URL` / `Hash` / `Compatibility` / `Recipe` / `Metadata`。

**禁止出现**：`PowerShell command` · `CMD command` · `Arbitrary executable arguments` · `Script` · `Dynamic code`。
> **远程 metadata 不能成为远程代码执行入口。**

### 29.2 隐私与日志脱敏（v5 §86）

日志与诊断包**不得**暴露：`Windows 用户名` · 完整用户目录 · `Steam 登录名` · `Token` · `Cookie` · 私人文件路径。
诊断导出时须处理为 `%USERPROFILE%\...` 形式。

> **对既有设计的调整**：§12 的诊断包只要求「不含补丁二进制」；现增加**路径与身份脱敏**。

### 29.3 下载与临时文件清理（v5 §87）

统一管理 `Download Cache` / `Staging` / `Backup` / `Temp`；成功后清理无用临时文件；失败时保留必要诊断文件**但设置生命周期**。
**避免「每更新一次留下几百 MB / 几 GB」。**

### 29.4 Backup 生命周期（v5 §88）

Backup **不能无限增长**：每个游戏保留最近 **N 个恢复点**，或按**大小上限**。
**删除前必须确认不再被当前安装记录引用。**

---

## 30. Provider 治理（v5 §63、§90、§91）

### 30.1 Runtime Update Discovery（v5 §63）

**禁止**「程序发布时是什么版本，以后就一直是什么版本」。

即使 Manager 半年不更新，只要上游补丁发布新版，客户端**仍应能发现** ⇒ **补丁更新规则不能依赖重新编译 Manager**。
每个 Provider 负责自己的 `Release parsing` / `Asset selection` / `Version comparison` / `Compatibility check`（v5 §63.1）。

### 30.2 Provider 远程元数据（v5 §63.2，可选但推荐）

从本项目 GitHub 获取 `providers.json` / `compatibility.json`，使「上游改 Asset 名 / 改 Tag 规则 / 增加新版驱动支持」**有时可只更新元数据而无需发布新 Manager**。
远程元数据必须 `Versioned` / `Validated` / `Fail-closed`（安全约束见 §29.1）。

### 30.3 Provider 健康状态（v5 §90）

```
Available · Unavailable · Rate Limited · Release Format Changed
License Restricted · Deprecated · Broken
```
**某个 Provider 挂了不得拖垮整个程序**（与 §6.4 的结构变化检测衔接）。

### 30.4 Feature Flag（v5 §91）

高风险或未完全验证的功能使用 `Feature Flag` / `Experimental Toggle`，例如 `Experimental Vulkan` / `Experimental Driver Profile` / `Prerelease Patch`，**默认关闭**。

---

## 31. UI 与文档（v5 §92、§93、§94、§95）

### 31.1 统一状态模型（v5 §92）

整个 UI 使用同一套状态词，**不同页面不得对同一状态用不同词**：

```
Up To Date · Update Available · Pinned · Experimental · Unsupported · Unknown
Installed · Loaded · Verified · Error · Rollback Available
```

> **与既有设计的对齐**：§5.3 定义了 `Installed / Loaded / Requested / Applied / Verified` 的**技术判定链**（保留）；
> §31.1 是**面向用户展示**的统一词表。两者关系：技术链是内部真值，UI 词表由其映射得出，且**必须保留 `Unknown` 与 `Error`**。

### 31.2 首次启动向导（v5 §93）

```
检测 GPU → 检测 Driver → 扫描 Steam → 更新 Provider metadata → 显示主页
```
**不要第一次打开就弹几十项高级设置。**

### 31.3 README 与文档分离（v5 §94、§95）

- `README.md` **面向普通用户**：项目是什么 / 支持什么 / 不支持什么 / 如何安装 / 如何更新 / 如何配置游戏 / 如何恢复 / 风险说明 / **Anti-Cheat 警告** / FAQ / 日志位置 / Release 下载方式
- **开发者文档分离**：`docs/ARCHITECTURE.md` · `docs/PROVIDERS.md` · `docs/UPDATE_SYSTEM.md` · `docs/COMPATIBILITY.md` · `docs/SECURITY.md`
- **避免 README 变成几万字开发笔记**

> ⚠️ 注意：本项目现有的 `docs/ARCHITECTURE_PLAN.md` 与 `docs/RESEARCH_NOTES.md` 属**开发文档**，最终须按 §95 命名/拆分（见 §35 阶段计划）。

---

## 32. 交付与验收（v5 §59、§60、§66、§67、§68、§96、§97）

### 32.1 GitHub 最终交付（v5 §59）

**「只在本地完成不算交付。」** 开发完成、核心测试通过后，**必须推送到用户指定的 GitHub 仓库**。

| 项 | 要求 |
|---|---|
| **必须上传** | 完整源代码 · 项目文件 · 构建配置 · `README.md` · `docs/` · 测试代码 · `.gitignore` · `LICENSE` · `THIRD_PARTY_NOTICES.md` · 兼容性数据库 · 更新 Provider 配置 · 必要脚本 |
| **禁止上传** | `API Key` · `Access Token` · `GitHub Token` · 密码 · 本地隐私文件 · 日志敏感信息 · 临时下载缓存 · `bin/` · `obj/` · **第三方专有补丁二进制** · 无权再分发的 `DLL`/`ZIP`/`EXE` |
| **推送流程** | 运行完整测试 → 确认工作区 → 整理 Commit → **检查敏感文件** → Fetch 远端 → 处理远端变化 → Push → 再次确认内容完整 |
| **禁止** | `git push --force`（除非用户明确授权）；**不得为省事覆盖用户已有提交** |
| **分支** | 默认 `main`；已有规范则遵守；大重构可先 `dev` / `feature/*`，**不得无理由创建大量分支** |
| **Commit** | 按逻辑模块组织（`refactor:` / `fix:` / `feat:` / `ui:` / `test:` / `docs:`）；**禁止** `update` / `fix` / `final` / `123` / `test2` |
| **交付报告** | `Repository` · `Branch` · `Final Commit` · `Tests` · `Build` · `Known Issues` · `Release Status` |

**仓库地址**（v5 §59.4）：若任务开始已提供则直接使用；**若未提供，不得自行创建陌生仓库或推送到开发者账号** —— 在最终上传前向用户确认 `GitHub repository` 与 `Target branch`。
> ⚠️ **当前该地址尚未提供**，见 §34 待决策项。

### 32.2 最终结束条件（v5 §60 + §68）

13 项基础条件（Phase 0 调研 → 架构计划 → 核心功能 → 自动/手动补丁更新 → DLSSG Provider → Smooth Provider → Profile 自动配置 → 安装/更新/回滚 → 安全验证 → 核心测试 → 文档 → **GitHub 推送成功** → **报告最终 Commit SHA**），
外加 §68 补充：`第三方补丁自动更新` · `第三方补丁手动更新` · `本软件自动检查更新` · `本软件手动检查更新` · `本软件 Self Update` · `GitHub Release 发布流程` · `Updater 回滚`。

**只有「能更新第三方补丁但本软件不能自更新」，或「能自更新但补丁版本被写死」，均不得判定完整完成**（v5 §68）。

### 32.3 新增验收项（v5 §96）

| 类别 | 验收内容 |
|---|---|
| **Compatibility** | Latest Available / Latest Compatible 正确区分；不会自动安装不兼容版本；Version Pin 正常；Rollback 正常 |
| **Reliability** | 崩溃后 Operation Journal 可恢复；多实例不会同时改游戏；游戏运行中不会危险覆盖 DLL；**GitHub 离线仍可启动** |
| **Data** | Schema migration 正常；自更新不会清空游戏记录；Safe Mode 可进入 |
| **Validation Honesty** | 自动测试结果与真实硬件验证状态**严格分开**；未经实机验证**不得标记 `Project Verified`** |
| **Privacy** | 日志无 Token；诊断包路径脱敏 |

### 32.4 最终原则（v5 §97）

```
能自动化，但不瞎自动化
能保持最新，但不盲目追最新版
能自动安装，但不覆盖未知文件
能自动更新，但必须可回滚
能给出状态，但不能虚报验证结果
```
凡涉及 `兼容性` / `版本选择` / `安装模式` / `NVIDIA Profile` / `实际生效状态` 的判断，都必须有 **`Evidence` · `State` · `Fallback` · `Rollback`**，而不是仅仅「代码能跑」。

---

## 33. **实机验证边界**（v5 §76、§77）⭐【防止虚报成功】

### 33.1 八级状态必须严格区分

```
Build Passed → Unit Tests Passed → Integration Tests Passed
→ Files Installed → Module Loaded → Feature Requested → Feature Applied
→ Real-Game Verified
```

**自动化测试通过 ≠ 实机功能已验证。**

### 33.2 本机的实机验证条件（**本次实测**）`[E1]`

| 项 | 实测值 | 对验证能力的影响 |
|---|---|---|
| GPU | **NVIDIA GeForce RTX 3070 Ti Laptop GPU** | ✅ 正是任务书 §39 示例机型，**具备 SM86 实机验证条件** |
| 驱动 | **617.14**（`32.0.16.1714`） | ✅ MFG 专门为 617.14 出过修复包（`SmoothMotion-2.8.2-R3-GP9-Driver-61714.zip`）⚠️ 但**不在 xikarioz 白名单内**（其 golden 为 616.64，617.14 更新于明确未授权的 616.92） |
| 显存 | 8192 MiB | 帧生成显存增量 1440p ≈ 540 MiB，余量充足 |
| **HAGS** | **已启用**：`HwSchMode = 2`（`HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers`，枚举 `1`=关 / `2`=开） | ✅ 调研确认 `hags.state=off` 是帧生成不生效的首要原因，**本机已满足该前置条件**；检测策略见 §36.1（**禁止**由「键缺失」推断「关闭」） |

### 33.3 报告纪律（v5 §76）

DSH 环境具备 RTX 30 GPU，因此**不得**再以「无硬件」为由跳过实机验证；但**必须**区分：
- 可报告：`Implementation Complete` / `Automated Tests Passed`
- **仅在真实游戏实测通过后**才可报告：`Real-Game Verified` / `Project Verified`

**用户实机验证流程**（v5 §77）：启动游戏 → 等待 Renderer 进程 → Manager 检查模块状态 → 检查日志 → **用户确认画面/FPS 行为** → 才能保存 `User Verified`。

> **与 §3.5 的衔接**：运行验证须**多信号交叉**（补丁日志 + 进程模块 + **`0xB01B8B02` Debug Bars 可视化确认** + 兼容库历史），任何单一信号都不得直接判定 `Verified`。

---

## 34. v5 新增风险与待用户决策

| ID | 项 | 影响 | 需要 |
|---|---|---|---|
| **R-14** | ~~**本机 HAGS 未启用**（`HwSchMode` 不存在）~~ → **已消解**（2026-09-26 更正：`HwSchMode = 2`，HAGS **已启用**） | 该项原判为帧生成不生效的首要原因；**本机已满足前置条件**。仍需保留检测与提示（用户机器状态未必相同） | **无需用户操作**；实机验证时仅需确认运行时生效（`ConfiguredOn` ≠ 已证明 `Enabled`） |
| **R-15** | ~~**GitHub 仓库地址未提供**~~ → **已消解**（仓库 `PLA0185/RTX30-FrameGen-Manager`，PUBLIC，默认分支 `main`） | v5 §59 的推送要求已满足：基线提交 `b0a6424` 已推送，本地与远端 SHA 一致；`origin`=用户仓库、`upstream`=BUNNY 原仓库 | **无需用户提供**；后续提交按 `origin` 推送，**禁止** `--force` |
| **R-01** | ~~本机无 .NET SDK~~ → **已消解**（.NET 8 SDK **8.0.425** 已安装） | Stage 2 开发不再被工具链阻塞 | **无需决策**；构建与测试均已实测通过 |
| **R-16** | Self Update 依赖本项目自己的 Release 流程（GitHub Actions + Tag） | 无仓库则无法建立发布链路 | 与 R-15 同解 |
| **R-17** | 驱动 617.14 相对社区白名单偏新 | xikarioz 类 Provider 会 `fail-closed`（设计如此）；MFG 则有专门支持 | 实机验证时**记录精确驱动指纹**入兼容库 |
| **U-12** | RTX 30 上驱动是否**实际执行**这些 DRS 设置（无任何来源记录） | 决定 Profile 写入能否真正生效 | **本机实机自测**（现已具备条件） |

---

## 35. 修订后的开发阶段（v5 §53 + 新增要求）

**Stage 1–11 保持 v5 §53 的划分**，按 v5 新增要求补入以下约束：

| Stage | v5 增量约束 |
|---|---|
| **Stage 2** | 同时建立 `.gitignore`、`LICENSE`、`THIRD_PARTY_NOTICES.md`（§59.1）；按 §80 配置 `origin`/`upstream` remote，**不自动合并上游** |
| **Stage 3** | Provider 需记录**统一元数据**（许可证 / 是否写 profile / 是否需管理员 / 是否触碰游戏进程）；实现 §30.3 的 Provider 健康状态 |
| **Stage 4** | 拆分为 `SelfUpdateService` + `PatchUpdateService`（§23.1）；实现 §24 的 Latest Available/Compatible 与 Version Pin；§28.6 的请求去重 |
| **Stage 5** | 兼容性矩阵扩展为 **12 维**（§25）；引入 **Install Recipe**（§26） |
| **Stage 6** | Profile 写入前必须建立 **Operation Journal** 条目（§28.1） |
| **Stage 7** | Provider 实现 §30.1 的 Runtime Update Discovery |
| **Stage 8** | **实机验证前置**：按 §36.1 检测并**如实报告** HAGS 状态（本机为 `ConfiguredOn`，**不得**把配置层事实写成运行时已生效）；按 §33 的报告纪律区分「自动测试通过」与「实机验证通过」 |
| **Stage 9** | Recipe 证据字段 + **8 档验证等级**（§26.1、§26.2） |
| **Stage 10** | 统一 UI 状态词表（§31.1）；首启向导（§31.2）；README 面向用户（§31.3） |
| **Stage 11** | 增加 v5 §66 的 Self Update 测试组与 §96 的验收项 |

**新增收尾阶段**：

| Stage | 内容 |
|---|---|
| **Stage 12** | **交付与推送**（v5 §59）：完整测试 → 敏感文件检查 → Fetch/合并远端 → Push → 输出交付报告（`Repository`/`Branch`/`Final Commit`/`Tests`/`Build`/`Known Issues`/`Release Status`） |
| **Stage 13** | **发布链路**（v5 §61.8、§65）：GitHub Actions 工作流（Tag `v*` → restore/test/publish/package/SHA256/Release），**测试失败禁止发布** |

**开发纪律**（用户本轮明确要求）：
```
先查证 → 设计 → 实现 → 测试 → 验证 → 再进入下一阶段
```
**禁止**「先随便做一个，出错后再反复修」。

---

## 36. v5 增量设计（二）：HAGS 检测策略

> 起因：本文件早期版本与 `RESEARCH_NOTES.md` 均曾把 HAGS 记为「未设置（`HwSchMode` 不存在）」，实测更正为 `HwSchMode = 2`（已启用）。该错误暴露的是**检测模型缺陷**，故在此固化设计。

### 36.1 `HagsDetectionService` 状态模型（七态，**禁止**二元化）

| 状态 | 判定依据 | 对外措辞 |
|---|---|---|
| `Unsupported` | 硬件/驱动能力层明确不支持 HAGS | 「你的设备不支持」 |
| `SupportedDisabled` | 能力具备，配置层 `HwSchMode = 1` | 「已关闭（建议开启）」 |
| `ConfiguredOn` | 配置层 `HwSchMode = 2`，**尚无运行时证据** | 「已开启（配置层；需实测确认生效）」 |
| `ConfiguredOnRebootRequired` | `HwSchMode = 2` 且存在待重启标志 | 「已开启，重启后生效」 |
| `Enabled` | 配置层 + **运行时证据**同时成立 | 「已开启且已生效」 |
| `Unknown` | 探测失败（权限不足 / API 不可用 / 异常） | 「无法确定，请手动确认」 |
| `ConflictingSignals` | 多信号互相矛盾（如注册表=2 而运行时报告不可用） | 「检测结果冲突」 |

**信号优先级与实现约束**：
1. **能力层**（GPU + WDDM 版本）→ 决定 `Unsupported` / `SupportedDisabled` 是否可达；
2. **配置层**：`HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode`（`1`=关 / `2`=开）。**必须区分三种返回**：值存在 / 值缺失 / 读取失败（`access denied`、异常）。**缺失与失败都不得推断为「关闭」**；
3. **重启层**：待重启标志 → 升格为 `ConfiguredOnRebootRequired`；
4. **运行时层**：**仅在存在受支持的官方 API 时**采用；**不得**依赖任何标为 "Reserved for system use" 的内部接口。

**红线**：
- **禁止**由「`HwSchMode` 缺失」推断「HAGS 关闭」（本次错误的根因）；
- 未取得运行时证据前，对外**一律**返回 `ConfiguredOn`，**不得**宣称 `Enabled`；
- 所有失败路径必须落入 `Unknown` / `ConflictingSignals`，**绝不静默降级为 `Disabled`**；
- 探测须记录原始证据（注册表值、读取返回码、能力探测结果）进诊断日志，便于事后复核对错；
- UI 文案中 `ConfiguredOn` 与 `Enabled` **不得混用**（v5 §31.1 统一状态词表）。

**进入 Stage 2 前的待查证项**：微软是否提供**受支持的桌面端** HAGS 状态查询 API。查证结论若为否，则运行时层留空，服务常态返回 `ConfiguredOn`，如实标注为「未验证运行时生效」。

### 36.2 与上游经验的关系
上游「`hags.state=off` 是帧生成不生效的首要原因」仍作为排查项**保留在代码与文案中**（用户机器状态未必与本机相同），但本机已满足该前置条件，不再是阻塞项。

---

## 37. v5 增量设计（三）：代理部署策略（Proxy Strategy）

### 37.1 上游证据（`[E1]`，存在内部矛盾）

| 来源 | 说法 |
|---|---|
| `alternatives/README.md:3` | release 包**根目录随附四个**工具代理（`version.dll` / `winmm.dll` / `dbghelp.dll` / `dinput8.dll`），「把根目录的文件**全部**复制到渲染 EXE 旁即可，**不需要挑**」 |
| `dlssg_sm86.ini:2-4` | 只部署 `version.dll`（或其中一个替代品）；「第一个被游戏加载的会运行 mod，其余只做转发，所以不会出问题——**但一个就够了**」 |
| `docs/INSTALL.md:33` | `dxgi.dll` / `d3d12.dll` 属**热路径 / 加载顺序敏感**，位于 `alternatives\`，仅当四个根代理都未被加载时才**手动复制一个** |
| `docs/INSTALL.md:319` | 签名涉及根目录四个代理 + `alternatives\*.dll` |
| **实测 git 仓库根目录** | **只有 `version.dll` 一个** |

> **结论**：「根目录四个」与「实际只有一个」不一致；「一个够用」与「全部复制」两种说法并存。**单代理与多代理的等价性未经验证** → 标记 **`Unresolved / Needs Validation`**，**不得**据此写死实现。

### 37.2 两种部署形态

| 形态 | 机制 | 优点 | 代价 |
|---|---|---|---|
| **Direct Proxy** | 以系统 DLL 名（`version.dll` 等）旁路加载，游戏自身加载即生效 | 无需额外加载器；对用户零配置 | 受加载顺序与游戏保护模块影响；同名文件易冲突 |
| **ASI** | 依赖 ASI Loader 前置加载 | 与 Loader 生态兼容；MFG 提供该形态 | 需用户已装 Loader；兼容性由 Loader 决定 |

### 37.3 候选策略

| 策略 | 内容 | 定位 |
|---|---|---|
| **A（默认，最小侵入）** | 仅部署**单一** `version.dll` + `dlssg_sm86.ini` | 首选；符合 ini 的「一个就够了」 |
| **B（多入口冗余）** | 部署根目录四代理 | 仅当 A 在目标游戏中未被加载时启用 |
| **C（敏感入口兜底）** | 从 `alternatives\` 取 `dxgi.dll` **或** `d3d12.dll`，**一次只放一个** | 最后手段（热路径敏感） |

### 37.4 选择依据（依赖维度，不得写死）
渲染 API（D3D12 / Vulkan）、商店与启动方式、游戏保护模块是否抢占代理名、渲染 EXE 定位是否成功、目标目录既有同名文件、用户是否已装其他代理。

### 37.5 强制约束
- 同一目录**禁止**同时放置多个热路径代理（`dxgi.dll` 与 `d3d12.dll` 互斥）；
- 部署前记录目标目录**既有同名文件**（哈希 + 来源），回滚须**逐文件精确恢复**，不得整目录删除；
- 部署与回滚动作**必须**写入 Operation Journal（§28.1）；
- 在最小真机实验完成前，**不得**在代码或文案中声称「多代理更稳」或「一个就够」。

### 37.6 最小真机实验（验证 A/B/C 等价性）

| 项 | 内容 |
|---|---|
| **自变量** | 部署策略 A / B / C（同一游戏、同一补丁版本、同一驱动 617.14、同一 Profile 状态） |
| **观测量** | ①补丁日志是否出现 `runtime_redirect`；②进程模块枚举中代理是否被加载；③`0xB01B8B02` Debug Bars 可视化；④FG 菜单是否出现；⑤用户主观画面/FPS 确认 |
| **判定** | 三种策略观测量**一致** ⇒ 等价性成立（可固定 A 为默认）；**不一致** ⇒ 记录差异场景（API / 启动方式 / 保护模块），写入兼容性矩阵 |
| **前置** | 需先完成 Stage 2–7；实验属 **Stage 8 实机验证**范畴，**自动测试通过不能替代本实验** |

---

## 38. Stage 2 实现记录（2026-09-26）

> 基线 `c3cc868`。本节只记录 Stage 2 **实际**做了什么、与原设计的差异、以及仍未验证的部分。

### 38.1 先核查既有实现（避免按任务书关键词重写）

| 项 | 核查结论 | 依据 |
|---|---|---|
| `DeploymentService` 的签名校验 | **已正确实现，不重写** | `DeploymentService.cs:146-213`：`WinVerifyTrust`；`CERT_E_UNTRUSTEDROOT` 视为完整（容纳自签名），`TRUST_E_BAD_DIGEST` 拒绝，未知码 fail-closed，正确执行 `STATEACTION_CLOSE` |
| `Deploy` 的既有防护 | **已正确实现**：游戏运行检查、内核反作弊阻止、可写检查、入口占用拒绝、`*.dlssgtmp` + `Move` 原子替换、失败清理临时文件、外部 INI 备份、`LooksLikeProjectIni` 内容判定 | `DeploymentService.cs:373-591` |
| 代理冲突（指令 §4.4） | **已满足**：`PickFreeProxy` 只挑空位，`proxyTaken` 拒绝覆盖，未知文件既不覆盖也不删除 | `:341-364`、`:428-434`；测试组「五个入口名全被占用」「保护其他 Mod 的文件」 |
| 反作弊文案（指令 §4.5） | **合规**：仅陈述「未检测到反作弊组件」，无「安全」类表述，代码中不存在任何绕过/关闭/规避能力 | `Strings.zh.cs:252`、`AntiCheat.Summary` |

**结论：Stage 2 的真实缺口只有两处** —— 下载路径的签名链、以及备份/事务。其余均为既有正确实现，未改动。

### 38.2 实际改动

| 缺口 | 修复 |
|---|---|
| **下载路径不校验签名完整性**：`ModFetcher.Verify` 只用 `X509Certificate.CreateFromSignedFile` 读证书（该方法**只提取证书、不校验签名是否仍覆盖文件**） | 新增 `DeploymentService.ProbeSignature(path)` → `SignatureStatus{Intact,NotSigned,BadDigest,Unknown}`；`Verify` 改为**先验完整性、再看证书**；`BadDigest`（篡改）**在任何来源上都被拒绝**并单独报告（不再与「无签名」混为一谈） |
| `Restore` 不校验备份哈希 | 还原前重算 SHA-256 与记录比对；不符则**拒绝还原、保留现场、报告** `Restore.BackupHashMismatch`；记录缺哈希同样拒绝（fail-closed） |
| `Restore` 无路径逃逸防护 | 新增 `IsSafeBackupTarget`：拒绝分隔符 / 盘符 / `..`，并确认解析后的完整路径确实位于渲染目录内 |
| 部署非事务：proxy 写成功而 INI 失败时留下半成品 | 写入前对**将被覆盖的文件**做事务快照；失败时回滚（原本不存在 → 删除本次写入；原本存在 → 恢复快照），每条回滚结果都报告，回滚不了也明确报告而不是沉默 |

### 38.3 与原设计的差异（有意偏离，非遗漏）

- **未拆分程序集**。§35 曾提出「先抽取 Core 再拆程序集」。本轮改为**在现有程序集内抽取可测试的核心**：把 `private IsSignatureIntact` 泛化为公开的 `ProbeSignature`，使验证原语可被直接单测，而不做物理拆分。理由：程序集拆分不在 Stage 2 范围（指令 §3）内，且会引入与安全修复无关的回归面。
- **发布摘要校验只做到「有值才校验」**。`Verify` 新增可选 `expectedSha256` 参数并接入 `MatchesPin`，但**当前没有来源提供该值**：主源 `sdli1995/dlssg_for_sm86` 走 git 树逐文件下载，其 7 个 Release **全部 `assets=0`**；GitHub 的 `digest` 字段只对 Release **Asset** 生效（已在 `_research/api/*.json` 核实：MFG / xikarioz / RHI 的 Release 均带 `digest: sha256:...`）。故对当前主源，**签名完整性是唯一可用的内容校验**。

### 38.4 已验证 / 未验证

**已验证**
- `dotnet restore` 无错误；`dotnet build -c Release` → **0 警告 / 0 错误**
- Harness 回归套件 → **362 通过 / 0 失败 / 10 跳过**（基线 `c3cc868` 为 343/0/10，新增 19 项检查）
- 新增测试**先在旧代码上失败**（3 项红：篡改备份被还原、失败后代理残留、非 PE 判级），修复后转绿 —— 测试确实捕获了缺口，而非事后补写

**未验证 / 不在本轮范围**
- **真实下载流程未端到端跑通**（需联网与 ~180 MB payload）；`Verify` 的新分支由 `ProbeSignature` 单测间接覆盖
- 10 项跳过测试仍需 Mod 文件（`Harness --fetch`）
- 无实机游戏验证（属 Stage 8）
- **`dotnet test` 在本仓库不适用**：解决方案内没有 VSTest 测试项目，实际测试载体是 `test/Harness` 控制台程序

### 38.5 Remaining Risks

| ID | 风险 | 说明 |
|---|---|---|
| **S2-01** | 主源不提供发布摘要，无法做内容级哈希校验 | 只能依赖 Authenticode 完整性 + 固定指纹；若上游更换证书，官方源仍会放行（**既有设计，本轮未改**） |
| **S2-02** | 事务回滚**不覆盖** legacy schema 分支下删除的冗余代理 | 这些文件在 mod 源目录中仍存在，可重新部署恢复；已如实记录而非隐藏 |
| **S2-03** | `Verify` 新逻辑缺少端到端下载验证 | 需联网抓取真实 payload 才能覆盖 |
| **S2-04** | 快照会占用 restore 目录额外空间 | 仅对「本次将覆盖的文件」快照（通常 0–2 个小文件），成功或失败后都会清理 |

---

## 39. Stage 3 实现记录：Provider Framework（2026-09-26）

> 基线 `c553af3`。本节只记录实际做了什么、与设计的偏差、以及仍未验证的部分。

### 39.1 先画调用链，再抽象

审查后确认的真实职责归属（避免先造空接口再倒推）：

| 阶段 | 现有负责者 | 位置 |
|---|---|---|
| 上游来源 | `ModFetcher.Sources`（raw / ghproxy / jsdelivr / ghfast / codeload / zipball 六源 + 自动顺序） | `ModFetcher.cs:214-250` |
| 版本识别 | `ModFetcher.DetectLatestVersionAsync`（读上游 README）+ `ModSource.ReadVersion`（payload 自带横幅）+ 版本标记文件 | `ModFetcher.cs:152`、`ModSource.cs:96-113` |
| 下载 | `ModFetcher.DownloadIntoAsync`（公开入口；内部 staging → Verify → Publish） | `ModFetcher.cs:571` |
| 校验 | `ModFetcher.Verify`（Stage 2 加固的完整链）+ `DeploymentService.ProbeSignature` | `ModFetcher.cs:817+` |
| 安装计划 | **没有独立计划层**：`PickFreeProxy` + `IniTemplate.Render` 直接进入 `Deploy` | `DeploymentService.cs:341`、`:437` |
| 部署 / 恢复 | `DeploymentService.Deploy` / `.Restore` | `:373`、`:609` |
| UI 入口 | `MainWindow.Actions.cs`（部署/恢复/批量）、`MainWindow.xaml.cs`（下载） | — |
| 状态保存 | `GameEntry.Deployment` 经 `LibraryStore` 序列化 | `Models.cs:138-158` |

**结论：现有类型已能表达 Provider 语义，因此没有制造第二套数据模型。**

### 39.2 Provider Contract（职责等价，非照抄）

`src/DLSSGManager/Providers/ProviderContracts.cs`：

- `IPatchProvider`：`Id` / `Metadata` / `Health` / `CheckLatestAsync` / `GetInstalledVersion` / `DownloadAsync` / `VerifyPackage` / `Install` / `Restore`
- 结果**复用现有 `OpResult`**，游戏**复用 `GameEntry`**，载荷**复用 `ModSource`** —— 未新增 `InstallResult` / `GameInfo` 一类平行模型
- `IPatchDownloader`：把静态 `ModFetcher` 置于接口之后，使「迁移未绕过安全路径」可以离线测试

**与 §5 目标接口的差异（有意）**：目标接口中的 `BuildInstallPlan(...)` 未落成独立类型——现有架构里根本没有计划层，`Deploy` 一步完成「选入口 + 渲染 INI + 写文件」。另造一个平行计划模型会产生两套真相。`Install(game, source, allowProtected)` 承担同等职责。

### 39.3 Distribution Models

`DistributionModel { GitTree, ReleaseAsset }`。

当前 Provider 为 **`GitTree`**（7 个 Release 全部 `assets=0`，二进制在 git 树内）。框架层面两种模型都能表达，Stage 7 的 MFG 类 Provider 可直接以 `ReleaseAsset` 接入，无需推翻本层。

### 39.4 Provider Metadata

`ProviderMetadata`：Id / DisplayName / UpstreamRepository / Distribution / License / LicenseNote / WritesNvidiaProfile / RequiresAdministrator / TouchesGameProcess / Experimental。

`LicenseClass` 六类（`Mit` / `Gpl3` / `ProprietaryEula` / `NoLicenseDeclared` / `ReferenceOnly` / `Unknown`）——**不是 `OpenSource` 布尔**；`RequiresAdministrator` 使用三态 `TriState`。

`DlssgSm86Provider` 的取值**全部来自已查证事实**：

| 字段 | 值 | 依据 |
|---|---|---|
| UpstreamRepository | `sdli1995/dlssg_for_sm86` | Phase 0 §1 |
| Distribution | `GitTree` | 7 个 Release 全 `assets=0` |
| License | `NoLicenseDeclared` | 仓库**无 LICENSE 文件**；README 自称 GPLv3。README 声明不是许可授予，故不归为 `Gpl3`；`LicenseNote` 同时保留两侧事实 |
| WritesNvidiaProfile | `false` | 现有安装流程只写代理 DLL 与 INI |
| RequiresAdministrator | `Conditional` | 写游戏目录通常不需要；DRS 权限官方未声明（属社区经验），须运行时探测 |
| TouchesGameProcess | `true` | 代理 DLL 由游戏加载 |
| Experimental | `true` | 任务书 §10.5 / §10.7 |

### 39.5 Provider Health 与 Registry

`ProviderHealthState` 七态：`Available` / `Unavailable` / `RateLimited` / `ReleaseFormatChanged` / `LicenseRestricted` / `Deprecated` / `Broken`；`ProviderHealth` 携带 `Reason`（不是裸枚举）。

**隔离边界**：`ProviderRegistry.GetHealth` 对每个 Provider 单独 try/catch，抛异常者报 `Broken` 且不影响其他；`CreateDefault()` 注册过程**不做任何 I/O**，因此单个不可用 Provider 不会阻止其他 Provider 构造。

`RateLimited` 与 `Unavailable` 是**不同状态**；当前分类依赖异常文本启发式（见 S3-02）。

**Registry 规则**：重复 ID **明确拒绝**（`TryRegister` 返回 false + 原因，`Register` 抛异常）；未知 ID 返回 **null 而不回退**到其他 Provider；不引入插件加载器、动态代码执行或远程程序集加载。

### 39.6 DlssgSm86Provider 迁移方式

采用指令 §11 的**方案 A**（`ModFetcher` 保留为底层共享服务，Provider 组合它）：

```
DlssgSm86Provider
    ├─ IPatchDownloader → ModFetcher（下载 + 完整验证链，Stage 2 已加固）
    └─ DeploymentService（事务部署 / 恢复 / 签名原语）
```

**Provider 内没有一行安全逻辑副本**：`Install` → `Deploy`，`Restore` → `Restore`，`VerifyPackage` → `ProbeSignature`。

**UI 最小接入**：`MainWindow.Actions.cs` 的 4 个调用点（单游戏部署/恢复、批量部署/恢复）由直接调用 `DeploymentService` 改为经由 `Providers.AppProviders.Patch.Install / Restore`。**行为完全等价**（同一委托链），未改动任何布局或导航；下载 UI 仍直接调用共享下载服务（属后续 Stage）。

### 39.7 已验证 / 未验证

**已验证**
- `dotnet build -c Release` → **0 警告 / 0 错误**
- Harness → **408 通过 / 0 失败 / 10 跳过**（基线 362/0/10，新增 46 项；**原有测试未删除、未减少**）
- 新增覆盖：Registry 12 项 · Metadata 12 项 · Health 4 项 · 故障隔离 4 项 · 迁移行为一致性 12 项 · 应用级入口 2 项
- **回归**：Stage 2 全部检查继续通过（签名完整性、备份哈希、事务回滚）
- `dotnet test` → exit 0、无输出（仓库无 VSTest 项目，如实记录，未伪造测试数量）

**未验证 / 不在本阶段范围**
- 未对真实网络运行 `CheckLatestAsync`；`DownloadAsync` 的委托关系由 fake seam 验证
- 未做实机游戏验证（属 Stage 8）
- 未实现 Stage 4 的更新系统（Latest Available/Compatible、Version Pin、Release Cache、请求去重）

### 39.8 Remaining Risks

| ID | 风险 | 说明 |
|---|---|---|
| **S3-01** | Provider Metadata 目前是编译期常量 | 远程元数据更新属后续设计；**远程 JSON 不执行代码**这一红线不变 |
| **S3-02** | `RateLimited` 与 `Unavailable` 的区分依赖异常文本启发式 | 精确区分需下载层暴露 HTTP 状态码；已在代码注释与本节标注为启发式，未写成可靠判定 |
| **S3-03** | UI 仅 4 个调用点接入 Provider | 下载路径与状态检查仍直接调用共享服务，属有意的分阶段接入 |
| **S3-04** | `Install` 未使用独立 InstallPlan | 与现有架构一致的有意选择；若将来引入计划层，需连同 `Deploy` 一起重构 |

---

## 40. Stage 4 实现记录：UpdateService（2026-09-26）

> 基线 `8cc5c14`。本节只记录实际做了什么、与设计的偏差、以及仍未验证的部分。

### 40.1 先画调用链（结论：本阶段是新建，不是改造）

审查后确认：**现有代码里没有 Release API 客户端、没有缓存、没有请求去重、没有 Pin/Hold**。唯一的"版本发现"是 `ModFetcher.DetectLatestVersionAsync`——读 payload 自带的 INI 横幅，其次读上游 README 标题（两次 raw GET，均无缓存、无 ETag）。

| 现有能力 | 归属 | Stage 4 处置 |
|---|---|---|
| 版本探测（INI/README） | `ModFetcher.DetectLatestVersionAsync` | **保留**，作为 Provider 的探测回退 |
| 下载 + 验证链 | `ModFetcher.DownloadIntoAsync` | **不动**（Stage 2 已加固） |
| 部署/恢复/事务 | `DeploymentService` | **不动**，Update 层不复制 |
| 更新检查 / 缓存 / 去重 / Pin | — | **本阶段新建** |

### 40.2 两套服务严格分离（非 `if (self)`）

```
Update/
├─ SelfUpdateService   目标=本管理器自身；源=正式 GitHub Release（非 main 分支提交）
└─ PatchUpdateService  目标=第三方补丁；经 ProviderRegistry 查 Provider
```

共享（§26）：`IGitHubReleaseClient`、`ReleaseCache`、`ReleaseEntry` 模型、`DigestParser`、网络状态归一化、`RequestDeduplicator`。
**不共享**：安装逻辑、回滚逻辑、版本规则、目标对象、Release 选择策略。两者无任何相互调用，故障互不影响（有测试）。

**`Push Commit ≠ Client Update`** 已写入 `SelfUpdateService` 的注释与行为：更新源只认正式 Release。

### 40.3 Release 模型与版本解析

`ReleaseEntry` 携带 `ReleaseId` / `Tag` / `IsDraft` / `IsPrerelease` / `Assets`（含 `AssetId` 与 `DigestSha256`）/ `PublishedAt` —— 身份字段而非只有 tag，因为同一 tag 可以重新上传不同字节。

`ReleaseVersion.Compare` **不假设 SemVer**：两侧都是点分数字才比较，否则返回 `VersionOrder.Unordered`，且 `IsNewer` 对 `Unordered` 一律返回 false。既避免了全局 `new Version(tag)`（MFG 的 tag 无版本语义，会抛异常），也不猜顺序。

**列表顺序不被信任**：候选从全部 Release 中按版本取最大，测试用乱序列表（`0.3.1 / 0.3.5 / 0.2.9`）验证。

`IReleaseVersionResolver` 是**追加**的可选接口，`IPatchProvider` 契约未改动——Provider 若需要自己的 tag 语义就额外实现它。

### 40.4 Latest Available 与 Latest Compatible

两者是 `UpdateCheckResult` 上的两个独立字段。Stage 4 只提供 `UnknownCompatibilitySelector`：一律 `CompatibilityState.Unknown` → `LatestCompatible = null` → **不给推荐目标**，同时 `UpdateAvailable` 仍为 true（如实报告有新版本）。

**不兼容的新版不会成为 `RecommendedVersion`**（有测试）。`SelfUpdateService` 使用独立的 `SelfHostCompatibilitySelector`——管理器自身更新不涉及第三方环境匹配，这是**有理由的判断**，不是"最新即兼容"的默认假设；该判断只适用于 self，不适用于任何 Provider。

### 40.5 Version Pin / Hold

`VersionPolicy` + `PinState { NotPinned, Pinned, Held }`，不用一个 bool 混合语义：
- `Pinned`：仍报告 `LatestAvailable`，但自动目标不得越过 `PinnedVersion`
- `Held`：仍允许检查、仍报告新版本，但不给出任何自动目标
- 状态词区分 `Pinned` / `Held` / `UpToDate` / `UpdateAvailable`

### 40.6 Release Cache

`ReleaseCache`：schemaVersion=1、原子写（tmp + Move）、**损坏即丢弃且不抛**（损坏缓存不得阻止启动）、TTL 30 分钟、`forceRefresh` 绕过、离线时用 stale 缓存并在 reason 中说明来源。

失效判据不看 tag：`IsStaleIdentity` 比较 `ReleaseId` / `AssetId` / `Digest`，覆盖"同 tag 重传""asset 被替换"。缓存落在 `AppPaths.Root`（应用数据层），**不写源码目录、不写游戏目录、不含任何 token**。

### 40.7 Request Deduplication

`RequestDeduplicator<TKey,TResult>`：相同 key 的并发请求合并为一次后端调用；**共享任务不绑定任何调用者的 CancellationToken**，因此一个等待者取消不会取消其他等待者（用 `WaitAsync(ct)` 只作用于自己的等待）。测试覆盖：并发 10 个相同请求 → `BackendCalls == 1`、不同 key 不合并、取消者以取消结束而其他等待者正常拿到结果。

### 40.8 网络与 Rate Limit（结构化，非字符串猜测）

`GitHubReleaseClient` 把响应归一化为 `UpdateNetworkState { Ok, Offline, Timeout, RateLimited, Forbidden, ServerError, Malformed, Unknown }`：
- 429，或 **403 且 `X-RateLimit-Remaining: 0` / 有 `X-RateLimit-Reset`** → `RateLimited`（并解析重置时间）
- 403 但无限流头 → `Forbidden`（**不误判为限流**）
- 5xx → `ServerError`；`HttpRequestException` → `Offline`；超时 → `Timeout`；JSON 解析失败 → `Malformed`

这使 Provider 的健康状态可以基于结构化结果判定，Stage 3 的文本启发式不再是唯一路径。API 不可达时检查以状态返回，**不会阻止程序启动**（有测试）。

> **允许清单未改动**：本阶段只做"发现"，不做 Release Asset 下载，因此不需要 `objects.githubusercontent.com`；该 host 仍留在 Carry-over，未擅自放宽为 `*.githubusercontent.com`。

### 40.9 Digest 模型

`DigestParser` 解析 `digest: sha256:<64hex>`；`Compare(path, published)` 返回 `DigestState { Verified, Mismatch, Unavailable, Malformed }`。
**GitTree 情形**（发布方无摘要）→ `Unavailable`，`IsInstallable = true` 但**绝不伪装成 `Verified`**；`Mismatch` 则 `IsInstallable = false`，拒绝进入安装流程。

### 40.10 Stable / Prerelease

`IsCandidateFor(channel)`：draft **在任何频道都被排除**；Stable 额外排除 prerelease；Prerelease 频道接受两者（回滚时需要）。缓存 key 含频道，Stable 与 Prerelease 的答案不会互相串用。

### 40.11 已验证 / 未验证

**已验证**
- `dotnet build -c Release` → **0 警告 / 0 错误**
- Harness → **486 通过 / 0 失败 / 10 跳过**（基线 408/0/10，新增 78 项，**Stage 2/3 原测试未删未减**）
- 全部为离线测试（fake release client / fake HTTP handler / fake clock / fake compatibility），不消耗真实 GitHub 配额
- `dotnet test` → exit 0、无输出（无 VSTest 项目，如实记录）

**未验证 / 不在本阶段范围**
- 未对真实 GitHub API 运行 smoke test（离线优先；真实可达性未在本轮验证）
- 未实现下载协调之外的安装（Update 层只到"决策 + 下载入口委托"）
- 未做独立 Updater、Release 发布流水线（Stage 13）、兼容性矩阵（Stage 5）、MFG Provider（Stage 7）、UI 改版（Stage 10）
- **未做任何 UI 接线**：Stage 4 完成条件未要求，且"不要大改 UI"优先

### 40.12 Remaining Risks

| ID | 风险 | 说明 |
|---|---|---|
| **S4-01** | 真实 GitHub API 未做 smoke test | 结构由 fake 验证；真实字段若有变化需在联调时确认 |
| **S4-02** | `LatestCompatible` 在当前配置下恒为 null | 这是**有意**的诚实结果（Stage 5 接入矩阵前无兼容性证据），但意味着 Stage 4 结束时自动更新目标不可用 |
| **S4-03** | Provider 侧健康状态仍保留文本启发式 | 结构化状态已可用，但 `DlssgSm86Provider.Classify` 尚未改用它（改动会触及 Stage 3 测试，留待需要时最小调整） |
| **S4-04** | `SelfHostCompatibilitySelector` 的"自身更新即兼容"是设计判断 | 依据是发布流程固定运行时；若将来发布面向不同运行时的构建，此判断需要重审 |
| **S4-05** | Release Cache 无上限清理 | 按 key 覆盖写入，键数量等于 Provider/频道组合，实际不会膨胀；未实现容量淘汰 |

---

## 41. Stage 5 实现记录：Game Detection + InstallPlanner（2026-09-26）

> 基线 `1b00f5d`。本节只记录实际做了什么、与设计的偏差、以及仍未验证的部分。

### 41.1 先确认职责归属（结论：检测层已存在，本阶段是分级与扩展）

审查确认的现状，**没有新建重复实现**：

| 现有能力 | 归属 | Stage 5 处置 |
|---|---|---|
| Steam 库枚举 / appmanifest 扫描 | `Detection.SteamLibraries` / `ScanSteam` | **保留**，商店来源不变 |
| 渲染目录解析 | `Detection.ResolveRenderDir` | **保留**，作为静态启发式的一环 |
| 静态 EXE 挑选 | `Detection.PickMainExe`（黑名单 + 体积排序） | **保留，但降级标注**：它现在只是 `StaticHeuristic` 级证据，不再是结论 |
| 游戏条目 | `GameEntry`（Models.cs） | **复用**，不新建 `GameInfo` |
| 反作弊扫描 | `AntiCheat.Scan` | **复用** |
| 代理入口名 | `ModSource.ProxyCandidates`（6）/ `KnownProxyNames`（7） | **复用**，冲突扫描直接遍历它 |
| 兼容性 seam | `Update.ICompatibilitySelector` | **未改动**，见 §41.4 |

**没有** `SteamDiscovery.cs` / `GameDiscovery.cs` / `AntiCheatScanner.cs` —— 这些职责已分别由 `Detection.cs`、`Models.cs`、`AntiCheat.cs` 承担，因此未为文件名新造重复实现。

### 41.2 Renderer EXE 分级判定（§9.2）

`GameDetection/GameDetectionModels.cs`：`EvidenceLevel` 六级共用阶梯（`Unknown(0)` → `UserConfirmation(1)` → `StaticHeuristic(2)` → `RuntimeDetection(3)` → `StoreManifest(4)` → `VerifiedDatabase(5)`），数值即强度。

`RendererDetector.Detect` 严格按 §9.2 顺序：**已验证记录 → 进程观察 → 静态启发式 → 用户确认**，返回 `RendererDetection(路径, 级别, 原因, 全部候选)`。

两条关键规则：
- **静态启发式永不升级为已验证**：`Game-Win64-Shipping.exe` 这类命名只作 `StaticHeuristic` 证据，`CanPlanWithoutAsking` 对它是 false。
- **用户显式指定短路全部推导**：§9.2 把用户确认列在最后，那是**询问顺序**；一旦用户给出答案，再用文件名去覆盖它就是拿猜测推翻事实。故用户选择直接采纳，标 `UserConfirmation`，`HasSufficientEvidence` 为 false（它不是机器证据）。

`ProcessRole` 区分 `Launcher` / `Game` / `Renderer` / `Child` / `StoreWrapper` / `Unknown`，`launcher`、`UnityCrashHandler`、`EasyAntiCheat` 等噪声名一律不判为渲染进程。

### 41.3 Graphics API 四级判定（§9.3）

`GraphicsApiDetector.Detect` 严格按 `Verified → Runtime → Static → User → Unknown` 取第一个非 Unknown 者，**并把全部证据留在结果上**。因此 `HasConflict` 可表达"静态读 DX12、运行时观察到 DX11"这类分歧，而不是静默取其一。

- 邻近 `d3d12.dll` 只作 `StaticHeuristic`（§10 明确禁止"看到 d3d12.dll 就标 Verified DX12"）。
- `Unknown` 时 `InstallPlanner` **返回 Blocked 并点名 `UnknownApi`**，绝不默认挑一个 API。

### 41.4 12 维兼容性矩阵与 Stage 4 接入

`Compatibility/CompatibilityMatrix.cs`：`CompatibilityRecord` 十二维（GPU / Driver / GraphicsApi / Game / Store / RendererExe / Provider / ProviderVersion / InstallMode / ProxyAsi / LaunchMode / ValidationState），`CompatibilityQuery` 为对应查询。

**匹配规则（保守，§13）**：
- 记录未涵盖某维 → 该维**不匹配**（缺失不等于相同），导致无法 Exact
- 查询不知道某维 → 该维**无法核对**，同样无法 Exact
- 存在**明确矛盾** → `None`（不是 `Partial`：把矛盾说成"部分吻合"正是错误记录被采用的路径）
- 只有"部分吻合且其余无法核对"才是 `Partial`
- `ValidationState` 是**记录的结论**而非环境属性，查询没有对应项 → 只检查其存在性

**Stage 4 接入方式：没有改 `ICompatibilitySelector`。** 该接口只有 3 个参数，扩签名会改动 Stage 4 已测行为。改为**新增** `ICompatibilityEvaluator`（接受完整 `CompatibilityQuery`），由 `CompatibilityMatrixSelector` **同时实现两者**：
- `Evaluate(query)`：完整 12 维判断，`Exact + ReportedWorking → Compatible`，`Exact + ReportedBroken → Incompatible`，其余一律 `Unknown`
- `Decide(provider, installed, candidate)`：缺上下文 → 直接 `Unknown`，并在原因里说明缺哪些维度

Stage 4 的 `UnknownCompatibilitySelector` 与其测试**原样保留**（回归验证通过）。

### 41.5 InstallRecipe 与 InstallPlanner

`InstallPlanning/InstallPlanner.cs`。

`InstallRecipe` 字段按设计：Game / Store / GraphicsApi / RendererExePattern / ProviderId / ProviderVersionRange / RequiredFiles / ProxyStrategy / Mode / AsiStrategy / NvidiaProfileChanges / LaunchArguments / ValidationSteps / RollbackSteps。

`AppliesTo` **不接受通配兜底**：API 或版本任一未知，配方即不适用；只有全部条件吻合才返回 true。

`InstallPlanner.Plan` 是**纯函数**，只读文件系统判断入口是否被占用，**不写任何文件**（有测试在规划前后比对目录内容与文件字节）。判定顺序：
1. 内核级反作弊且未授权 → **Blocked**
2. `Api == Unknown` → **Blocked**（点名 `UnknownApi`，且计划不含任何待部署文件）
3. 渲染 EXE 未确定或仅有静态证据 → **NeedsConfirmation**
4. 无空闲代理入口 → **Blocked**；有冲突 → 警告 + 只选空闲入口
5. 兼容性 `Incompatible` → **Blocked**；非 `Exact` → **NeedsConfirmation**
6. 全部满足 → **Ready**

`ProxyStrategy` 记 A/B/C，但**不宣布哪个最优**（等价性仍为 `Unresolved / Needs Validation`）。

### 41.6 代理冲突规划

`ProxyConflictScanner.Scan` 遍历全部 7 个已知入口名，按归属分类：`OwnedByThisTool`（依据部署记录）/ `KnownGameFile` / `KnownCompatibleMod` / `Unknown`。

- **归属不明一律不算空闲**，也永不覆盖（"先备份再覆盖"同样禁止）
- 我方部署的入口可被识别（读 `game.Deployment.ProxyName` 与备份记录）
- `KnownCompatibleMod` 检测以 `Func<string,bool>` 钩子形式提供；**未接入识别器时保持 `Unknown`**（不猜）

### 41.7 已验证 / 未验证

**已验证**
- `dotnet build -c Release` → **0 警告 / 0 错误**
- Harness → **540 通过 / 0 失败 / 10 跳过**（基线 486/0/10，新增 54 项；**Stage 2/3/4 原测试未删**）
- 覆盖：渲染 EXE 五级判定与噪声排除 · API 四级优先级与冲突保留 · 12 维匹配（Exact / Partial / None）· 损坏矩阵容错 · 代理占用分类 · 规划器全部 Blocked 分支 · **无副作用守卫（目录与字节比对）** · 配方适用性
- 未修改任何真实游戏目录、未写入 NVIDIA Profile

**未验证 / 不在本阶段范围**
- 未在真实游戏上验证检测结果（自动测试通过 ≠ 实机验证通过）
- 未实现进程观察的采集端（`observedRenderer` 由调用方提供；采集属后续阶段）
- 未接入 Epic / Game Pass 枚举（`StoreSupport.NotImplemented` 如实表达）
- 未实现 PE 导入表解析（静态证据目前仅邻近 DLL + 命名结构）
- 未做 UI 接线；未做 Profile 写入（Stage 6）

### 41.8 Remaining Risks

| ID | 风险 | 说明 |
|---|---|---|
| **S5-01** | 静态启发式仍是唯一可得的自动证据来源 | `StoreManifest` 级需商店提供 EXE 信息，Steam appmanifest 不含；因此当前实际最高只能到 `RuntimeDetection`（需先观察进程） |
| **S5-02** | 兼容性矩阵为空 | Stage 5 结束时会话内没有任何 12 维记录，故 `LatestCompatible` 仍恒为 null——与 Stage 4 一致的有意结果 |
| **S5-03** | `KnownCompatibleMod` 只留钩子 | 未接入已知 mod 识别器，归属不明一概按冲突处理（保守方向，不影响安全性） |
| **S5-04** | 进程观察缺少采集器 | `observedRenderer` 需外部提供；自动采集（启动游戏并在进程树中识别渲染进程）未实现 |
| **S5-05** | `CompatibilityMatrixSelector.Decide` 做了两次查询 | 行为正确但有冗余；未优化以免在无测试覆盖动机下改动已验证路径 |
