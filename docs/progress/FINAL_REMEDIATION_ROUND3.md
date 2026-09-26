# 第三轮最终整改报告：DRS + 事务执行链

> 任务书：《RTX30_FrameGen_Manager_第三轮最终整改任务书.md》（1268 行）
> 本轮范围：**不新增 Stage、不重做 Phase 0 / Stage 1–11、不大范围重构** —— 只修完阻断真实 NVIDIA Profile 与安装事务闭环的 P0，然后重新回归并生成新的 Release Candidate。

## Baseline

| 项 | 值 |
|---|---|
| 基线提交 | `5f90132a1f0f96c3fcc40708d5f8708b71e0dd2d` |
| 最终提交 | 见文末「版本归档」 |
| 提交数 | 33 |
| 仓库 | `PLA0185/RTX30-FrameGen-Manager`（PUBLIC，`main`），无 force push |

## DRS Session Ownership

**修好了。** `NvApiDrsAdapter.FindApplicationProfile(string)` 自己拥有完整会话生命周期：`Open(null)` → `FindApplicationByName` → `GetProfileInfo` → `finally { Close(); }`。**调用者不需要维持 `_session`。**

修之前：`NvidiaProfileService.FindApplication` 直接转发给要求 `_session != IntPtr.Zero` 的低层入口，而 UI 路径从不打开会话 —— **每次都以「没有已打开的 DRS 会话」失败**。测试全绿，因为当时的替身**也不检查会话**。

低层 `FindApplication` 保留为「需要已打开会话」的入口，两个入口的前置条件在测试里被分别断言。

## FindApplicationByName

真实驱动调用，**ABI 层 PASS**。`--nvapi-smoke` 中报告为 `B1 FindApplicationByName（ABI 层）: PASS（调用返回了结果，未抛异常）`。

## GetProfileInfo

**真正调用了 `NvAPI_DRS_GetProfileInfo`**（`0x61cd6fd6`，此前只定义 ID 从未使用）。新增常量与委托 `DrsGetProfileInfoDelegate`。**未解析 / 返回非 OK / `profileName` 为空时一律返回 `NotFound` 并说明原因**，不猜。

## Profile Name Resolution

**从 `NVDRS_PROFILE.profileName` 读取，不再用 `NVDRS_APPLICATION.userFriendlyName` 冒充。**

布局取自官方头文件：`nvapi.h` **L24592** 确认 `typedef NVDRS_PROFILE_V1 NVDRS_PROFILE`（**默认就是 V1**）—— `version`=0 · **`profileName`=偏移 4**（4096 字节）· `gpuSupport`=4100 · `isPredefined`=4104 · `numOfApps`=4108 · `numOfSettings`=4112，**总大小 4116**。

`ProfileNameOffset` 改为 `internal` 以便 Harness 断言它等于 4、且不等于 4104（旧错误值）。

**⚠️ 边界**：本机 `FindApplicationByName` 返回 `-166`，`GetProfileInfo` 一直是 `NOT_RUN`，**走不到读 Profile 名那一步**。上述断言守的是**回归**（代码没有改回旧的错误偏移），**不是**「读取行为在真机上正确」。

## Read Gate

**开启，依据是一次真实运行。** `ReadCallsProven = true` —— `--nvapi-smoke --loop`：**驱动被触达 200/200、异常 0**。

## Write Gate

**关闭，且这是本轮最重要的结论之一。**

原先只有**一个** `private static readonly bool DriverCallsProven = true`，它一次决定了 `CanRead` / `CanDelete` / `CanSave` / `CanWrite`。**200 次真实读取通过，就自动把「写、保存、删除」全部开放了 —— 而这三条路径从未在真实驱动上跑过一次。** 这直接违反项目红线「读权限不能推出写权限」。

现在拆成五个各自独立的门：

| 门 | 值 | 依据 |
|---|---|---|
| `ReadCallsProven` | `true` | 200 次真实读取，0 异常 |
| `WriteCallsProven` | **`false`** | 写入未通过（见下） |
| `DeleteCallsProven` | **`false`** | 同上 |
| `SaveCallsProven` | **`false`** | 同上 |
| `ApplicationLookupProven` | `true` | 真实调用过且未崩（返回 `-166`） |

`CanWrite => CanRead && WriteCallsProven && CanDelete && CanSave` —— **写路径要求整条往返都被证明**。

**一处需要解释的不矛盾**：`ApplicationLookupProven = true` 而真实调用返回 `-166`。这个门回答的是「**这条调用有没有被真实跑过且没崩**」，而 `DrsApplicationLookup.Found` 回答的是「**它找到了什么没有**」。**应用确实没找到，而这条查找确实已被证明。**

## Temporary Profile Write Smoke

**已完整实现并真实执行过；未通过；按任务书允许的方式保持 fail-closed。**

命令：`DLSSGManager.Harness.exe --nvapi-write-smoke`。它创建 `RTX30FGM-SMOKE-<GUID>` profile + `RTX30FGM-SMOKE.exe` 绑定，在**自己创建、自己删除**的 Profile 上走完整往返，全程 `try/finally` 清理，**从不触碰用户现有 Profile 或 Ground Branch**。

实测（多次运行，含诊断）：

```
✓ 创建临时 Profile    : code=0
✓ 绑定测试 EXE        : code=0        ← 早期运行成功
    原始状态          : Absent value=0
× 写入受控值          : NvAPI_DRS_SetSetting 返回 -160。
× 保存                : （当时被能力门拒绝，已修）
    读回              : Absent value=0（期望 1）
    恢复后状态        : Absent
✓ 解绑测试 EXE        : code=0
✓ 删除临时 Profile    : code=0
```

**错误码含义一律未查明，列为未知、不猜**：

| 码 | 出现处 | 状态 |
|---|---|---|
| `-160` | `SetSetting`（`0xB0D384C0`）与 `DeleteProfileSetting` 同码 | **未知** |
| `-137` | `SetSetting`（`0xB0CC0875` / `0xB03A4546`）与删除同码 | **未知** |
| `-167` | `CreateApplication`（后期运行） | **未知** |
| `-166` | `FindApplicationByName` / `DeleteApplication` | **已知**：`NVAPI_EXECUTABLE_NOT_FOUND` |

这些码**不在任何可得的官方头文件中定义**（`nvapi.h` 仅注释提到 `-166` 的名字；`vapi_lite_common.h` 零命中；`_research/nvapi/` 只有 4 个 `.h`），网络检索亦未找到。

**已排除的两个嫌疑（有代码依据）**：`settingType` = `TypeDword` = 0（= `NVDRS_DWORD_TYPE`）、`settingLocation` = `LocationCurrentProfile` = 0（= `NVDRS_CURRENT_PROFILE_LOCATION`），二者均与官方枚举首项一致。

**从数据读出的事实（非推测）**：错误码**随设置 ID 变化**，**与写入的值无关**（写 1 与写 0 相同）；**同一设置上 `SetSetting` 与 `DeleteProfileSetting` 返回同一个码**；**`SaveSettings` 返回 `code=0`**（保存路径本身可用）；**创建/绑定/解绑/删除 Profile 均 `code=0`**（同一会话同一权限下成功，故「整体权限不足」这个解释很弱）。

**⚠️ 一个未解释的反常，如实记录**：**早期运行时 `NvAPI_DRS_CreateApplication` 返回 `code=0`（成功），后期运行返回 `-167`** —— 而中间**没有改动过 `CreateApplication` 的实现**。**不能写成「同一操作稳定失败」，也不能写成「只是偶发」。**

**`-167` 的成因假设已被否证**：我曾推测它是「绑定已存在（残留）」。修复清理逻辑后真的去解绑，**解绑返回 `-166`（`EXECUTABLE_NOT_FOUND`）—— 说明那个绑定根本不存在**。残留假设被自己的证据推翻。**教训：相邻数字很容易被当成一组语义（`-166` 已知、`-167` 相邻），但相邻不等于同义。**

## Cleanup

**每次都干净。** 每一轮运行都成功创建并成功删除临时 Profile，**无残留**。

**并修好了一个自我阻塞**：解绑原先只在 `applicationCreated == true` 时执行，于是**绑定失败时残留不会被清理，下一次运行仍以同样方式失败 —— 一次失败变成永久失败**。已改为「只要 Profile 是本轮的（名字带 GUID，不可能是用户原有的）就尝试解绑」。**清理必须比创建更宽松。**

## UI Payload Isolation

**修好了。** `MainWindow.Actions.cs` 两处主流程（单游戏运行、批量预览）由 `PayloadDirectory: SourcePath` 改为 **`null`**，由工作流按 `ProviderId + ResolvedVersion` 决定路径。

修之前：所有 provider、所有版本**共用界面上那一个 mod 源路径** —— **「Provider + 版本隔离」只存在于代码里，从未在主流程生效**。批量预览那处更糟：**预览看到的文件与真正执行时用的文件来自两个不同的地方，而用户同意的是后者。**

**⚠️ 边界**：`MainWindow.Actions.cs` **不在 Harness 的编译白名单里**（只由 WPF 工程编译），因此「UI 现在确实传 null」**无法被自动断言**，只能靠代码守住。Harness 验证的是它依赖的机制（payload 目录按 provider 与版本分开、不与 staging 重合）。**不把「机制正确」写成「UI 行为已验证」。**

## Plan–Deployment Consistency

**发现并修复了一个真实缺口。**

`FilesToDeploy` 曾是**整个解压目录**：`PayloadManifest` 扫描全目录树（`SearchOption.AllDirectories`）得到 312 个文件 → `ProviderPayloadFiles` → `InstallPlanner` 原样全用 + 追加代理 → **313**。

而真实部署路径（`DeploymentService.Deploy(game, ModSource, ...)`）**只写代理入口 + INI**。

**实测对比（真实 MFG payload）**：

| | 修复前 | 修复后 |
|---|---|---|
| payload 文件数 | 312 | 312 |
| `FilesToDeploy` | **313** | **6** |
| 判定 | FAIL | **PASS（6 / 312）** |

修复后的 6 个：`Manual\Dinput8\dinput8.dll` · `Manual\Version\version.dll` · `Manual\Winmm\winmm.dll` · `payload\loader\version.dll` · `payload\native\version.dll` · **`dlssg_sm86.ini`**。

**规则有依据，不是按目录名猜**：保留 payload 中所有命中 `ModSource.IsKnownProxyName` 的文件（0.3.3+ 会把这些作为「待机代理」一并写入，planner 事先不知道是哪几个，必须按名字放行）；**无条件放行 `ModSource.IniName`**（INI 由管理器生成，payload 里没有，但部署一定会写）；补上 `proxyChoice` / `asiChoice`。

**第 2 条是实测才暴露的**：第一次改完是 5 个且**不含 `dlssg_sm86.ini`** —— 而部署一定会写它。**单向校验会把刚写下的 INI 判成「计划外的意外文件」，把一次成功的安装判成失败。** 若只改一半就提交，就会把一个「计划误导用户」的问题换成一个「安装必然失败」的问题。

**⚠️ 一个被撤回的推论**：我曾据此推断「正常安装会被误判失败并回滚」—— **错了**。一致性校验是**单向**的（只问「实际部署的文件里有没有计划没提过的」），313 vs 2 不会失败。**本缺陷的性质是「计划向用户展示了错误的信息」，后果是误导，不是事务失败。** 两者严重程度不同，不能混为一谈。

## Rollback on Mismatch

**已实现。** `PlanExecutionResult` 新增：

- `FilesWereWritten { get; init; }` —— provider 是否真的往盘上写了文件，**与 `Ok` 无关**；
- `RollbackRequired => FilesWereWritten && !Ok`。

**四个返回点全部设 `FilesWereWritten = true`**，其中 `install.Ok == false` 那处是关键：**provider 可能在写了部分文件之后才失败，`Ok == false` 不代表盘上什么都没有** —— 保守假定写了，**多余的回滚远比漏掉的回滚便宜**。

**Workflow 失败路径**：`filesWritten = execution.FilesWereWritten;` —— **修法只有一行**。修之前 `filesWritten` 只在成功后才置位，于是**安装已写文件、一致性检查失败、回滚什么也没做，游戏目录留下残留，报告却说这次什么都没写**。这是任务书称为「当前最危险的事务漏洞」的那一处。

## Missing Deployment Record

**改为 Failed。** 这正是任务书点名的「注释写 `Not a pass`、代码却 `return true`」—— 也是我上一轮**验证错了一半**的地方（我的断言钉住的正是错的那一半）。

现在：`steps` 记录「部署记录为空…已按失败处理」，返回 `false` + **`FilesWereWritten = true`**（`Install()` 已返回 Ok，文件很可能在盘上）。

## Rollback Failure Handling

**两半都已落地。**

- **状态表达**：`WorkflowResult.RollbackIncomplete { get; init; }` —— 「尝试过回滚、但没成功」。与「根本没有需要回滚的东西」不同：前者意味着机器上可能还留着本次写下的内容。在 `Finish` 的两条回滚路径上分别置位。**成功侧断言**已加（「回滚成功时不声称回滚未完成」）。
- **重新扫描确认**：`Finish` 在 `provider.Restore(...)` 之后新增 `ScanForLeftovers(plan, game)` —— **只看 `Restore` 的返回值不够**（它回答「这次调用有没有报错」，不回答「目录有没有回到原样」）。扫描发现残留时：步骤消息写明文件名、`errors` 写「回滚未完成」、`rollbackIncomplete = true`。

**⚠️ 失败侧断言留空并留证**：要让工作流走到「回滚尝试失败」，需要 `MfgSmoothProvider` 能注入还原失败，而它内部调用的是 `DeploymentService.Restore` **静态方法**。两个方向都试过并留证：换替身会因兼容性查询落空停在 `NeedsConfirmation`（走不到安装）；强转会抛 `InvalidCastException` 把套件从 887 项腰斩到 807 项。**留一条假断言比留空更有害**；成功侧断言至少能挡住「恒为 true」的实现。

## Missing Profile Policy

**按 `NotFound` 处理。** 找不到 Profile 时不猜测其他 Profile，也不自动创建。

`Create` 与 `NeedsConfirmation` **不是「现在还做不到」，而是「现在做不诚实」**：`CreateProfile` 虽已实现，但它是一次真实写入，**在 `WriteCallsProven = false` 的当下走这条路只会被能力门拒绝**。与其把它摆成一个点了会失败的可选项，不如把当前策略讲清楚。

## Journal Identity / Auto-Create Rollback

**Journal 现在记录真实 Profile Identity**：`ApplicationExe` · `WasProfileCreated` · `WasApplicationCreated`（`init` 属性，不改既有构造签名）。

**为什么必需**：回滚必须知道它究竟动过什么。只记 `ProfileName` 不够 —— 同一个 Profile 名下可能有多个应用绑定，而「本次是不是创建了它」决定了回滚**删掉它**还是**只恢复设置**：**删掉用户原本就有的 Profile 或绑定，比留下一点残留严重得多。**

**回滚端已消费**：`NvidiaProfileService.Rollback` 在恢复设置之后、且 `failed == 0` 时，**若 `journal.WasProfileCreated` 才调用** `DeleteProfileByName(journal.ProfileName, journal.ApplicationExe)`；删不掉则 `failed++`（**删不掉就是没回滚干净**，留一个空 Profile 会一直出现在用户的驱动面板里）。

新增 `IDrsAdapter.DeleteProfileByName`（自己管会话：`Open(profileName)` → 先解绑 → 删 Profile → `_profile` 置零 → `SaveUngated` → `finally Close()`）。**先解绑再删 Profile**：某些驱动版本在 Profile 仍持有应用时不接受删除。

**位置必须在 `finally { _adapter.Close(); }` 之后** —— 它自开自合会话，放在前面会打断外层会话。

**一正一反两条断言**：① `WasProfileCreated = true` → `DeletedProfiles` 包含它；② **`WasProfileCreated = false` → `DeletedProfiles` 必须为空**（没有第 ②，一个「永远删」的实现也能满足第 ①，**而那会删掉用户的 Profile**）。

**⚠️ 边界**：**当前运行路径不会创建 Profile**（`NotFound` 策略，`Create` 分支受写能力门阻塞），**因此这条回滚路径在真机上无法被真实走到，只能靠替身验证。不声称「已真机验证」。**

## MFG Real Payload Plan Smoke

见「Plan–Deployment Consistency」—— `--mfg-asset-smoke` 新增 plan-only 段：真实 312 文件 payload → 临时 `GameEntry`（`RenderDir` 指向系统临时目录下的一次性路径）→ **只调 `InstallPlanner.Plan(...)`，不安装、不写任何游戏目录** → 三态判定（`FAIL` 全量 / `NOT_DETERMINED` 计划未 Ready / `PASS` 确实筛选过）。

## NVAPI Smoke Report States

拆成阶段状态段（P1-15）：

```
A1 NVAPI Load                    : PASS
A2 CreateSession                 : PASS
A3 LoadSettings + GetBaseProfile : PASS
A4 Read Calls（200 次）          : PASS
B1 FindApplicationByName（ABI 层）: PASS（调用返回了结果，未抛异常）
B2 Application Found             : NOT_FOUND
C  GetProfileInfo                : NOT_RUN
D1 Write / ReadBack / Delete / Save : NOT_RUN（见 --nvapi-write-smoke）
D2 Cleanup                       : NOT_RUN（见 --nvapi-write-smoke）
```

**`FindApplicationByName` 分成 B1（ABI 层）与 B2（业务层）两段** —— 这正是 `-166` 不再可能被读成 PASS 的原因。**A4 是三态**：不带 `--loop` 时为 `NOT_RUN`（不是 `FAIL`）—— 阶段表第一版自己犯过「把未运行报成失败」的错。

## Build

```
dotnet restore                              : exit 0
dotnet build -c Release --no-incremental    : 0 错误 / 0 警告
dotnet test                                 : exit 0（本项目无 VSTest 用例，静默返回 0 属预期）
```

## Harness

**897 通过 / 0 失败 / 10 跳过**（基线 878 项全保留，累计 +19）。

**「原 878 项未被删除」的证据（核对，不是声称）**：

```
基线 5f90132 的 Check 调用数 : 993
当前          的 Check 调用数 : 1012      净增 19（与通过数净增 19 完全吻合）
被删除的独立 Check 文本数     : 1
  已不在: Check("没有部署记录时如实报「无从核对」，不报「一致」",
```

**唯一被替换的那条，正是 P0-09 修正的断言** —— 它断言的正是错的那一半。**不是丢失覆盖，是修正一个断言了错误行为的断言**，且已改为「按失败处理」并**新增**一条「没有部署记录时要求回滚」。

**§18 七组测试覆盖**：

| # | 内容 | 状态 |
|---|---|---|
| 1 | Profile Lookup Session | ✅ |
| 2 | Profile Name | ✅（回归护栏，非真机行为验证） |
| 3 | Capability Gates | ✅ |
| 4 | Payload Isolation UI Path | ✅（仅机制层） |
| 5 | Transaction Failure | ✅ |
| 6 | Empty Deployment Record | ✅ |
| 7 | Rollback Failure | 🟡 成功侧已覆盖，失败侧因夹具阻塞留空并留证 |

## RC Packaging

`scripts/package-release.ps1` exit 0。版本 **1.9.3**。

## EXE

`artifacts/release-candidate/win-x64/DLSSGManager.exe`（63.0 MB）
SHA256 `B82958DF25628AC38ACCCCFFD460862ACED88B72F56EA65BFFD5B2261B2291A6`

## ZIP

`artifacts/RTX30-FrameGen-Manager-win-x64-1.9.3.zip`（57.7 MB）
SHA256 `9C958F9090D2E2C9AD9540A256B715B90E92B7DC1E9F764E9E989C01C315C838`

**从全新临时目录解压启动验证**（针对**最终 ZIP**，不是 publish 目录）：

```
临时目录: %TEMP%\rtx30fgm-rc-887c0ddb（全新创建）
解压顶层: DLSSGManager.exe · LICENSE · README.md · THIRD_PARTY_NOTICES.txt
18 秒后仍存活 : True
句柄非 0      : True
正常关闭      : True
临时目录已清理: 是
```

**⚠️ 未核验项**：`%LOCALAPPDATA%\DLSSGManager` 不存在，**未定位到日志文件**，因此「日志无 Fatal/Unhandled」**未核验**。实测的是进程稳定性、句柄与正常关闭；**「日志干净」不是实测的**。

## Pending User Validation

1. **视觉 UI 与实机游戏验证** —— 需人工：启动 `artifacts\release-candidate\win-x64\DLSSGManager.exe`，在深色主题下悬停工具栏按钮，确认 ToolTip 文字颜色可读。
2. **真实游戏端到端** —— 部署代理 DLL 到真实游戏目录、启动游戏确认帧生成生效。
3. **Ground Branch 第一次真实 E2E** —— 本轮红线仍禁止触碰。

## Known Risks

1. **写入路径未通过真实验证**，写能力门保持关闭；`CanWrite = false`。`-160` / `-137` / `-167` 含义未知，**不猜**。
2. **`CreateApplication` 在不同时期返回 `code=0` 与 `-167`**，差异**未解释**。
3. **`Create` 分支在真机上走不到**，其回滚路径只经替身验证。
4. **`RollbackIncomplete` 的失败侧无断言**（夹具阻塞，已留证）。
5. **§18.4 的 UI 侧无法自动断言**（不在编译白名单），只能靠代码守住。
6. **本轮「日志干净」未核验**。
7. **`-166` 之外的 DRS 错误码缺少官方定义**，任何依赖错误码语义的改进都需要先补上这份资料。

## 最终判定

**DRS + Transaction Remediation: PARTIAL**

**判定理由（只有一条实质理由与一条能力理由）**：

- **P0 层面**：11 项中 **9 项完成**；**P0-05（临时 Profile 写入 Smoke）未通过** —— 但任务书 §23 的验收条件明确写的是「**临时 Profile Write / Save / ReadBack / Delete / Cleanup smoke 完整通过，或 Write 继续 fail-closed**」，**这条满足的是后半个分支**；**P0-10 的两半都已落地**（状态表达 + 重新扫描确认），仅失败侧断言因夹具阻塞留空并留证。
- **因此判定的实质理由不是「有 P0 没做」，而是：写入路径仍未在真实驱动上被证明，且其错误码缺少官方定义无法推进。** 这是能力的边界，不是本轮的遗漏。
- **P1 层面**：**6 项全部完成。**
- **§18 / §19 / §21 / §22**：全部执行；测试覆盖与全量回归有可核查证据。

**未标 PASS 的原因**：任务书 §23 的 19 个勾选项里，**「临时 Profile 写入 smoke 完整通过」这一项没有勾上**（它满足的是「或保持 fail-closed」分支）；另有四项交付物（视觉 UI、实机游戏、真实写入、Ground Branch E2E）按定义需要人工或真机，本轮红线明确禁止。**把 PARTIAL 写成 PASS 会让「读通过不自动开放写」这条红线在报告层面破功。**

**未标 FAIL 的原因**：**没有任何一项是在能力允许下被跳过的。** 写入路径的每一次失败都伴随真实的错误码与实测记录；`Create` 分支走不到是能力门正确工作的结果；两处留空（P0-10 失败侧、§18.4 UI 侧）都写明了确切的技术原因与后续路径。

## 版本归档

- **Baseline**：`5f90132a1f0f96c3fcc40708d5f8708b71e0dd2d`
- **Final Commit**：`3befa5d1fd871e2e7e14b6721e7cbe6f86a571a8`（报告与文档提交后会有新的 HEAD，代码产物以本条为准）
- **提交数**：33
- **未打 Stable Tag**（按要求）
- **未 force push**
