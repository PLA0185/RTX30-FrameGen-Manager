# 最终补全验证与 Windows EXE 打包

> 基线 `c64fecc`（Stage 2–11 完成时的末端提交）。

## 1. 总览

| 项 | 值 |
|---|---|
| **Baseline Commit** | `c64fecce12497258695708ee14ead258690d6509` |
| **Final Commit** | 见文末 |
| **Build Result** | **0 警告 / 0 错误** |
| **Harness Result** | **720 通过 / 0 失败 / 10 跳过**（exit 0） |
| **UI Process Smoke** | **PASS** |
| **Visual UI Validation** | `Pending User Validation`（本工具无法看见桌面 GUI，不伪造视觉验证） |
| **NVAPI / DRS Status** | 官方头文件已取得（**MIT**），真实适配器**未实现**，仍 **fail-closed** |
| **Real Asset Download Smoke** | **未执行**（见 §7） |
| **Publish Mode** | Release |
| **Runtime Identifier** | `win-x64` |
| **Self-contained** | **true**（证据见 §5） |
| **Single-file** | **Yes** |

## 2. UI Process Smoke Test（§5）

用当前 Release 构建启动 WPF 程序，两次独立运行：

| 检查项 | 结果 |
|---|---|
| 进程启动后不立即崩溃 | ✅ 稳定运行 15 秒 / 18 秒 |
| 主窗口句柄出现 | ✅ 非 0（16711932 / 4916144） |
| 主窗口标题 | `DLSSG 30 系管理器  v1.9.3` |
| 启动日志 | ✅ **无 Exception / Fatal / Unhandled** |
| 退出时只关本次 PID | ✅ 该 PID 消失 |
| 不误杀其他进程 | ✅ `dotnet` 2 / `node` 7 仍在 |

启动日志内容（`%APPDATA%\DLSSGManager\manager.log`）：

```
===== 启动 DLSSG 30 系管理器 =====
界面主题已切换为 深色
Mod 文件源未就绪：尚未获取 mod 文件。点「下载 / 更新 Mod 文件」自动下载。
数据目录：C:\Users\linxi\AppData\Roaming\DLSSGManager
提示：游戏若装在 Program Files 下，写入需要管理员权限，可用右上角按钮重启。
— 首次启动：自动检测上游版本并获取 Mod 文件 → D:\DLSSG Manager\mod
显卡： NVIDIA GeForce RTX 3070 Ti Laptop GPU · 驱动 32.0.16.1714 与本机匹配：SM86 路由即作者实卡验证的路径。
```

**诚实边界**：本环境无法"看见"或操作桌面 GUI，因此下列**视觉与交互项未验证**，不作声明：
默认进入「总览」· 左侧导航显示 5 + 2 · 页面切换 · 同时只高亮一项 · 游戏库页外观与改动前一致 · 高级项可折叠 · 窗口缩放布局。

这些属于 `Pending User Validation`（清单见 `docs/progress/STAGE_10.md`）。

## 3. 真实 NVAPI / DRS 前置（§3）

**已按 §3.1 从官方来源取得所需资料，且许可明确允许**：

| 资料 | 来源 | 许可 |
|---|---|---|
| `nvapi_interface.h`（32 KB，554 行） | `NVIDIA/nvapi` 官方仓库 | **MIT**（文件头 SPDX 明确） |
| `nvapi.h`（1.4 MB，23 031 行） | 同上 | **MIT** |
| `License.txt` | 同上 | `nvapi*.lib` 为 MIT |

**函数 ID 已从官方头文件逐条核对**（不再是记忆或社区转述）：

```
NvAPI_Initialize                 0x0150e828   （与 Phase 0 记录一致）
NvAPI_Unload                     0xd22bdd7e
NvAPI_DRS_CreateSession          0x0694d52e   （一致）
NvAPI_DRS_DestroySession         0xdad9cff8
NvAPI_DRS_LoadSettings           0x375dbd6b   （一致）
NvAPI_DRS_SaveSettings           0xfcbc7e14   ← 新增
NvAPI_DRS_LoadSettingsFromFile   0xd3ede889
NvAPI_DRS_SaveSettingsToFile     0x2be25df8   ← 新增
NvAPI_DRS_FindProfileByName      0x7e4a9a0b   （一致）
NvAPI_DRS_CreateProfile          0xcc176068
NvAPI_DRS_GetProfileInfo         0x61cd6fd6
NvAPI_DRS_SetSetting             0x577dd202   （一致）
NvAPI_DRS_GetSetting             0x73bf8338   ← 新增
NvAPI_DRS_EnumSettings           0xae3039da
NvAPI_DRS_DeleteProfileSetting   0xe4a26362   ← 新增，解锁写入路径
NvAPI_DRS_CreateApplication      0x4347a9de
NvAPI_DRS_FindApplicationByName  0xeee566b2
```

**结构体布局已取得**（`NVDRS_SETTING_V1`，`nvapi.h:24486-24514`，`#pragma pack(push, 4)`）：

```
NvU32   version
NvU16   settingName[2048]      ← NVAPI_UNICODE_STRING_MAX = 2048
NvU32   settingId
enum    settingType            (NVDRS_SETTING_TYPE, 5 值)
enum    settingLocation        (NVDRS_SETTING_LOCATION, 4 值)
NvU32   isCurrentPredefined
NvU32   isPredefinedValid
union { u32PredefinedValue | binaryPredefinedValue | wszPredefinedValue | u64PredefinedValue }
union { u32CurrentValue    | binaryCurrentValue    | wszCurrentValue    | u64CurrentValue }
```

`NVDRS_BINARY_SETTING = { NvU32 valueLength; NvU8 valueData[NVAPI_BINARY_DATA_MAX]; }`
`NVAPI_SETTING_MAX_VALUES = 100`

**这些资料的存放位置**：`_research/nvapi/`（该目录已被 `.gitignore` 排除，不随仓库分发——与「不转发上游二进制/大文件」的既有约定一致）。

### 状态：仍是 fail-closed，**未实现真实适配器**

原因：实现它需要一次性完成「P/Invoke 全签名 + 结构体封送 + 只读路径 + 测试 + 真机验证」，属于一个完整的独立工作单元；把它塞进本轮剩余预算，最可能产出一个**从未在真实驱动上跑过**的适配器——正是本项目红线最反对的那类成果。

**因此本轮明确不实现它，也不改变 `NvApiDrsAdapter` 的 fail-closed 行为**（`IsAvailable=false`、`CanWrite` 要求 `CanDelete`、所有操作返回带原因的失败）。

**前置条件已解除**：官方 MIT 头文件与全部所需 ID / 布局均已取得（此前记录为「缺官方头文件」）。下一轮可直接实现。

## 4. 打包（§8–§13）

沿用仓库**既有**发布约定（`.github/workflows/release.yml`），未另建第二套流程：

```
Release · win-x64 · --self-contained true
-p:PublishSingleFile=true
-p:IncludeNativeLibrariesForSelfExtract=true
-p:EnableCompressionInSingleFile=true
-p:DebugType=none
```

**Single-file 的采用依据（§8.1）**：这是仓库既有约定，且本轮实测其产物**能启动、窗口能出现、稳定运行**（§2、§6），故沿用。若将来出现原生 DLL / 资源 / Self Update 相关问题，应退回「自包含目录 + 主 EXE」而非强撑单文件。

**版本号**：取 `csproj` 的 `<Version>1.9.3</Version>`——未凭空宣布新版本，也未标 Stable。

## 5. Self-contained 验证（§15）

**配置层面已确认**（最强证据是 `runtimeconfig.json` 的 `includedFrameworks` 字段）：

```json
"includedFrameworks": [
  { "name": "Microsoft.NETCore.App",        "version": "8.0.31" },
  { "name": "Microsoft.WindowsDesktop.App", "version": "8.0.31" }
]
```

框架依赖发布此处会是 `"framework"`（单数）而非 `includedFrameworks`。

旁证：RID 输出目录含 **242 个运行时程序集**（`System.Private.CoreLib.dll`、`PresentationFramework.dll` 等）；自包含 EXE **63.0 MiB** vs 框架依赖 DLL **0.50 MiB**。

**诚实声明**：

```
Self-contained configuration verified
Clean machine without .NET runtime not physically tested
```

本机装有 `Microsoft.WindowsDesktop.App 8.0.31`，**无法**在无 .NET 的机器上物理验证；未为此外卸载用户运行时。

## 6. 干净目录启动验证（§14）

```
ZIP 解压到全新临时目录 C:\Users\linxi\AppData\Local\Temp\dlssg_rc_clean_59da90e9
（不从源码目录运行）
        ↓
启动 DLSSGManager.exe
        ↓
进程存活 18 秒 · 主窗口句柄 4916144 · 标题「DLSSG 30 系管理器  v1.9.3」· 工作集 231.1 MiB
        ↓
只终止本次 PID（该 PID 已消失）· 其他进程未受影响（dotnet 2 / node 7）
        ↓
临时目录已删除
```

**验证对象是最终 ZIP 中的内容**，不是 `bin\Release`。

## 7. 未执行项（诚实记录）

| 项 | 状态 | 原因 |
|---|---|---|
| **真实 Release Asset 下载 Smoke** | **未执行** | 需要新增 Harness 在线模式并下载 ~100 MB 资产；本轮预算已用于 NVAPI 资料、UI Smoke 与打包交付。这是**下一轮的第一项**。 |
| 真实 NVAPI/DRS 读写 | 未实现 | 见 §3（前置已解除，实现待下一轮） |
| 视觉 UI 验证 | Pending | 本工具无法看见 GUI |
| 实机游戏 / Smooth Motion `Verified` | Pending | 需用户运行游戏 |
| 无 .NET 机器的物理验证 | 未测试 | 见 §5 |

## 8. 交付物

| 交付物 | 路径 | SHA-256 |
|---|---|---|
| **主 EXE** | `artifacts/release-candidate/win-x64/DLSSGManager.exe` | `4DC058688255DBD07AF1B5708DC0140EA64969AECE3E5E4C98814167E1CDBA02` |
| **RC ZIP** | `artifacts/RTX30-FrameGen-Manager-win-x64-1.9.3.zip` | `E61B4F6D03F3A8A1A897A7D3AB55D3E39C2C9C10EF9C7E52757503E76E42785E` |
| SHA256SUMS | `artifacts/SHA256SUMS.txt` | — |
| 可复用打包脚本 | `scripts/package-release.ps1` | — |

**发布目录内容（恰好 4 个文件，已核实）**：

```
66,035,847  DLSSGManager.exe
     1,108  LICENSE
     4,725  README.md
     1,782  THIRD_PARTY_NOTICES.txt
```

ZIP 条目数与发布目录一致（4 个）。**不含**源码、`.git`、`_research`、`test`、Harness、`obj`、开发日志、用户日志、凭据、PDB、上游二进制。

脚本内置防呆：若发布目录出现 `.git*` / `credentials` / `.cs` / `.csproj` / `.sln` / `.pdb`，**立即中止并报错**，而不是默默打包。

## 9. 打包脚本（§17）

`scripts/package-release.ps1`：清理 → 构建 → Harness → `dotnet publish` → 复制声明文件 → 剔除开发期文件 → 防呆检查 → ZIP → SHA256SUMS → 打印路径。**任一步失败即非 0 退出**，不吞错误。

**编码注意**：脚本含中文注释，必须以 **UTF-8 with BOM** 保存——Windows PowerShell 5.1 对无 BOM 的 `.ps1` 按 ANSI 解码，会因中文乱码导致解析失败（本轮实际踩到并修复）。

## 10. GitHub Actions（§18）

`.github/workflows/release.yml` **已存在**且与本轮 publish/package 流程语义一致（self-contained + SingleFile + Inno Setup 安装包 + SHA256SUMS + 仅 tag 触发 Release）。**未另建第二套**。

**本轮未创建 Stable Tag、未创建 Stable Release**（§18 要求），原因：实机游戏验证仍 Pending、真实 NVAPI 尚未实现。

## 11. 最终判定

```
Release Candidate Packaging: PASS
  · Build 0 警告 / 0 错误
  · Harness 720 通过 / 0 失败
  · 发布成功（win-x64 self-contained 单文件）
  · 最终 ZIP 干净目录启动成功

Project Acceptance: PARTIAL / Release Candidate
  · Ground Branch 实机            Pending User Validation
  · Smooth Motion Applied/Verified Pending User Validation
  · 视觉 UI 完整检查               Pending User Validation
  · 真实 NVAPI / DRS              未实现（前置已解除）
  · 真实 Release Asset 下载 Smoke  未执行
```

**不声明** Stable / Fully Verified / Production Ready。

## 12. 用户下一步

1. 用 `artifacts/RTX30-FrameGen-Manager-win-x64-1.9.3.zip` 做**视觉与交互验收**（清单见 `docs/progress/STAGE_10.md` 五条）。
2. 实机游戏验收（`Applied` / `Verified`）。
3. 若需要真实 DRS 读写，下一轮可实现真实适配器——官方 MIT 头文件与全部 ID / 结构体布局**已就位**。
