# Report-vs-Code Audit（§22）

> 基线 `4cf3462` → 本文件写作时的 HEAD `98275aa`。本文对仓库里**所有声称 PASS / 已验证 / 已知边界**的陈述逐项核对，只以**源码与实测**为准。
> 目的是回答 §22 的三个问题：**报告写 PASS 的东西，源码是否真的实现？测试是否真的覆盖？Smoke 是否真的执行？**

## 一、已被本会话推翻的旧陈述（必须在新报告中更正，不得沿用）

| 出处 | 原陈述 | 现状（证据） |
|---|---|---|
| `FINAL_REMEDIATION_ROUND3.md` L85 / L282 | 「`-160`/`-137`/`-167` 含义**在任何可得的官方资料里都查不到**，列为未知、不猜」 | **已推翻**。四个码在 `NVIDIA/nvapi` 官方 `nvapi_lite_common.h` 中逐行核对到：`-137 INVALID_USER_PRIVILEGE` / `-160 SETTING_NOT_FOUND` / `-166 EXECUTABLE_NOT_FOUND` / `-167 EXECUTABLE_ALREADY_IN_USE`。**当时的理由是「本机副本里没有」，那不等于「官方没有」。** |
| 同上 L282 | 「**写入路径未通过真实验证**，写能力门保持关闭」 | **部分推翻**。提权下写入往返**全部通过**（写 → 保存 → 读回一致 → 删除 → 保存 → 恢复 `Absent`），`WriteCallsProven`/`SaveCallsProven`/`DeleteCallsProven` 全为 `true`。**非提权下仍失败（`-137`），那是预期行为。** |
| 同上 L131 | `FilesToDeploy` 313 → **6** | **数值已变**。现在是 **4**（G 项改用目标叶子名并去重）。 |
| 同上 L132 | 判定 `PASS（6 / 312）` | **数值已变**：`PASS（4 / 312）`。 |
| 同上 | 「`-167` 的成因此假设被否证」与「Round 11 成功 / 后期失败**未解释**」 | **已解释**。`-167` = `EXECUTABLE_ALREADY_IN_USE`（「该应用已存在于另一个 Profile 中」）。固定 EXE 名导致**第一次成功、之后永远失败**；改为 GUID 唯一后实测 `-167` 消失。**「未解释项」已闭合。** |

**处理原则**：旧报告是历史记录，**不追改**（项目惯例：第二轮报告也刻意保留了已闭合的缺口列）。**但这些陈述绝不能被新报告沿用。**

## 二、逐模块核对（源码 / 测试 / Smoke 三项）

### NVAPI Read
- **源码**：`NvApiDrsAdapter` 的读路径 + `ReadCallsProven = true`。
- **测试**：能力门三档断言（只读为真时写门保持关闭 / 写门未证明时 `CanWrite` 为假 / 四门全开时才为真 / 抽掉一门立刻收回）。
- **Smoke**：`--nvapi-smoke --loop` 实测 **驱动被触达 200/200、异常 0**。
- **结论**：三者一致，**PASS 成立**。

### NVAPI Write
- **源码**：`WriteTo` / `SaveUngated` / `DeleteFrom` 绕过能力门（为挣门而设计），公开入口 `Write`/`Save`/`Delete` 保留门；`NvApiStatus.Report` 输出官方名称与含义。
- **测试**：能力门断言覆盖「未证明时不开放」；**提权路径无自动测试**（需要管理员，见「外部阻塞」）。
- **Smoke**：**非提权** → `-137`，三门保持 `false`；**提权** → 往返全通，三门置 `true`（实测输出已验证）。
- **结论**：**提权下 PASS 成立；非提权下 fail-closed 成立**。两者都不是「未验证」。

### MFG Asset / Payload
- **源码**：`MfgSmoothProvider.PrepareCanonicalPayload`（把嵌套发行包正规化成 canonical 布局）。
- **测试**：3 条（正规化后 `IsValid` 为真 · canonical 含 INI 模板 · **只有不可部署入口名时不正规化**）。
- **Smoke**：真实 312 文件 payload，实测 `IsValid` **False → True**、备用入口 2。
- **结论**：**PASS 成立**。

### MFG Install Plan / `FilesToDeploy`
- **源码**：`InstallPlanner.BuildPlannedFiles` 产出 `PlannedFile[]`（带 `SourceKind`），`FilesToDeploy` 从**同一次调用**派生。
- **测试**：4 条（区分 payload/生成文件 · 两视图完全一致 · 计划选定的代理都在可部署名集合里 · 可部署名与扫描名确实是两个集合）。
- **Smoke**：真实 payload `FilesToDeploy = 4 / 312`，判定 `PASS`。
- **结论**：**PASS 成立**。

### Profile Lookup
- **源码**：`FindApplicationProfile` 自管会话；`GetProfileInfo` 读 `NVDRS_PROFILE.profileName`（偏移 4）。
- **测试**：调用前无会话仍能定位 · 定位时自己开了会话 · 低层入口仍要求会话 · `ProfileNameOffset == 4` 且 `!= 4104`。
- **Smoke**：`--nvapi-smoke` 报告 `B1 ABI 层 PASS` / `B2 NOT_FOUND` / `C NOT_RUN`。
- **结论**：**ABI 层 PASS**；**「读取行为在真机上正确」未验证**（本机 `-166` 走不到 `GetProfileInfo`）—— **报告不得把回归护栏写成真机行为验证。**

### Rollback
- **源码**：`Finish` 双向（`FilesWereWritten` 决定是否回滚）· `ScanForLeftovers` 回滚后重新扫描 · `RollbackIncomplete` 暴露失败 · `Rollback` 消费 `WasProfileCreated` 删除自建 Profile。
- **测试**：回滚成功时不声称未完成 · 删除自建 Profile · **不删除非自建 Profile** · 回滚失败被如实报告 · 回滚失败时保留真实现场 · 同一 journal 不回滚两次。
- **结论**：**成功侧 PASS**；**失败侧断言因夹具阻塞留空**（`DeploymentService.Restore` 是静态方法）—— **这是已知缺口，不是 PASS。**

### UI Workflow
- **源码**：`PayloadDirectory: null` 两处 · `ProfileSettings` 按 `provider.RequiresSmoothMotionDrs` 决定两处。
- **测试**：**无** —— `MainWindow.Actions.cs` **不在 Harness 编译白名单里**。
- **结论**：**UI 行为无法自动验证，只能靠代码守住。报告必须这样说。**

### RC Packaging
- **源码**：`scripts/package-release.ps1`。
- **Smoke**：脚本 exit 0、内部跑 Harness、发布目录恰好 4 个文件、**从全新临时目录解压启动 18 秒稳定 + 句柄非 0 + 正常关闭**。
- **结论**：**PASS 成立**；**「日志干净」未核验**（未定位到日志文件）—— **不得写成通过。**

## 三、本会话新引入但**尚无消费者**的声明（如实标注，不得写成「已生效」）

| 声明 | 消费者 | 说明 |
|---|---|---|
| `IPatchProvider.RequiresSmoothMotionDrs` | `MainWindow.Actions.cs` 两处 | 已生效 |
| `IPatchProvider.SupportsSmoothMotionDrs` | **无** | 声明性元数据；**只有测试读** |
| `IPatchProvider.ProvidesDlssFrameGeneration` | **无** | 同上 |
| `NvApiStatus.RequiresElevation` | **无**（文案已含可操作提示） | **不得写成「已用于决策」** |
| `NvApiStatus.IsNotFound` / `IsNameCollision` | **无** | **「在有人真正用它之前，它只是一组未被验证的语义」** |

## 四、外部阻塞（§27 允许的停止理由）

**提权那一半对照实验需要人工点 UAC。** 非提权侧已自动完成；提权侧在本次会话中由用户点过一次 UAC 完成后已取得结果，**但它不是一个可无人值守重复的自动化步骤**。

## 五、结论

- **没有发现「报告写 PASS 而源码没实现」的情况。**
- **发现 5 条旧陈述已被推翻或数值已变**（第一节），**新报告不得沿用**。
- **发现 2 处已知缺口**（Rollback 失败侧断言 · UI 行为无法自动验证）与 **1 处未核验项**（日志干净），**都必须写进新报告的 Known Risks，不能写成 PASS**。
- **发现 5 个无消费者的新声明**（第三节），**必须如实标注**。
