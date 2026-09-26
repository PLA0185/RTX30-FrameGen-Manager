# 第四轮 · 自主闭环整改报告（§28）

> **状态：草稿，等待 Pass B 结果填入。** 标 `[待 Pass B]` 的位置在审查返回后更新。
>
> 基线 `4cf3462` · 本文件写作时 HEAD `7d78de1` · 36 个已推送提交 · 无 force push · 未打 Stable Tag。

---

## 1. 本轮做了什么

按《自主闭环整改总指令》执行，**实现者自己完成「实现 → 测试 → 独立审查 → 发现问题 → 修复 → 再审查」的闭环**，用户不充当外部 QA。

### §5 的已知问题 A–H（全部完成）

| 项 | 内容 | 关键证据 |
|---|---|---|
| **A** | NVAPI 官方状态码 | 四个码在 `NVIDIA/nvapi` 的 `nvapi_lite_common.h` **逐行核对**（L320/L344/L350/L351）；分类器 `NvApiStatus` 接入全部**面向用户**的报错路径 |
| **B** | 提权前后写 Smoke 对照 | **非提权 `-137`、三门保持 `false`；提权后往返全通（写→保存→读回一致→删除→保存→恢复 `Absent`），三门置 `true`** |
| **C** | Provider 能力拆分 | `RequiresSmoothMotionDrs` / `SupportsSmoothMotionDrs` / `ProvidesDlssFrameGeneration` 三个字段 |
| **D** | `dlssg-sm86` 不再被强制走 DRS | UI 两个构造点按 `provider.RequiresSmoothMotionDrs` 决定 `ProfileSettings` |
| **E** | MFG payload 正规化 | `PrepareCanonicalPayload` 把嵌套发行包转成 canonical 布局；实测 `ModSource.IsValid` **False → True** |
| **F** | `FilesToDeploy` = 最终目标路径清单 | 真实 payload **312/445 文件 → 4**（`dinput8.dll` / `version.dll` / `winmm.dll` / `dlssg_sm86.ini`） |
| **G** | `DeploymentFileSource` | `PlannedFile` 带 `SourceKind`；**`Generated` 的文件不再被拿去要求它存在于 payload** |
| **H** | Plan = 实际写入 | `FilesToDeploy` 与 `PlannedFiles` **同源派生**；Executor 的一致性校验是**双向**的 |

### §29 顺序中的其余条目

§7（提权对照）· §8（smoke EXE 名 GUID 唯一化 + 删后反查）· §10 · §11 · §13 · §14 · §15 · §16 —— 全部落地。

---

## 2. 验证结果（数字与口径）

| 项目 | 结果 |
|---|---|
| `build -c Release --no-incremental` | **0 错误 / 0 警告** |
| Harness（**本地**） | **911 通过 / 0 失败 / 10 跳过** |
| Harness（**干净环境**） | **891 通过 / 0 失败 / 9 跳过** |
| `Check(` 断言总数 | **1035**（第三轮 1012，**净增 23**）；本地通过 911（第三轮 897，净增 14）——差额来自跳过项与未执行分支，非丢失 |
| `--nvapi-smoke --loop` | **200 次真实读取，驱动被触达 200/200，异常 0** |
| `--nvapi-smoke` | A1–A4 PASS · B1 ABI 层 PASS · B2 NOT_FOUND · C/D NOT_RUN |
| `--nvapi-write-smoke` | **非提权**：`-137`，三门 `false`（预期行为）· **提权**：往返全通，三门 `true` |
| `--mfg-asset-smoke` | 真实 payload · plan-only `FilesToDeploy = 4` · 判定 PASS |
| `--network-smoke` | PASS |

**★ 口径说明（必须写进结论里）**：**干净环境的真实数字是 891 通过 / 0 失败 / 9 跳过，不是本地的 911。** 差的 21 项经逐 Section 求差确认**全部**来自 `extra-proxies/d3d12.dll` —— 该文件被 `.gitignore` 排除（设计上由使用者自备），干净树里相关断言走「不存在则如实跳过」分支。**这不是隐藏的开发机依赖，报告里不能把 911 写成通用数字。**

---

## 3. 独立审查与整改

### Pass A（两个独立审查者，各自独立复现）

**1 × P0 / 4 × P1 / 14 × P2**。

**P0**：`InstallPlanExecutor` 在 payload 根目录校验代理入口，忽略了项目自己规定的 `altnative/` 布局 —— 真实 MFG payload 有三个可部署入口名，其中两个落在 `altnative/`，于是**任何 MFG 安装都会失败**，而错误信息是「payload 中的『dinput8.dll』未通过校验」（payload 里明明有它）。
**两个审查者用两种不同手段各自复现了它**：一个用自建探针跑真实 provider 的 6 组场景；另一个跑真实 312 文件资产并用原始 `WinVerifyTrust` 探针排除了签名因素。

**4 条 P1 全部修复并各有回归断言**：planner 入口候选集用扫描名 · 零写入的失败重部署回滚掉用户既有安装 · `Adopt` 不写 `ProviderId` · 批量预览漏传 `AllowProtected`。

### Pass B（重跑）

**结论：0 P0 / 1 P1 / 9 P2。** 第一次 Pass B 任务运行 5 轮未返回，在新 HEAD 上重跑后才返回；审查者开工时撞上实现者正在做回退验证的中间态（工作树里有一处故意注入的缺陷），它**用 `git archive` 导出 HEAD 复测**并明确警告，没有采信工作树。

**那 1 个 P1 是本轮修复自己引入的**：`Deploy` 的 `catch` 已经自己回滚了，而我把「收回 `FilesWritten`」写成**无条件**的 —— 于是回滚**失败**时（快照建不出来、还原副本被 AV 占用）写进去的半成品留在游戏目录，而外层因为看到「没写过」就跳过了 `Provider.Restore`。**修复方向对，但条件错了。** 现在由 `Rollback(...)` 的返回值决定（`bool`，只有两边都真的撤销了才收回结论）。

**审查者的方法论提醒（已采纳）**：**注入式反例必须建在 `git archive` 导出的 HEAD 上** —— 实现者会边审边改，工作树可能正处于变异态。

**§18 的状态**：Pass A 报出缺陷 ⇒ 计数重来；Pass B 报出 1 个 P1 ⇒ **计数再次重来**；**Pass C 是新周期的第一轮，尚未运行。**

### Pass C

**[待 Pass C]**

---

## 3b. 修复的断言守护状态（**不写成「已覆盖」**）

**「有回归断言」与「断言真的能失败」是两件事。** 本轮对每条新增断言做了**回退验证**（临时回退修复、确认断言变红、再恢复），两次给出相反结果：

| 修复 | 回退后的结果 | 状态 |
|---|---|---|
| Pass A 的 P0（执行器用 `source.DllPath` 校验代理） | 变红 | ✅ **有效** |
| planner 入口候选集用 `ProxyCandidates` | 变红 | ✅ **有效** |
| 零写入失败重部署（P1-2 两半） | 一半有效：Executor 层变红；**第一版 workflow 层断言仍全绿**（第二次运行在 planner 阶段就 fail-closed，没进 `Deploy` 的 try） | ✅ 已改到 Executor 层 |
| 「写了又自己回滚」不再二次回滚（Pass B 的 P1） | 变红 | ✅ **有效** |
| 成功部署删除事务快照（P2-2） | 变红（`restore/<id> 下仍有 _pending`） | ✅ **有效** |

**三条修复明确没有断言守护**（如实记录，且写清需要什么才能覆盖）：

1. **«回滚失败»侧**（应为 `FilesWritten=true` / `RollbackHandled=false`）—— **当前架构构造不出**：`Deploy` 在写之前就 `Snapshot`，写成功后的收尾步骤不会失败到需要回滚；而 `AppPaths.Root` 是只读静态属性、在首次访问时解析一次，测试内无法把 `restore` 占成文件。**需要给 `Deploy` 注入一个「快照提供者」接缝才能覆盖。**
2. **`ScanForLeftovers` 的双向误判** —— **构造不出**：需要串起「部署前目录里已有同名外来文件 → 备份 → 失败 → 回滚 → 正确还原 → 断言不报残留」三段。**需要一个能造出「部署前已有同名外来文件」的夹具。**
3. **`Adopt` 的 `ProviderId`** —— **零覆盖，且这与环境里有没有 Mod 文件无关**：夹具写的是 4 字节假 PE，而 `Adopt` 只接管「本项目签名」或「哈希 = 已分发 `d3d12.dll`」的文件 ⇒ `adopt.Ok` 必为 false ⇒ **必然走早退分支，断言永不评估**。**需要真实签名的代理，或 `extra-proxies/d3d12.dll` 的字节。**（此条由 Pass B 审查者独立发现，我此前把它写成「TestAdopt 覆盖」，**那个说法是错的**。）

---

## 4. 我做过的自我纠正（本轮最值得记录的部分）

| 我曾经的结论 | 事实 | 教训 |
|---|---|---|
| 「`-160`/`-137`/`-167` **在任何可得的官方资料里都查不到**」 | **官方头文件对 200+ 个码都有定义** | 把「本机副本里没有」当成「官方没有」 |
| 「一致性校验是**单向**的」 | **是双向的**，只是当时两边恰好都成立 | 结论对但理由错；**读到第一个 `if` 不等于读到判定出口** |
| 「预览与执行的反作弊判据**不同源**」 | 两者**等价**（`HasKernelAntiCheat` 就是 `Protection?.HasKernelAntiCheat == true`） | **两个写法不同不等于两个东西** |
| 「打包脚本**首次运行失败**、重跑成功」 | **脚本从未失败** —— PowerShell 管道重置了 `$LASTEXITCODE` | **不可重现的失败是待查项，不是缺陷** |
| 「P2 清单里有 **6 处**裸数字报错路径」 | 实际剩 **1 处**（我在那之后已修 9 处） | **清单会朝两个方向过期**，写进报告前要重新核对 |
| 「`Adopt` 的断言在无 Mod 文件环境下不可达，有 Mod 文件时由 `TestAdopt` 覆盖」 | **任何环境下都不可达** —— 夹具自造假 PE，与 Mod 文件无关 | **早退守卫 `if (!ok) return;` 会把零覆盖伪装成通过** |
| 「状态位由『是否尝试过』决定就够了」 | **同一位置连续修错两次**（先从不收回 → 删掉用户安装；再无条件收回 → 半成品无人清理） | **必须由动作的真实结果决定** |

---

## 5. 已知缺口与未核验项（不写成 PASS）

1. **`Adopt` 的 `ProviderId` 断言在无 Mod 文件环境下不可达** —— 有 Mod 文件时由 `TestAdopt` 覆盖；该断言**未被默认套件执行**。
2. **`--nvapi-write-smoke` 的提权路径无自动测试** —— 需要人工点 UAC，属外部真实阻塞。
3. **UI 行为无自动断言** —— `MainWindow.Actions.cs` / `MainWindow.xaml.cs` 不在 Harness 编译白名单，只能靠代码守住。
4. **Rollback 失败侧断言** 仍受静态方法夹具限制（§22 审计已列）。
5. **「日志干净」未核验** —— 未定位到日志文件。
6. **运行时 `CanWrite` 仍为 `false`** —— 三个能力门是 `internal static` 进程内状态：默认 `false`、每次启动重置、生产路径不跑 smoke。**提权路径已被证明，但生产运行不会自动获得该状态。**
7. **零消费者/零生产者声明**（如实标注，不写成「已生效」）：`NvApiStatus.RequiresElevation` · `IsNameCollision` · `ProviderRegistry.AllHealth()` · `DeploymentFileSource.ExistingReusable` · `ProviderMetadata` 的 8 个字段。

---

## 6. 环境事实

- **Harness 默认套件完全离线**；驱动交互只发生在显式 smoke 中；网络只发生在 `--network-smoke` / `--mfg-asset-smoke`。
- **本机**：RTX 3070 Ti Laptop，驱动 `32.0.16.1714`，HAGS 启用 · .NET 8 SDK `8.0.425` · gh `2.101.0`（`PLA0185`）。
- **红线遵守**：全程**未触碰真实 Ground Branch**，**未修改任何真实 NVIDIA Profile**，写入只发生在自建的临时 Profile（且每次都被成功删除并通过反查确认无残留）。

---

## 7. 判定

| 完成条件 | 状态 |
|---|---|
| Build PASS | ✅ 0 错误 / 0 警告 |
| Harness PASS | ✅ 本地 911/0 · **干净环境 891/0** |
| Integration Review | ✅ §17 列出 |
| Real Smoke PASS | ✅ 见 §2（提权路径为人工触发） |
| **Independent Code Review PASS** | **[待 Pass B]** |
| No Known P0 | ✅ Pass A 的 P0 已修 + 回归断言 |
| No Known P1 | ✅ Pass A 的 4 条 P1 已修 + 回归断言 |
| Documentation Claims Match Code | ✅ §22 审计的 5 条过时陈述已在白板与报告中更正 |
| Packaging PASS | ✅ 两次全新 clone 各跑一次打包：`exit=0`、EXE 63.0 MB + ZIP 57.7 MB |

**DRS + Transaction Remediation: [待 Pass B 结果后填写]**

**Ready for User Ground Branch E2E: [待 Pass B 结果后填写]**

> 若为 `NO` 且不存在外部阻塞，则继续整改，不停。
