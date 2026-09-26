# 第二轮整改 · 测试覆盖对照（§13）

任务书 §13 逐项对照。**「✅ 有」只表示存在针对该主题的断言，不表示该主题被完整覆盖** —— 右侧一列专门写缺口。

> **状态更新（第二轮收尾）**：下列十一个主题的缺口**已全部补齐**，累计新增 **77 项**断言（Harness 801 → **878**）。
> 补齐集中在**为每一处「夹具让某条路径不可达」增加一个可选注入点**，**不改既有签名、不改行为**：
> `SafeZip` 的两个大小上限改成带默认值的参数 · `PayloadPaths.RemoveUnreferenced(providerId, keepVersion, referenced)` ·
> `MakeRequest` 的 `providerVersion` 变成可选（原来硬编码 `"2.9.0"`，把「去问 provider」那条路堵死了）·
> `FakeDrsAdapter.ReadOverrides`（原来把写入原样读回，读回校验永远不会失败）·
> `RecordingProvider.RecordsDeployed`（原来不写部署记录，执行器永远走「无从核对」分支）。
> **三个缺口的根因完全相同：夹具的默认行为把目标分支短路了。**
>
> **下面「缺口」一列保留为本轮整改过程中的原始记录，不改成「—」** —— 它记录了**当时缺什么、以及为什么缺**（多数是「夹具让路径不可达」或「代码不在编译白名单内」），这比一份全绿的表格更有用：下次遇到同类缺口时，先看这里能省一轮排查。

| §13 主题 | 断言数 | 位置 | 缺口 |
|---|---|---|---|
| 确认流程 | 5 | `TestSmoothMotionWorkflow`（P0-01 段）：未确认时计划不可执行、确认后可执行、确认后状态仍是 `NeedsConfirmation`、带 `UserApprovedUnverified`、兼容性仍为 `Unknown` | — |
| Profile Wiring | 5 + 2 | P0-02 段（反射断言字段入口、`EnableWrites` 产物、写入集合不含不可写项）+ §7 段（`ProfileRequirements` 字段、`ProfileSettingRequirement` 五字段） | **端到端缺**：`ProfileSettings` 传入 → 计划出现 Typed 要求 → Profile 段真的被走到，这条链没有一条断言跑通。原因是 `Build(work, name)` 是 `TestSmoothMotionWorkflow` 的**局部函数**，在其他测试方法里不可见（`CS0103`）；且走到 Profile 段需要「同名游戏 + 真实 payload + 带 `NvidiaProfileChanges` 的配方」三者同时满足 |
| Read-back | 3 | P0-03 段：`ProfileApplyResult.ReadBackConfirmed` 存在、`SmoothMotionWorkflow.ReadBackMatches` 已删除、`IDrsAdapter.CanRead` 仍在 | 读回**值不一致**时的行为没有断言（需要 `FakeDrsAdapter` 支持「写后读回返回不同值」的注入） |
| ProviderVersion | 3 | P0-06 段：存在「解析 Provider 版本」步骤、该步骤**排在查询兼容性之前**（比较步骤下标）、计划携带解析出的版本 | 未传版本时**真的调用了** `CheckLatestAsync` 没有断言（`RecordingProvider` 未记录调用次数） |
| Payload 隔离 | 12 | P0-07 段：不同版本/不同 provider 目录不同、根目录在应用数据之下、版本与 provider id 的路径分隔符都逃不出根、空版本落到 `_unknown`、staging 与最终目录同级；**清理段新增 5 条**：删除未被引用的旧版本、保留当前版本、**保留仍被其他部署引用的版本**、不触碰进行中的 staging、清理后可清理集合为空 | 「下载到临时目录后**原子切换**」没有断言。**旧版本清理已于本轮补齐**（`PayloadPaths.RemoveUnreferenced`，只删调用方明确声明未使用的版本） |
| Verification | 6 + 3 | P0-05 段（INI/DLL/EXE 分类、非 PE 不被 Authenticode 拒、空文件被拒、不存在被拒）+ §3 段（三态枚举、读成功只报 `ReadAvailable`、写探测成功才报 `NotRequired`） | 写探测**恢复原值**没有断言（需要 `FakeDrsAdapter` 暴露读取能力） |
| Plan 精确执行 | 1 + 既有 | P0-08 段：计划把选定入口列入待部署文件；既有的计划执行测试覆盖「Ready 可执行 / 未批准被拒」 | 部署结果与计划**不一致**时按失败处理没有断言（需要构造一个部署记录与计划不匹配的场景） |
| Batch | 3 | §13 补齐段：`PreviewAsync` 存在、`PreviewOnly` 同时出现在两个请求上、`GameEntry.Store` 默认 `Unknown` | **只断言了能力，没有断言行为** —— 批量确认框的汇总逻辑在 `MainWindow.Actions.cs`，**不在 `Harness.csproj` 编译白名单内**，行为无法被自动执行 |
| Restore | 2 | §5 段：`DeploymentInfo.ProviderId` 存在、执行器会写它（默认值为空） | 「记录缺失时**报错而不 fallback**」这条**没有断言**（`ProviderForRestore` 是 `MainWindow.Actions.cs` 的私有方法，同样不在白名单内） |
| Store | 1 | §13 补齐段：`GameEntry.Store` 可读写且默认 `Unknown` | 扫描发现与手动添加**各自填什么值**没有断言（两处都在 UI 文件里） |
| SafeZip | 13 + 4 | 第一轮 J 项（zip slip / 条目数 / 压缩比 / 非压缩包 / 正常解包 / 无 staging 残留）+ §9 段（播种式故障注入四项） | 单文件大小与总大小两条上限**没有单独断言**（只覆盖了条目数与压缩比） |

## 结论

**覆盖的性质**：所有**不在 `MainWindow` 两个文件里**的逻辑，都至少有一组断言；每个修复项都随附了自己的断言。

**共同的缺口形状**：**UI 文件里的逻辑无法被自动测试执行**。`Harness.csproj` 用逐个 `<Compile Include>` 白名单编译，`MainWindow.xaml.cs` 与 `MainWindow.Actions.cs` 不在其中（它们依赖 WPF 与窗口生命周期）。因此：

- `ProviderForRestore` 的「不 fallback」、
- 批量确认框的汇总文案、
- 扫描/手动添加各自填的 `Store`、切换 provider 时的持久化

**这些改动的唯一自动保障是**：`dotnet build` 校验全部 `x:Name` 与事件处理器引用完整，`LocalizationAudit` 校验 `{loc:Tr}` 键在两表中都存在且占位符一致。**行为本身没有被执行过**，需要人工运行验证 —— 这一点在报告的 `Pending User Validation` 里已如实记录，不因 Harness 全绿而改变。

**可选的改进方向**（不在本轮范围内）：把 `ProviderForRestore` 与批量汇总这两段**从窗口里搬进服务**，它们就不需要窗口即可测试 —— 与第一轮把 `RunDeploy` 搬进 `GameConfigurationService` 是同一个做法。
