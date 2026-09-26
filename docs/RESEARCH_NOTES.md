# RESEARCH_NOTES — Phase 0 调研记录

> 项目：**RTX 30 Frame Generation Manager**
> 任务书：`RTX30_Frame_Generation_Manager_DSH_Task_v2.md`
> 调研日期：2026-09-26 ｜ 阶段：**Phase 0（调研）**，本阶段不修改任何源码
>
> **本文件只记录可验证的事实。** 凡未取得一手证据者一律标注 `Unknown` / `Needs Validation` / `Experimental`，不做推测性补全。

---

## 0. 调研方法与环境事实

### 0.1 证据等级

| 标记 | 含义 |
|---|---|
| `[E1]` | 本次调研实际抓取的原文 / 实际读到的源码行 / 实际发起的请求 |
| `[E2]` | 官方文档的明文声明，未做事实验证 |
| `[E3]` | 社区线索（Issue、第三方项目），**不得单独作为实现依据** |
| `[?]` | `Unknown`，资料不足 |

**关键方法论修正（本次调研中实际发生）**：
> 最初用 `NVIDIA/nvapi` 的 `nvapi.h`（1,353 KB）检索 6 个 Smooth Motion Setting ID，得到「全部 0 命中」，但**连已知存在的 `VSYNCSMOOTHAFR_ID`（`0x101AE763`）也是 0 命中** —— 说明 `nvapi.h` **根本不是** DRS 设置 ID 的定义文件。
> 改用正确的 `NvApiDriverSettings.h`（76 KB）后，sanity check 全部通过（`VSYNCSMOOTHAFR_ID` / `SHIM_MCCOMPAT_ID` / `PREFERRED_PSTATE_ID` 均正常命中），此时「6 个 ID 零命中」才是**有效证据**。
> **教训**：任何「某符号不存在」的结论，必须先用一个**已知存在的对照符号**验证检索面正确。

### 0.2 本机环境 `[E1]`

| 项 | 实测值 | 影响 |
|---|---|---|
| OS | Windows，默认 shell `pwsh` | 原项目 WPF / Windows API 依赖成立 |
| `git` | **可用**（`D:\Git\cmd\git.exe`） | — |
| `python` | **可用**（3.14） | 调研脚本 |
| `node` | **可用** | — |
| **`dotnet`** | **已安装 8.0.425**（`C:\Users\linxi\AppData\Local\Microsoft\dotnet\dotnet.exe`；`dotnet --list-sdks` → `8.0.425 [...]\sdk`） | ✅ 原项目（.NET 8 / WPF）**可在本机构建与测试**，R-01 已消解 |
| `gh` CLI | **已安装 2.101.0**（`C:\Users\linxi\AppData\Local\Programs\gh\gh.exe`），已登录账号 `PLA0185` | 可走 CLI，不必再省 REST 配额 |
| `api.github.com` | 可用，未认证配额 **60 次/小时** | 必须节约，见 §0.3 |
| `github.com` git 协议 | **间歇性失败**（`Failed to connect to github.com:443 after 21079 ms`），同一时刻 HTTPS 正常 | 取源码改用 `codeload` zip |
| `raw.githubusercontent.com` | 稳定可用 | **主证据通路**（不消耗 API 配额） |
| `codeload.github.com` | 可用 | 取整仓 zip |
| **GPU** | **NVIDIA GeForce RTX 3070 Ti Laptop GPU**（8192 MiB，`Status=OK`） | ✅ **本机即目标硬件，具备 RTX 30 / SM86 实机验证条件**（正是任务书 §39 首页示例机型） |
| **NVIDIA 驱动** | **617.14**（`32.0.16.1714`，2026-09-17） | ⚠️ 相对社区白名单偏新：MFG 有专门为 617.14 出的修复包；但**不在 xikarioz 白名单内**（其 golden 为 616.64）—— 见 §2.4 |
| **HAGS（硬件加速 GPU 计划）** | **已启用**：`HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode = 2`（枚举 `1`=关 / `2`=开） | ✅ 上游明确 `hags.state=off` 是帧生成**不生效的首要原因**；本机已满足该环境前置条件（见下方勘误块与 R-14） |

> ### ⚠️ 勘误：HAGS 结论更正（2026-09-26）
>
> 本表**早期版本**曾记录「HAGS **未设置**（注册表 `HwSchMode` 不存在）」——**该结论错误，现予更正**。
>
> | 项 | 内容 |
> |---|---|
> | **错误结论** | HAGS 未设置（`HwSchMode` 不存在） |
> | **错误成因** | 早期只做单次注册表读取，且**未区分「读取失败」与「键确实不存在」**，把探测面的失败当成了系统事实 |
> | **更正后的实测值** | `HwSchMode = 2`（开）。用两条独立路径重测，结果一致：①PowerShell `Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' -Name HwSchMode`；②`reg query`（同机同轮） |
> | **旧检测为何不可靠** | 单信号探测无法区分 `Unsupported` / `SupportedDisabled` / `ConfiguredOn` / `ConfiguredOnRebootRequired` / `Enabled` / `Unknown` / `ConflictingSignals` 七种状态，「读不到」被直接等同于「关」 |
> | **新的检测规则（后续实现必须遵守）** | **禁止**由「`HwSchMode` 缺失」推断「HAGS 关闭」。判定须以**注册表配置 + GPU/WDDM 能力 + 重启状态/运行时证据**组合而成；**在未取得运行时生效证据前，一律如实返回 `ConfiguredOn`，不得对外宣称 `Enabled`**（设计见 [`ARCHITECTURE_PLAN.md`](ARCHITECTURE_PLAN.md) §36.1） |
>
> **连带影响**：R-14 由「**高**风险 + 需用户操作」降级为「**已满足前置条件**，仅需在实机验证时确认运行时生效」；上游 `hags.state=off` 这条首要原因的排查项在本机不再适用，但**仍须在代码中保留检测与提示**（用户机器不一定与本机相同）。

> **v5 §76 的意义**：本机具备 RTX 30 GPU，因此**不得**再以「无硬件」为由跳过实机验证；但自动测试通过**仍不等于**实机验证通过，报告时必须严格区分（见 `ARCHITECTURE_PLAN.md` §33）。

### 0.2.1 工具链、仓库与基线（2026-09-26 实测）`[E1]`

**工具链（已具备）**：

| 工具 | 版本 | 绝对路径 |
|---|---|---|
| .NET SDK | **8.0.425**（`dotnet --list-sdks` → `8.0.425 [...\sdk]`） | `C:\Users\linxi\AppData\Local\Microsoft\dotnet\dotnet.exe` |
| GitHub CLI | **2.101.0**（2026-09-15 构建），已登录 `PLA0185` | `C:\Users\linxi\AppData\Local\Programs\gh\gh.exe` |

`DOTNET_ROOT`（用户级）= `C:\Users\linxi\AppData\Local\Microsoft\dotnet`。

> ⚠️ **本机环境陷阱（已实测踩到）**：**DSH 后端进程的环境变量是启动时的快照**，因此即使用户级 `PATH` 已包含上述两个目录，**由它派生的新 `pwsh` 里 `dotnet` / `gh` 仍可能报 `CommandNotFoundException`**。
> **对策**：脚本与自动化一律**用绝对路径调用**，或在使用前显式刷新：
> ```powershell
> $env:PATH = [Environment]::GetEnvironmentVariable('PATH','Machine') + ';' + [Environment]::GetEnvironmentVariable('PATH','User')
> ```

**仓库状态**：

| 项 | 值 |
|---|---|
| 仓库 | **`PLA0185/RTX30-FrameGen-Manager`**（PUBLIC，默认分支 `main`） |
| `origin` | `https://github.com/PLA0185/RTX30-FrameGen-Manager.git`（用户仓库，v5 §59 的交付目标） |
| `upstream` | `https://github.com/BUNNY-19C/DLSSG-30s-manager.git`（**只读参考，不自动合并**） |
| 基线提交 | **`b0a6424`** `chore: import upstream BUNNY baseline and add Phase 0 deliverables`，本地与远端 SHA 一致 |
| 未上传内容 | `_research/`（证据快照）、`extra-proxies/d3d12.dll`、`PROJECT_MEMORY.md`（均在 `.gitignore` 内，已确认未被推送） |

**基线构建与测试（原始输出摘录，2026-09-26 复测）**：

```
$ dotnet build DLSSGManager.sln -c Release
  DLSSGManager -> ...\src\DLSSGManager\bin\Release\net8.0-windows\DLSSGManager.dll
  Harness      -> ...\test\Harness\bin\Release\net8.0-windows\Harness.dll
已成功生成。
    0 个警告
    0 个错误
已用时间 00:00:02.97

$ dotnet ...\test\Harness\bin\Release\net8.0-windows\Harness.dll
===== 通过 343 · 失败 0 · 跳过 10 =====
（跳过的 10 项需要 Mod 文件，运行 Harness.exe --fetch 获取后重试）
```

> **解读边界（v5 §76）**：上表是**基线（未修改的上游代码）**的构建与自动测试结果，属 `Automated Tests Passed`，**不构成**任何实机功能验证结论。跳过的 10 项需先获取 Mod 文件。另注：Harness 的 TFM 为 **`net8.0-windows`**（非 `net8.0`），路径写错会报 `无法执行，因为找不到指定的命令或文件`。

### 0.3 配额策略（实际执行）

GitHub 未认证配额仅 60 次/小时且为**多进程共享**。本次实际用量约 21 次：6×`/repos` + 6×`/releases?per_page=100` + 2×`git/trees` + 2×`/issues` + 若干探测。
其余全部走 `raw.githubusercontent.com` 直链与网页抓取。**所有原始响应已缓存到 `_research/`，后续分析不再重复联网。**

**缓存目录布局**：
```
_research/
├─ api/           # 6 个仓库的 repo / releases / issues 原始 JSON
├─ docs/          # 24 份上游权威文档（raw 直取）
├─ community/     # 社区实现证据（NPI 命名表、RHI 源码、官方 DRS 头文件）
├─ upstream/      # 原管理器完整源码（codeload zip 解压）
├─ fetch_gh.py · fetch_docs.py · fetch_community.py · fetch_crossverify.py
```

---

## 1. 研究过的仓库与 Release（§58 要求）

| # | 仓库 | 许可 | 最新版本 | Release 数 | 分发模型 |
|---|---|---|---|---|---|
| 1 | `BUNNY-19C/DLSSG-30s-manager` | **MIT** | `v1.9.3`（2026-09-25） | **26** | Release Asset |
| 2 | `sdli1995/dlssg_for_sm86` | 仓库无 LICENSE（README 称 GPLv3） | `0.3.5`（2026-09-19） | 7 | **git 树逐文件** |
| 3 | `pipotoufikxyz-lgtm/dlssg_for_sm86-MFG-version` | **无 LICENSE** | `smfix`（2026-09-24） | **18** | Release Asset |
| 4 | `xikarioz/Smooth-Motion-RTX30` | 专有 **EULA** | `v0.5.0`（稳定）/ `v0.5.2-alpha.1`（预发布） | 9 | Release Asset |
| 5 | `NVIDIA/nvapi` | `NOASSERTION` | **无 Release** | 0 | — |
| 6 | `RankFTW/RHI` | **GPL-3.0** | `RHI-2.7.6`（2026-09-24） | **100+** | Release Asset |
| 7 | `Orbmu2k/nvidiaProfileInspector` | 社区维护 | — | — | 参考：社区设置命名表 |

> **分页修正**：首轮抓取用 `per_page=15`，把仓库 1 误记为 15 个 Release（实际 **26**）、仓库 3 误记为 15（实际 **18**）、仓库 6 截断于 100。改用 `per_page=100` 后修正。**`per_page` 过小会静默截断且不报错。**

### 1.1 官方文档 `[E1]`

已抓取的权威文档（存于 `_research/docs/`）：

| 来源 | 文档 | 大小 |
|---|---|---|
| `sdli1995/dlssg_for_sm86` | `README.md` / `README.en.md` / `dlssg_sm86.ini` / `alternatives/README.md` / `THIRD_PARTY_NOTICES.txt` / `docs/INSTALL.md` | 3.5–52 KB |
| `pipotoufikxyz-lgtm/…-MFG-version` | `README.md` / `VULKAN_SUPPORT.md` | 1.9 / 7.4 KB |
| `xikarioz/Smooth-Motion-RTX30` | `README` / `COMPATIBILITY` / `INSTALL` / `VALIDATION` / `RELEASE_NOTES` / `EULA` / `FAQ` / `SECURITY` / `SUPPORT` / `TROUBLESHOOTING` / `THIRD_PARTY_NOTICES` / `ROADMAP` / `docs/COMMUNITY_VALIDATION.md` | 1.1–13.7 KB |
| `NVIDIA/nvapi` | `README.md` / `nvapi.h` / **`NvApiDriverSettings.h`** | 1 KB / 1,353 KB / **76 KB** |
| `RankFTW/RHI` | `README.md` | 10.2 KB |
| `Orbmu2k/nvidiaProfileInspector` | `nvidiaProfileInspector/CustomSettingNames.xml` | 376.6 KB |

> ⚠️ `sdli1995` 的 README 引用了 `docs/SIGNING.md`、`docs/CAPTURE.md`、`docs/ARCHITECTURE.md`、`docs/MFG_PROBE.md`、`docs/evidence/`，但 `docs/` 目录下**实际只有 `INSTALL.md` / `INSTALL.en.md`** —— 其余链接均为死链。

### 1.2 研究过的 Issues `[E1]`

**`sdli1995/dlssg_for_sm86`**（共 154 open，抓取最近 30 条）：

| Issue | 标题要点 | 对本项目的意义 |
|---|---|---|
| **#581** | *Only version 0.1.0 works for me* | **版本回退是真实需求** |
| **#596** | *God of War Ragnarok only supports version 0.2.4 and prior, DLSS-FG option not available* | **多版本并存是真实需求** |
| #593 | *Virus detected = drawback* | 杀软误报需内置解释 |
| #577 | `[3070][0.3.4~0.3.5]` 注入绝区零被强制退出 | 游戏保护模块冲突 |
| #598 / #569 / #570 | 战神5 卡顿崩溃 / FS25 冻结 / Horizon ZD 间歇 `0xBAD00005` | 崩溃类问题跨版本反复 |
| #601 / #600 / #573 / #562 | FG 菜单灰置/不出现 | **FG 不可用 ≠ mod 未装**，须看日志归因 |
| #580 | *Suggestion: a wiki page for game compatibility, e.g. which file name works* | **社区明确索要「入口名 ↔ 游戏」权威表** |
| #574 | *What about the implementation of Smooth Motion (for Linux/Proton)?* | 社区已在关注 Smooth Motion 路线 |
| #602 | RTX A5000 Laptop（专业卡 / SM86）0.3.5 可用 | SM86 不限于消费级 RTX 30 |

**`pipotoufikxyz-lgtm/…-MFG-version`**（共 28 条，抓取 25 条）：

| Issue | 标题要点 | 对本项目的意义 |
|---|---|---|
| **#18** | *dead space remake not working, **SM on in NVPI*** | **证实激活依赖手工 NVPI** —— 本项目的自动化目标 |
| **#20** | Dragon's Dogma 2: **`driver_validation_rejected`** | 2.8.2 内部含驱动校验 |
| **#16** | *Under the new driver version, Smooth Motion will be disabled* | 驱动更新会破坏已装配置 |
| **#15** | *Windows Defender quarantines `SmoothMotion-1.6-installer.zip` as `Trojan:Script/Wacatac.B!ml`*（已修复） | **杀软误报历史**，UI 必须预置说明 |
| #6 | *Installer worked poorly, please keep manual installation route* | 安装器可靠性不足 |
| #25 | *Stopped working after changed from version 1.6 to 2.82* | 跨版本回归 |
| #11 / #21 / #19 / #22 / #23 | 多游戏不工作（Genshin、Valheim） | 兼容性需数据库承载 |
| #17 | *READ BEFORE CREATING AN ISSUE!!!!*（pinned） | 作者要求附日志/截图 |

---

## 2. 核心结论：RTX 30 系 Smooth Motion 的可行性定位

> **本节是任务书 §10.5 / §10.7 的直接回应。结论按 §10.5 E 的「正确示例」格式表述。**

### 2.1 结论

```
NVIDIA 官方当前仅声明 Smooth Motion 支持 GeForce RTX 40 Series and newer GPUs。
RTX 30 系（Ampere SM86）方案属于非官方社区实现。
当前发现多个独立社区项目通过隐藏 Driver Profile Setting / 补丁实现 RTX 30 Smooth Motion，
其中 6 个 DRS Setting ID 已获两个互相独立的来源交叉验证一致。
因此该功能继续研究，状态标记为 Experimental / Community Supported。
```

**这不构成停止开发或删除功能的理由**（§10.5 B、§10.7）。

### 2.2 支持性证据 `[E1]`

**证据 1 —— 社区项目明确把 RTX 30 列为「架构兼容」。**
`xikarioz/Smooth-Motion-RTX30` 的 `COMPATIBILITY.md:46` 原文：

| GPU | Driver | API | Status |
|---|---|---|---|
| RTX 3090 | 616.64 | D3D11 | **PROJECT_VALIDATED** — instrumentally validated live path |
| RTX 3090 | 616.64 | D3D12 | **PROJECT_VALIDATED (real-game)** — observed working |
| **Other single-GPU RTX 30 / SM86（3080, 3070, 3060, 3050, laptop）** | 616.64 | D3D11 / D3D12 | **EXPERIMENTAL（architecture-compatible；admitted when the exact driver/profile is accepted）** |

`COMPATIBILITY.md:36` 进一步说明：*"Unknown / unvalidated NvPresent → Setup + Manager run; Smooth Motion refused (fail-closed)"* —— 即**未验证 ≠ 不可用**，而是安全拒绝。

**证据 2 —— 另一个独立项目以「注入 + NVPI Profile」实现 RTX 30 Smooth Motion。**
`pipotoufikxyz-lgtm/…-MFG-version` 的 `README.md:20,27,34-36` 原文：
> *"UPDATE: **ADDED SMOOTH MOTION SUPPORT FOR 617.14 DRIVER.** (wasn't working)"*
> *"To Activate it: activate it through Nvidia Profile Inspector"*
> *"-Open NVIDIA Profile Inspector on your computer. -**Locate Smooth Motion** -**Enable it and Save**"*
> *"**INSTALL FOR 3000S SERIES:** … Copy this package's `version.dll` and `dlssg_sm86.ini` beside the actual rendering EXE."*

**证据 3 —— 业务痛点被上游 Issue 直接证实。**
MFG 的 `#18 dead space remake not working, SM on in NVPI`、`#20 driver_validation_rejected`；`sdli1995#580` 索要「哪个文件名对哪个游戏有效」的权威表。
⇒ 本项目「自动判断加载方式 + 自动配置 Profile」的定位（任务书 §21、§29）**有真实需求支撑**。

**证据 4 —— 结构性结论（本项目架构的决定性输入）** `[E1]`

对 8 个社区实现（`sdli1995`、MFG、xikarioz、RHI、NVPI、NVPI Revamped、`ItsAdeline/NVSmooth30`、`Aryoksini/DLSS5-Feeder`、`emoose/DLSSTweaks`）逐一核对后的结论：

```
所有已确认的 RTX 30 成功案例，都是「路线 B（适配 NvPresent / 代理 DLL 补丁）
                              + 路线 A（NVPI 写入 Smooth Motion profile）」
两条路线的组合。
没有任何一个项目仅靠写入 DRS profile 就在 SM86 上跑通 Smooth Motion。
```

**典型例证**：`ItsAdeline/NVSmooth30`（MIT，93★）patch `NvPresent64.dll` 的兼容性检查、做 CUDA SM89→SM86 kernel 重定向与 D3D11→D3D12 bridge，但它的 31 个源码文件**不含任何 NVAPI/DRS 写入**，README 反而要求用户**手工用 NVPI 开 Smooth Motion**，并写明 *"You will need to do this everytime your drivers are updated"*。

⇒ **架构含义（关键）**：本项目的 Smooth Motion 功能**必须同时实现两条路线**——
① 补丁/代理部署（`ARCHITECTURE_PLAN.md` §11 流程的步骤 6–7）；
② NVIDIA Profile 自动配置（步骤 8）。
**只实现 ② 不足以在 RTX 30 上生效。** 这也印证了本项目「自动化用户手工操作」的价值定位：社区现状是「两条路线都要做，且每次驱动更新后都要重做」。

**证据 5 —— NVIDIA 官方只声明「支持范围」，并未声明 SM86「不可能」** `[E1]`

NVIDIA 官方驱动 README（第 39 章 `nvpresent`）原文：
> *"…enhancing your experience on **GeForce RTX 40 Series and newer GPUs**"*
> *"can be enabled by setting the environment variable `NVPRESENT_ENABLE_SMOOTH_MOTION=1`"*

即：官方文档**仅划定支持范围**，**没有**任何「RTX 30 在技术上无法实现」的表述。这与任务书 §10.5 B 的立场完全一致——官方不支持属**预期事实**，而非**技术否决**。

该文档同时揭示了功能的底层机制名 **`NVPRESENT_ENABLE_SMOOTH_MOTION` / `NvPresent`**，与 `xikarioz/Smooth-Motion-RTX30`（适配 `NvPresent64.dll`）及 `DLSS5-Feeder`（提到 Smooth Motion 的 `InvisibleWindowClassNvPresent`）**三处独立印证**同一机制。

⇒ **本项目结论表述（按 §10.5 E）**：官方支持范围不含 RTX 30，社区通过 `NvPresent` 适配层 + 隐藏 Driver Profile Setting 实现该能力，因此**继续研究并实现**，状态 `Experimental` / `Community Supported`。

### 2.3 该路线的事实来源（按 §10.5 C 排序，本项目应采用）

| # | §10.5 C 规定的事实来源 | 状态 |
|---|---|---|
| 1 | 目标补丁项目 README（MFG / xikarioz） | ✅ 已采集 |
| 2 | GitHub Releases | ✅ 已采集（6 仓库全量，`per_page=100`） |
| 3 | GitHub Issues / Discussions | ✅ 已采集（`sdli1995` 30 条 + MFG 25 条） |
| 4 | 已知成功案例 | ✅ 已采集（xikarioz README 的 16 款游戏清单） |
| 5 | **多个独立社区实现之间的交叉验证** | ✅ **已找到 4 个独立来源**，见 §3.2；远超 §10.5 D 的「两个独立实现」门槛 |
| 6 | 本项目实际测试结果 | ⏳ Stage 1 之后 |
| 7 | 用户实际验证结果 | ⏳ 产品化之后 |

**已检索到的独立社区实现（共 8 个，其中 4 个为任务书点名之外的新发现）**：

| 项目 | 许可 | 角色 |
|---|---|---|
| `pipotoufikxyz-lgtm/dlssg_for_sm86-MFG-version` | **无 LICENSE** | 任务书点名；注入 + 要求手工 NVPI |
| `xikarioz/Smooth-Motion-RTX30` | **专有 EULA** | 任务书点名；应用级 fail-closed |
| `RankFTW/RHI` | **GPL-3.0** | 任务书点名；DRS 写入实现（**代码不可复制**） |
| `Orbmu2k/nvidiaProfileInspector` | **MIT** | 任务书点名；社区命名表 |
| **`ItsAdeline/NVSmooth30`** | **MIT** | 🆕 93★；patch `NvPresent64.dll` + CUDA SM89→SM86 重定向；**不含 DRS 写入**，要求手工 NVPI |
| **`xHybred/NvidiaProfileInspectorRevamped`** | **MIT** | 🆕 NVPI 分支；命名表含 Smooth Motion 且条目更细 |
| **`Aryoksini/DLSS5-Feeder`** | **MIT** | 🆕 README 给出 NVPI 设置 ID 表（第 4 个独立 ID 来源） |
| **`emoose/DLSSTweaks`** | **MIT** | 🆕 提供 DRS 函数 ID 的「主 + 回退」双 ID 表；其包装**不含** Smooth Motion |

另抓取 `jp7677/dxvk-nvapi` 的 `Passing-driver-settings.md` —— 其 DRS 文档对 Smooth Motion **零命中**（负面证据，佐证该设置为社区发现而非公开接口）。

> ⚠️ 新增来源的**仓库全名与许可证已由调研线核实**（MIT 四个），但**引用其命名表条目时仍须保留版权声明**；`Aryoksini/DLSS5-Feeder` 的 README 内文自称 `jlrouzies-fr/DLSS5-Feeder`，两者关系（fork / 改名）**未核实**，实现前需确认。
> 这些来源的用途**仅限证据交叉验证**，不得复制其代码。

### 2.4 驱动兼容性对照（**v5 §69「Latest Compatible」的直接依据**）`[E1]`

| 实现 | 驱动要求 / 状态 | 依据 |
|---|---|---|
| `sdli1995/dlssg_for_sm86` | 需带 NGX / NVAPI / CUDA 接口；**实测于 591.86 与 610.74**；cubin 需约 **R580+**，更老驱动自动回退 PTX | 上游 README |
| **MFG 版** | **617.14 有专门支持**（Release `smdriverupdate` = `SmoothMotion-2.8.2-R3-GP9-Driver-61714.zip`）；README 记 *"ADDED SMOOTH MOTION SUPPORT FOR 617.14 DRIVER (wasn't working)"* ⇒ 617.14 曾**破坏** Smooth Motion 后被修复 | 上游 README + Release 清单 |
| **xikarioz** | **616.64 = golden（live validated）**；591.86 = static/experimental；**616.92 明确未授权**；未知 `NvPresent` build ⇒ **fail-closed**（不修改任何东西） | `COMPATIBILITY.md` |
| **本机实测** | **617.14**（`32.0.16.1714`） | 本次实测 |

⇒ **这正是 v5 §69 的现成实例**：在本机驱动 `617.14` 下，
- 走 **MFG 路线**：`Latest Compatible` 应包含专门适配 617.14 的包；
- 走 **xikarioz 路线**：`Latest Available = v0.5.0`，但**很可能触发 fail-closed**（617.14 高于其白名单上限）。

**两个「最新」在此场景下并不相同** —— 这解释了为什么 v5 §69 要求将二者分开展示，且禁止「盲目追最新版」。

---

## 3. **交叉验证：6 个 Smooth Motion DRS Setting ID**

> 任务书 §10.5 D 要求：实现前应**尽量满足至少一种**验证条件 —— 其中首选项是「**两个独立实现使用相同 Setting ID**」。本节即该验证的执行记录。

### 3.1 结论（按来源强度分级，**不作乐观合并**）

| 分组 | Setting ID | 独立来源数 | 判定 |
|---|---|---|---|
| **核心 ID** | `0xB0D384C0`（Enable）、`0xB0CC0875`（Enabled APIs）、`0xB03A4546`、`0xB03A4547` | **4**（NPI / Revamped / RHI / DLSS5-Feeder） | ✅ `Community Verified` |
| 诊断 ID | `0xB053C379`（Debug Log） | **1**（仅 NPI 命名表） | ⚠️ 单一来源 |
| 诊断 ID | `0xB01B8B02`（Debug Bars） | 2，但**措辞逐字相同、疑同源** | ⚠️ 保守计为**单一来源 + 弱佐证** |

四个核心 ID 的数值在全部来源中完全一致，**远超 §10.5 D 的首选验证条件（「两个独立实现使用相同 Setting ID」）**，故标记 `Community Verified` 并继续实现。
它们仍属 **`Undocumented NVIDIA Driver Setting`**（官方 DRS 头文件中零命中）。

> ⚠️ **命名存在真实冲突**（ID 与取值一致，仅人类可读标签不同）：
> `0xB03A4546` / `0xB03A4547` 在 NPI 表中名为 `Flip Metering 0/1`，在 Revamped 与 RHI 中名为 `Flip Pacing [Fullscreen]/[Windowed]`；Revamped 另含同 ID 的隐藏条目 `Flip Meter Debug / Flip Meter`。
> **本项目实现时必须以 ID 为准，不得以名称为准**；UI 展示时须标注命名分歧。

### 3.2 交叉验证对照表 `[E1]`

**来源**：
- **A** = NVIDIA Profile Inspector `CustomSettingNames.xml`（社区维护的命名表）
- **B** = `RankFTW/RHI` 源码 `DlssPresetService.DriverSettings.cs`（C# / GPL-3.0，仅作参考）
- **C** = NVIDIA Profile Inspector **Revamped** `CustomSettingNames.xml`（**独立衍生产品**）
- **D** = `jlrouzies-fr/DLSS5-Feeder` README（**独立项目**，ReShade 生态）

| Setting ID | A（NPI） | B（RHI 源码） | C（NVPIRevamped） | D（DLSS5-Feeder） | 独立来源数 |
|---|---|---|---|---|---|
| `0xB0D384C0` | `Smooth Motion - Enable` | `SMOOTH_MOTION_ENABLE_ID`（`:27`） | ✅ | `Smooth Motion - Enable`（`0`/`1`，**per application**） | **4** |
| `0xB0CC0875` | `Smooth Motion - Enabled APIs` | `SMOOTH_MOTION_APIS_ID`（`:28`） | ✅ | `Smooth Motion - Enabled APIs`（位掩码，默认 `7`：`1` DX12 / `2` DX11 / `4` Vulkan） | **4** |
| `0xB053C379` | `Smooth Motion - Debug Log Level` | — | — | — | **1**（单一来源） |
| `0xB01B8B02` | `Smooth Motion - Debug Bars` | — | — | `Smooth Motion - Debug Bars`（**用于确认 Smooth Motion 是否真的在跑**） | **2，措辞逐字相同、疑同源 ⇒ 计为单一来源 + 弱佐证** |
| `0xB03A4546` | `Smooth Motion - Flip Metering 0` | `SMOOTH_MOTION_FLIP_PACING_FS_ID`（`:29`） | ✅ | — | 3 |
| `0xB03A4547` | `Smooth Motion - Flip Metering 1` | `SMOOTH_MOTION_FLIP_PACING_WIN_ID`（`:30`） | ✅ | — | 3 |

**来源 URL**：
- A / C：`raw.githubusercontent.com/Orbmu2k/nvidiaProfileInspector/master/nvidiaProfileInspector/CustomSettingNames.xml`（及 Revamped 分支同名文件）
- B：`raw.githubusercontent.com/RankFTW/RHI/main/RenoDXCommander/Services/DlssPresetService.DriverSettings.cs`
- D：`raw.githubusercontent.com/jlrouzies-fr/DLSS5-Feeder/main/README.md`

> ⚠️ **来源 A / C 的性质必须如实说明**：`CustomSettingNames.xml` 是 **社区维护的「自定义设置名」表**，用于给未知 DRS ID 赋予可读名称，**不是** NVIDIA 官方定义。多来源一致只能证明「社区共识」，不能升级为 `Official`。
>
> ✅ **位掩码语义已获独立印证**：来源 B 与来源 D 独立给出相同的位掩码定义（`1`=DX12、`2`=DX11、`4`=Vulkan），且来源 D 给出了实际用法示例（把 `7` 改成 `3` 即关闭 Vulkan）。
>
> ⚠️ 来源 D 把 Enable 描述为 **per application**（按应用/按 profile 设置），与 §10.3 的 profile 级写入模型一致；来源 B 则标注该档位为 `On [40 Series+]`。

### 3.3 取值语义 `[E1]`（来源 B，`DlssPresetService.DriverSettings.cs:94-123`）

| 设置 | 取值 |
|---|---|
| `SmoothMotionEnableOptions` (`0xB0D384C0`) | `Off = 0x0` ｜ `On [40 Series+] = 0x1` |
| `SmoothMotionApisOptions` (`0xB0CC0875`) | `None=0` ｜ `DX12=1` ｜ `DX11=2` ｜ `DX11/12=3` ｜ `VK=4` ｜ `DX12,VK=5` ｜ `DX11,VK=6` ｜ `All=7` —— **位掩码：DX12=bit0, DX11=bit1, Vulkan=bit2** |
| `SmoothMotionFlipPacingFsOptions` (`0xB03A4546`) | `Off [Latency] = 0x0` ｜ `On [Pacing] = 0xFFFFFFFF` |
| `SmoothMotionFlipPacingWinOptions` (`0xB03A4547`) | `Off [Latency] = 0x0` ｜ `On [Pacing] = 0x1` ｜ `Force On [Pacing] = 0xFFFFFFFF` |

> ⚠️ 来源 B 把 Enable 标注为 **`On [40 Series+]`** —— 社区实现自身也承认这是 40 系+ 的官方档位。本项目在 RTX 30 上使用它属社区实验路线，**UI 必须如实标注**。

### 3.4 官方侧的核对 `[E1]`

对官方 `NVIDIA/nvapi@main:NvApiDriverSettings.h`（76 KB）检索：

| 检索项 | 结果 |
|---|---|
| sanity check：`VSYNCSMOOTHAFR_ID` / `0x101AE763` / `SHIM_MCCOMPAT_ID` / `PREFERRED_PSTATE_ID` | **各 1 命中**（证明检索面正确） |
| `0xB0D384C0` / `0xB0CC0875` / `0xB053C379` / `0xB01B8B02` / `0xB03A4546` / `0xB03A4547` | **各 0 命中** |
| 任意 `0xB0xxxxxx` 段 ID | **不存在** |

⇒ 判定为 `Undocumented NVIDIA Driver Setting`，来源为社区交叉验证。

> ⚠️ **陷阱**：官方头文件中的 `VSYNCSMOOTHAFR_ID = 0x101AE763` 是「**Vertical Sync - Smooth AFR Behavior**」（多 GPU 帧交替同步），与 Smooth Motion **无关**，严禁混用。

### 3.5 附：来源 D 提供的 Smooth Motion **运行时行为**（对运行验证有直接价值）`[E1]`

`DLSS5-Feeder` 的 README 是从**图形注入侧**观察 Smooth Motion 的，与前述「设置侧」来源互相独立，提供了以下可直接用于本项目设计的事实：

| # | 事实（README 原文要点） | 对本项目的价值 |
|---|---|---|
| 1 | **Smooth Motion 在 Vulkan 上于驱动内部生成帧**：「inside the graphics driver — the very last step before the picture reaches your monitor, **past anything ReShade or any add-on can touch**」 | 解释为何 Vulkan 路径需要特殊对待；本项目在 Vulkan 场景应给出明确提示而非静默尝试 |
| 2 | **Smooth Motion 会创建自己的 D3D11 设备与不可见代理 swapchain**，窗口类名 `InvisibleWindowClassNvPresent` | 可作为**运行时探测点**：枚举进程内是否存在该窗口类 / 额外 D3D11 设备 |
| 3 | **Smooth Motion 会引入额外的 `Present` 调用**（第三方需做线程安全处理） | 提示本项目在进程观察时应容忍额外 Present |
| 4 | **已知会使部分第三方 add-on 崩溃**：`Luma` 因 `GetCurrentBackBufferIndex()` → `GetBuffer()` 在 Smooth Motion 的 flip-model wrapper 下取到 null | 佐证「Smooth Motion 生效会改变呈现路径」这一事实 |
| 5 | **`Smooth Motion - Debug Bars`（`0xB01B8B02`）是确认其是否真正运行的可靠可视化手段**：「coloured bars on screen mean it is running」 | ⭐ **为任务书 §25 的 `Applied` / `Verified` 状态提供了一个独立、可操作、可截图留证的外部证据源**，可与补丁日志互补 |
| 6 | **该工具无法在 Vulkan 上检测 Smooth Motion**（其检查的文件只在 DirectX 游戏中被驱动加载，故日志静默 ≠ 未开启） | 警示：**「没有检测到」不等于「没有生效」**，运行验证不得只依赖单一信号 |
| 7 | 驱动/运行时组合存在不兼容（示例：`616.64` 与 `616.56` 在不同神经网络消费者下结果不同） | 强化 R-05（驱动耦合）：安装记录必须保存驱动指纹 |

> **设计结论**：本项目的运行验证链（任务书 §25）应采用**多信号交叉**，至少包含：
> ① 补丁自身日志（`dlssg_sm86\logs\loader_*.jsonl` 的 `install.active` / `backend_install.status`）；
> ② 进程模块枚举（代理 DLL/ASI 是否被加载）；
> ③ **`0xB01B8B02` Debug Bars 的用户可视化确认**（唯一能覆盖到驱动内部行为的信号）；
> ④ 兼容性数据库中同配置的历史结果。
> **任何单一信号都不得直接判定为 `Verified`。**

---

## 4. 上游分发模型与 Release 解析规则（更新系统的设计基石）

### 4.1 **两种分发模型必须并存** `[E1]`

| 模型 | 仓库 | 事实 |
|---|---|---|
| **git 树逐文件** | `sdli1995/dlssg_for_sm86` | 7 个 Release **全部 `assets=0`**；二进制只存在于 git 工作树 |
| **Release Asset** | MFG / xikarioz / RHI | 二进制只通过 Release Asset 分发，仓库内无发行包 |

**对模型 A 的实测**（`raw.githubusercontent.com`，不消耗配额）：

| 路径 | HTTP | Content-Length |
|---|---|---|
| `main/dlssg_sm86.ini` | 200 | 3,548 |
| `main/version.dll` | 200 | **30,021,920** |

**payload 体积**：根目录 `version.dll` + `alternatives/` 5 个 DLL + INI ≈ **180 MB**（十进制）。
而整分支归档 `codeload.github.com/…/zip/refs/heads/main` 携带 `310.1/` 与 `archive/` 历史产物，**远大于 payload**。
⇒ **必须以逐文件 raw 为主路径，归档仅作限流兜底。**

### 4.2 实测的 API 语义陷阱 `[E1]`

| # | 陷阱 | 实测证据 |
|---|---|---|
| **T1** | **`/releases` 列表顺序不可依赖** | MFG 仓库首项是 `smxbox`，但按 `published_at` 最新的是位于 **index 1** 的 `smfix`。5 个仓库中 2 个不满足「首项即最新」 |
| **T2** | **列表顺序 ≠ `created_at` 降序 ≠ `published_at` 降序** | 同上仓库两者均不匹配 |
| **T3** | **`created_at` 与 `published_at` 可互相倒挂** | `sdli1995` 的 `0.3.3`：published 早于 created 15 分钟；`0.3.2` 早 35 分钟 |
| **T4** | **`/releases/latest` 对无 asset 仓库无用** | `sdli1995` 返回 HTTP 200 但 `assets=[]`，拿不到任何文件 |
| **T5** | **平台/预发布会污染「最新」判定** | xikarioz 按 `published_at` 取最新会命中 **Linux 包** `linux-v0.1.0-experimental` |
| **T6** | **Asset 名可能滞后于 tag** | xikarioz tag `v0.4.3` 的资产名为 `SmoothMotionSM86-**0.4.2**-win64.zip` |
| **T7** | **同名 Asset 内容不同** | xikarioz `v0.4.2` 与 `v0.4.3` 的 zip 同名但字节数不同（34,993,431 vs 34,991,734） |
| **T8** | **Asset 会被事后撤走** | MFG 的 `sm86` / `sm75-2` / `sm75` 三个 Release 当前 `assets=0` |
| **T9** | **上游会原地重建 Release 并更新校验和** | xikarioz `RELEASE_NOTES` 声明 2026-09-24 重建过资产 |

**由此得出的硬性规则**：
- **不得**用列表首项、时间排序或 `/releases/latest` 作为版本权威
- 版本必须由 **Provider 专属解析器**从 `tag` / `name` / `asset 名` 提取并交叉校验
- **唯一键 = `repo + tag + asset 名 + size`；内容身份 = SHA-256**
- 缓存失效条件必须包含 **SHA256SUMS 变化**

### 4.3 各仓库的版本命名现状 `[E1]`

| 仓库 | tag 形态 | 版本号实际位置 | 解析难度 |
|---|---|---|---|
| `BUNNY` | `v1.9.3`（规范 SemVer） | tag | 低 |
| `sdli1995` | `0.3.5`（规范） | tag + **README 首行** `# DLSSG for SM86 (proxy) - 0.3.5 Version` | 低（但无 asset，版本≠可下载物） |
| **MFG** | **完全非 SemVer**：`smxbox` `smfix` `smdriverupdate` `SMMANUAL` `smmnaul` `smgp` `sm86alt` `benchSM` `smq1.6` `sm86-7` `smIN` `SM` `version2` `sm86-9` `asi` `sm86` `sm75-2` `sm75` | **Release name**（`Smooth Motion 2.8.2 - Fixes 2`）与 **asset 名**（`SmoothMotion-2.8.2-R3-GP9-Fixes1.zip`） | **高** —— 且二者自相矛盾：tag `smfix` 的 name 是 "Fixes **2**"、asset 是 "Fixes**1**"；版本号 `2.8.2` 在 4 个 Release 中重复 |
| `xikarioz` | `v0.5.0`（规范）+ `linux-*` 独立线 | tag（**asset 名会滞后**） | 中 |
| `RHI` | `RHI-2.7.6`（规范带前缀） | tag | 低 |

### 4.4 MFG 的 Asset 过滤规则 `[E1]`

18 个 Release 中混有**用途完全不同**的包：

| Release name | Asset | 体积 | 判定 |
|---|---|---|---|
| Smooth Motion 2.8.2 - Fixes 2 | `SmoothMotion-2.8.2-R3-GP9-Fixes1.zip` | 14.60 MB | **正式包** |
| Smooth Motion 2.8.2 - Fixes | `SmoothMotion-2.8.2-R3-GP8-Xbox-Detection.zip` | 14.46 MB | **正式包**（Xbox 限定） |
| Smooth Motion 2.8.2 - Driver Update Fixed | `SmoothMotion-2.8.2-R3-GP9-Driver-61714.zip` | 14.54 MB | **正式包**（驱动限定） |
| Smooth Motion 2.8.2 | `SmoothMotion-2.8.2-R3-GamePass-Picker-Fix.zip` | 13.65 MB | **正式包**（GamePass 限定） |
| … Driver Update Manual | `Manual.zip` | 2.50 MB | **排除** |
| Smooth Motion - Manual Install | `Smooth.motion-manual.zip` | 3.30 MB | **排除** |
| Smooth Motion - Benchmark | `SmoothMotion_Benchmark-1.1-Liquid-Glass.zip` | 5.17 MB | **排除** |
| DLSS MFG 1.2 - ALTERNATIVE PROXIES | `dlssg_sm86-Proxy-Alternatives.zip` | 15.72 MB | 故障回退用 |
| Smooth motion 1.4 / 1.4.1 | `SM86_ASI_Glass_Menu.4*.zip` | 7.3 / 8.1 MB | **ASI 模式** |
| Smooth Motion - ASI Version | `asi.verison.zip` | 0.53 MB | **ASI 模式**（文件名拼写错误 `verison`） |

> ⚠️ **四个 2.8.2 包功能侧重不同**（GamePass 支持 / Xbox 检测 / 617.14 驱动 / KCD2+Trails 修复），上游**从未说明后出的包是否累积前面的修复**。
> ⇒ **禁止静默自动选包**，必须把候选包及其修复清单并列展示给用户（符合任务书 §7）。

---

## 5. 原管理器分析（`BUNNY-19C/DLSSG-30s-manager`）

> 源码已解压至 `_research/upstream/DLSSG-30s-manager/`（36 个文件 / 420 KB）。
> 详细审计见 [`docs/phase0-manager-audit.md`](phase0-manager-audit.md)。

### 5.1 已确认的关键事实 `[E1]`

**下载通路**（`src/DLSSGManager/ModFetcher.cs:214-250`）——6 个源按序降级：

| 序 | id | 官方 | URL 模板 | 形态 |
|---|---|---|---|---|
| 1 | `raw` | ✅ | `raw.githubusercontent.com/{repo}/{ref}/{path}` | 逐文件 |
| 2 | `ghproxy` | ❌ | `gh-proxy.com/https://raw.githubusercontent.com/…` | 逐文件 |
| 3 | `jsdelivr` | ❌ | `cdn.jsdelivr.net/gh/{repo}@{ref}/{path}` | 逐文件 |
| 4 | `ghfast` | ❌ | `ghfast.top/https://raw.githubusercontent.com/…` | 逐文件 |
| 5 | `codeload` | ✅ | `codeload.github.com/{repo}/zip/refs/heads/{ref}` | 归档 |
| 6 | `zipball` | ✅ | `api.github.com/repos/{repo}/zipball/{ref}` | 归档 |

**版本判定**：`RepoPath = "sdli1995/dlssg_for_sm86"`、`RepoRef = "main"`（`:65-66`）——**跟随 main 分支**，无 tag、无 Release、无版本比较。版本号写入标记文件 `.manager-version`（`:72`）。

**签名校验**：实现在 `DeploymentService.cs:146-213`，使用 **`WinVerifyTrust`**，正确区分 `TRUST_E_BAD_DIGEST`（`0x80096010`，拒绝）与 `CERT_E_UNTRUSTEDROOT`（`0x800B0109`，自签名放行），其余未知返回码 **fail-closed**。
证书指纹常量（`ModFetcher.cs:58`）：`85BA66762F851E49148D706915D09026281418E6`（与上游 README 声明一致）。

**数据模型缺口**：`Models.cs` 的 `GameEntry` **没有** `GraphicsApi` / `Store` / `LauncherExe` / `Engine` / `Architecture` / `InstalledPatches` / `VerifiedInstallPlan`；`GameStatus` 只有 `NotDeployed` / `Deployed` / `Modified` / `Missing` / `Unknown`，**没有**任务书 §25 要求的 `Loaded` / `Requested` / `Applied` / `Verified`。

### 5.2 源码审计确证的缺陷（`docs/phase0-manager-audit.md`，含 `文件:行号`）

> 以下为独立审计线的结论，**已逐条核对行号**。

**① 签名链断裂，断口正是注释声称已覆盖之处（重构第一优先级）** `[E1]`
- 正确实现**存在**：`DeploymentService.cs:95-213` 的 `WinVerifyTrust`，fail-closed 语义与句柄释放均正确 → 可直接提取复用。
- 但**下载路径没有调用它**：`ModFetcher.Verify`（`ModFetcher.cs:817-871`，唯一调用点 `:646`）只用 `X509Certificate.CreateFromSignedFile`（**仅解析证书块，不校验摘要**）+ `Subject.Contains("DLSSG")` + `Thumbprint` 比对。
- **且在官方源上指纹不符时仅记警告后放行**（`:857-868`）⇒ **官方源上 pin 完全不生效，实际只剩 6 字符的弱检查**。
- 测试只断言「指纹可读取」而**不断言相等**（`test/Harness/Program.cs:2274`）⇒ 证书轮换不会让任何测试变红。
- `DeploymentService.cs:46-53` 的注释**精确预见了这个漏洞**，但只在自己类里堵住了。

**② 部署非事务 + 恢复盲信 JSON 路径** `[E1]`
- `Deploy` 是「前置检查（很细）+ 就地写 + 记记录」，**无 Stage / Validate / Rollback**；DLL 与 INI **分两步非原子写**（`:488-494`）；异常只删 `.dlssgtmp`（`:577-588`）。
- `Restore` 回填备份时**从不读取 `BackupItem.Sha256`**（字段定义在 `Models.cs:125`，恢复路径 `:705-720` 从未使用），且 `Path.Combine(game.RenderDir, b.FileName)` 与 `b.StoredPath` **均无逃逸约束** ⇒ **配置驱动的任意文件写入**。

**③ 版本标记会被抹掉** `[E1]`
- `TryWriteVersionMarker(versionLabel ?? "")`（`ModFetcher.cs:662`）在版本探测失败时**写入空标记，抹掉已知的版本号**。

**④ 依赖环的确切位置** `[E1]`
- `ModSource.cs:103/266` ⇄ `ModFetcher.cs:206/914` ⇄ `DeploymentService.cs:924` 三者互相引用，仅因单程序集而合法。

**⑤ UI 层占比** `[E1]`
- `MainWindow.xaml.cs` + `.Actions.cs` + `.Protection.cs` 合计 **1,518 行 = .cs 总量 7,620 行的 19.9%**，且承担持久化与事务编排。

**⑥ 功能缺失** `[E1]`
- **无任何 DX11/DX12/Vulkan 判定**（全仓零命中；`d3d12` 的命中全是代理 DLL 文件名）。
- 商店**仅覆盖 Steam**。
- 「渲染 EXE」靠 **15 项文件名黑名单 + 体积排序猜测**（`Detection.cs:261-282`）。
- `GameEntry` **缺 9/10 项**必需字段；`Store` 被塞进自由文本 `Notes`（`MainWindow.Actions.cs:409`）。
- `GameStatus` 的 5 个值是**文件一致性语义**，与 `Requested`/`Applied`/`Verified` **不可映射**。

**⑦ 行数修正（相对任务书）** `[E1]`
- `test/Harness/Program.cs` 实测 **2,297 行**（任务书标注 1,927）。
- `ModFetcher.cs` 970 / `DeploymentService.cs` 939 / `Gpu.cs` 548 与任务书一致。

**⑧ 必须补的白名单项（新架构必然踩到）** `[E1]`
- 按 Release Asset 分发时，`github.com/.../releases/download/...` 会 **302 跳转到 `objects.githubusercontent.com`**，而原白名单（`ModFetcher.cs:34-46`，共 7 个域）**不含它**，会被 `IsAllowedAddress` 直接拒绝。
- ⇒ 新架构的白名单**必须包含** `objects.githubusercontent.com`，并为其补 `TestUrlPolicy` 用例。

**⑨ 应删除的资产** `[E1]`
- `extra-proxies/d3d12.dll`（**10 MB 二进制**）的授权状态已明确（`THIRD_PARTY_NOTICES.txt:4-11`：*不在本项目授权范围*）。识别旧构建**只需那个 SHA-256 常量**，文件本身不应留在仓库中。

---

## 6. 未知事项清单（编码前须解决）

| ID | 事项 | 为何重要 | 解决方式 |
|---|---|---|---|
| **U-01** | 上游文档称「发布包根目录含四个工具类代理」，但 **git 仓库根目录只有 `version.dll`**；该仓库又无 Release Asset | 决定「部署几个代理」 | 实机核对；查 commit 历史 |
| **U-02** | 原管理器「只部署一个代理」 vs 上游「多代理共存不冲突，但一个就够了」（`dlssg_sm86.ini:3-4`）是否功能等价 | 影响安装规划正确性 | 实机 A/B 对比 + 日志 `configuration.proxies{active,standby[]}` |
| **U-03** | `sdli1995` README 称源码 GPLv3，但**仓库无 LICENSE 文件** | 法律边界 | 阅读 `THIRD_PARTY_NOTICES.txt` 全文 |
| **U-04** | `NVIDIA/nvapi` 的 `NOASSERTION` 许可具体条款 | 能否再分发 `nvapi64.dll` | 读官方 LICENSE |
| **U-05** | `RankFTW/RHI` 是否真的含 NVIDIA Profile/DRS 管理代码 | §10.6 的研究假设 | ✅ **已确认**：含 `DlssPresetService.DriverSettings.cs` 等，非仅 HDR 工具 |
| **U-06** | Ground Branch 的渲染 EXE / API / 代理模式 | §48 首批测试目标 | **实机测试**；上游零命中，不得引用 |
| **U-07** | `310.1` 与 `310.9` 变体对 RTX 20（SM75）的适用差异 | 变体选择逻辑 | 读 `docs/INSTALL.md` 的 `## Two build variants` |
| **U-08** | `fg_gate_*` 日志的 JSONL 字段 schema | 运行验证能否程序化解析 | 实机产生一份 Level 2/3 日志 |
| **U-09** | ~~本机**无 .NET SDK**~~ | ~~阻塞 Stage 2 起全部开发~~ | ✅ **已解决**（2026-09-26）：.NET 8 SDK **8.0.425** 已按官方方式安装，构建与测试实测通过 |
| **U-10** | ~~除 NPI 与 RHI 外，是否还有第三个独立来源确认上述 6 个 ID~~ | — | ✅ **已解决**：已找到 **4 个独立来源**（NPI、RHI、NVPIRevamped、DLSS5-Feeder），其中 2 个核心 ID 由 4 源确认，见 §3.2 |
| **U-11** | MFG 的 INI 键名与 zip 内部清单（未下载二进制） | Smooth Provider 的文件布局 | 需在获得合法样本后核对 |
| **U-12** | **RTX 30 上驱动是否实际执行这些 DRS 设置** —— 无任何单一来源记录 | 直接决定 Profile 写入能否真正生效；也是 v5 §69「Latest Compatible」的判据基础 | **本机实机自测**（现已具备 RTX 3070 Ti Laptop 条件）；在取得证据前一律按 `Experimental` 处理 |
| **U-13** | `0xB053C379` / `0xB01B8B02` 的真实语义；`0xB03A4546/47` 的权威命名（Flip Metering vs Flip Pacing）；`0x8A2CF5F5` 与 `0x577DD202` 的官方关系 | 影响 UI 文案与函数指针回退表 | 实测 + 继续检索官方来源 |
| **U-14** | `ItsAdeline/NVSmooth30` 与 `Aryoksini/DLSS5-Feeder` 的多个改造版共存时的**叠加行为**（是否可同时安装） | 影响 Provider 的互斥检查设计 | 实机验证；无证据前按「只允许一个 Smooth Motion 方案」处理 |

---

## 7. 许可证矩阵与行为边界

| 组件 | 许可 | 与本项目（拟 MIT）兼容 | 允许 | 禁止 |
|---|---|---|---|---|
| `BUNNY-19C/DLSSG-30s-manager` | **MIT** | ✅ | 复用代码（保留声明） | — |
| `sdli1995/dlssg_for_sm86` | 无 LICENSE 文件（README 称 GPLv3） | ❓ U-03 | 查询元数据、用户端下载、校验、管理安装 | **打包/再分发任何二进制** |
| MFG 版 | **无 LICENSE** | ❓ | 同上 | 同上 |
| `xikarioz/Smooth-Motion-RTX30` | **专有 EULA** | ❌ | 学习思路、查询 Release、**用户端自行下载**、作为 External Provider 调用 | 见下 |
| `NVIDIA/nvapi` | `NOASSERTION` | ❓ U-04 | 待确认 | **不得假定可再分发** |
| `RankFTW/RHI` | **GPL-3.0** | ❌ **不兼容** | **仅可学习思路** | **禁止复制任何源代码** |
| 内嵌 `nvngx_dlssg.dll` / 重编译内核 | NVIDIA 第三方（上游声明 *not relicensed*） | ❌ | 用户端自驱动获取 | 打包、再授权 |

### 7.1 xikarioz EULA 逐条要点 `[E1]`（`EULA.txt`）

1. 覆盖编译产物 + 文档 + 更新；**不发布源码，不授予源码许可**
2. 允许在自有/控制的计算机上**个人非商业**使用
3. **禁止**：(a) 再分发/再许可/出售/出租/发布，或**以其他方式使软件可被第三方获得**；(b) **repackage / rebrand / 与其他产品或安装器捆绑**；(c) 收费；(d) 声称作者或去除署名；(e) 未经许可商用。**允许自用备份**
4. 与 NVIDIA **无关联**；**不含任何 NVIDIA 软件或二进制**
5. **"Do not use the Software with anti-cheat protected or competitive multiplayer titles."**
6. 禁止逆向/反编译
7. AS IS；*"IT IS AN EXPERIMENTAL CONSUMER PREVIEW BUILD"*
8–10. 责任限制 / 违反即终止 / 可分割

**由此推出的本项目行为边界**：

| 可做 | 不可做 |
|---|---|
| 查询 Release 元数据、用 API 做更新检查 | 自建镜像 / 转发下载 |
| **只提供跳转官方 Release 的链接，由用户端自行下载** | 把二进制打包进本项目仓库或 CDN |
| 使用官方 `SHA256SUMS*.txt` 校验 | `/VERYSILENT` 静默安装、改名、去署名、重打包、捆绑 |
| 展示证据等级、修复清单、驱动要求并标注出处 | 对 xikarioz 逆向提取内部 profile |
| 标注「非官方，与 NVIDIA 及作者无关联」 | 对反作弊保护或竞技多人游戏启用 |

> **额外事实**：xikarioz 的 `SUPPORT.md` 明确列出 *"Not supported: … Automated updates (none; download new releases manually)"*
> ⇒ 本项目提供的更新检查是**增值能力**，只能做到「检查 + 由用户执行」。

---

## 8. 实现风险登记册

| ID | 风险 | 等级 | 依据 | 缓解 |
|---|---|---|---|---|
| **R-01** | ~~本机**无 .NET SDK**，原项目无法构建与测试~~ → **已消解**（2026-09-26） | ~~**阻塞**~~ → **已关闭** | §0.2 实测 | 已装 .NET 8 SDK **8.0.425**（Microsoft 官方源）；实测 `dotnet build -c Release` → **0 警告 / 0 错误**（2.97 s），Harness → **343 通过 / 0 失败 / 10 跳过**（跳过项需 Mod 文件） |
| **R-02** | 版本判定在非 SemVer 仓库必然失败（MFG tag 全无语义） | **高** | §4.3 | Provider 专属 `IVersionParser`；**禁止**全局 `new Version(tag)` |
| **R-03** | `/releases` 顺序与时间字段均不可靠 | **高** | §4.2 T1–T3 | 显式版本解析 + 交叉校验 + 平台/预发布过滤 |
| **R-04** | 选错 Asset（Linux 包 / Manual / Benchmark / Xbox / GamePass） | **高** | §4.2 T5、§4.4 | Provider 级 `SelectReleaseAsset()` + 排除词表 + 单元测试 |
| **R-05** | **驱动更新会破坏已装配置** | **高** | MFG `#16`、`#20`（`driver_validation_rejected`） | 记录安装时的驱动指纹，变更时主动提示重装 |
| **R-06** | payload ≈ 180 MB，全量下载代价大 | 中 | §4.1 | 逐文件哈希比对、只下载变化文件、断点续传 |
| **R-07** | 杀软误报导致安装包被隔离 | 中 | MFG `#15`（`Trojan:Script/Wacatac.B!ml`） | 内置说明 + **永不建议关闭 Defender** |
| **R-08** | 游戏保护模块抢占代理名 | 中 | `sdli1995#577`（HoYoKProtect 隔离 `version.dll`） | 代理冲突检测 + 自动换入口 + `Proxy Conflict` 终止 |
| **R-09** | 「FG 菜单不出现」被误判为安装失败 | 中 | `sdli1995#601/#600/#562` | 运行验证必须读补丁日志归因，而非只看文件 |
| **R-10** | 配置破坏性变更（`Optimized` bool→int；`altnative/`→`alternatives/`；`winhttp.dll` 移除） | 中 | §5.1、`0.3.0`/`0.3.2` 变更 | Schema 版本化 + 键级迁移 + 迁移前备份 |
| **R-11** | Undocumented Setting ID 的稳定性 | 中 | §3.4 | 标记 `Undocumented`；写入前读取原值、支持精确恢复；失败回退 |
| **R-12** | 许可证污染（GPL-3.0 代码混入 MIT；上游二进制再分发） | 中 | §7 | `THIRD_PARTY_NOTICES.md`；**禁止打包任何上游二进制** |
| **R-13** | 写 DRS 需要管理员权限的假设未证实 | 中 | 官方文档零权限声明 | **运行时探测**，不写死假设 |
| **R-14** | ~~**本机 HAGS 未启用**（注册表 `HwSchMode` 不存在）~~ → **已消解**（2026-09-26 更正：`HwSchMode = 2`，HAGS **已启用**） | ~~**高**~~ → **已关闭**（保留检测能力） | 两条独立路径重测一致（`Get-ItemProperty` + `reg query`）；上游定性仍成立但本机不适用 | **无需用户操作**。实机验证时仅需确认运行时生效——`ConfiguredOn` 是配置层事实，**不等于**已证明 `Enabled`（见 §0.2 勘误块与 `ARCHITECTURE_PLAN.md` §36.1） |
| **R-15** | ~~**GitHub 仓库地址未提供**~~ → **已消解**：仓库 `PLA0185/RTX30-FrameGen-Manager`（PUBLIC，默认分支 `main`） | ~~**高**（阻塞交付）~~ → **已关闭** | 基线提交 `b0a6424` 已推送，本地与远端 SHA 一致；`origin`=用户仓库、`upstream`=BUNNY 原仓库 | **无需用户提供**。后续按 `origin` 推送，**禁止** `git push --force`（除非用户明确授权） |
| **R-16** | Self Update 依赖本项目自身的 Release 链路（Actions + Tag） | 中 | v5 §61、§65 | 与 R-15 同解；发布资产命名规则须在流程中固定 |
| **R-17** | 本机驱动 617.14 相对社区白名单偏新 | 中 | §2.4 | 实机验证时记录**精确驱动指纹**入兼容库；xikarioz 类 Provider 会按设计 fail-closed，属预期行为而非缺陷 |

---

## 9. 状态与下一步

**Phase 0 交付物**：
- 本文件（`docs/RESEARCH_NOTES.md`）
- [`docs/ARCHITECTURE_PLAN.md`](ARCHITECTURE_PLAN.md) —— 架构设计，覆盖任务书 §11 的 20 项
- [`docs/phase0-manager-audit.md`](phase0-manager-audit.md) —— 原管理器源码审计附录

**进入 Stage 2 的前置条件**：

| # | 条件 | 状态（2026-09-26） |
|---|---|---|
| 1 | 解决 **R-01**（工具链），否则无法编码与测试 | ✅ **已满足**：.NET 8 SDK **8.0.425** 已装；实测 `build -c Release` **0 警告 / 0 错误**，Harness **343 通过 / 0 失败 / 10 跳过** |
| 2 | 消解 **U-01 / U-02 / U-09**（实机验证与用户决策） | ⏳ **部分**：涉及「需用户拍板」的部分已由本轮指令解除（见下方四项阻塞结论）；技术未知项仍见 §6，须靠实机自测 |
| 3 | 建立「签名校验链完整性」的失败测试（针对原管理器下载路径的校验缺口） | ⏳ **Stage 2 实施项**（`ModFetcher.Verify` `:817-871` 缺 `WinVerifyTrust`，且官方源指纹不匹配仅告警放行） |
| 4 | 交付仓库（原 R-15） | ✅ **已满足**：`PLA0185/RTX30-FrameGen-Manager`，基线提交 `b0a6424` 已推送 |
| 5 | HAGS 环境前置（原 R-14） | ✅ **已满足**：`HwSchMode = 2`（配置层）；**运行时生效仍待实机确认**，不得写成已验证 |

**四项阻塞的处理结论（2026-09-26）**：.NET 8 SDK 按官方方式安装；HAGS 经重测**更正为已启用**；GitHub 仓库已建立并完成首次推送；代理部署策略已形成设计（见 [`ARCHITECTURE_PLAN.md`](ARCHITECTURE_PLAN.md) §37），**均不再作为待用户决策项**。

**Stage 2 起点约束**：先抽取 Core（哈希 / 签名 / 路径 / 模型）**再**拆分程序集——因 `ModSource ⇄ ModFetcher ⇄ DeploymentService` 目前依赖同程序集（`ModSource.cs:103/266`、`ModFetcher.cs:206/914`、`DeploymentService.cs:924`）。
