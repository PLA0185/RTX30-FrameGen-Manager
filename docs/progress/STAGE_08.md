# Stage 8 — 自动 Smooth 编排

> 基线 `985e8cd` → 本阶段提交见文末。

## Implemented

`src/DLSSGManager/Orchestration/SmoothMotionWorkflow.cs`：

- `SmoothMotionEvidence { None, Installed, Loaded, Requested, Applied, Verified }` —— 数值即强度
- `VerificationSignal(Name, Present, Detail, Strong)` + `VerificationReport.FromSignals`（纯函数）
- `SignalNames`（信号名集中定义，避免报告与断言漂移）
- `WorkflowOutcome { Succeeded, NeedsConfirmation, Blocked, Failed }`
- `WorkflowStep` / `WorkflowRequest` / `WorkflowResult`
- `IWorkflowDetector`（检测 seam：渲染 EXE / 图形 API / 代理占用）
- `SmoothMotionWorkflow.RunAsync` —— 编排全流程

**编排放在服务里，不在窗口里**。把「检测→决策→计划→安装→配置→验证」序列放进点击处理函数，会同时失去可测试性与批量/命令行复用的可能。

## 状态分级（§7.1 的核心）

| 级别 | 含义 | 需要什么证据 |
|---|---|---|
| `None` | 什么都没做 | — |
| `Installed` | payload 在磁盘上 | 文件存在 |
| `Loaded` | 游戏进程真的加载了代理 | 进程模块观察 |
| `Requested` | 已请求驱动启用 | Profile 提交 |
| `Applied` | 驱动接受并保存 | Profile 保存成功 |
| `Verified` | 独立信号一致确认在生成帧 | **≥2 个信号 且 ≥1 个强证据** |

**`Verified` 需要交叉印证**：至少两个独立信号存在，且至少一个是强证据（Debug Bars 观察 / 补丁日志显示生成帧）。因此**「文件复制成功」永远到不了最高级**——单个弱信号在结构上不可能满足条件。

这条规则由 `FromSignals` 纯函数实现，并有 5 项专门测试（含"单条强信号不足以判 Verified"与"两条弱信号也不足以"）。

## 两轮计划生成（本阶段发现的一个真实死锁）

兼容性查询需要安装方式（`InstallMode`）与代理入口（`ProxyAsi`），而这两者来自计划；计划的状态又依赖兼容性判定。

**第一版实现按其自然顺序写，结果死锁**：不完整上下文的查询只能得到 `Partial` → 决策 `Unknown` → 计划 `NeedsConfirmation` → 而「计划确定后复核」的代码以 `Ready` 为前提 → 复核永不执行 → 永远是 `NeedsConfirmation`。测试直接暴露了这一点（10 项失败）。

**修正**：两次生成计划。
1. 第一轮计划**只用于取得 mode 与 proxy 入口**，其状态刻意不被采纳；
2. 用这两项补全 12 维查询；
3. 第二轮计划才是实际执行依据。

## 顺序即安全属性

- **计划未放行之前不写任何东西**（`Blocked` / `NeedsConfirmation` 路径下不下载、不写文件、不开 DRS 会话——有测试）
- **先文件后驱动**：失败时不可能留下「Profile 已为某游戏启用、但其代理从未装成」
- **回滚先驱动后文件**：用户更可能先发现的反常状态是驱动已开而代理不在
- 任一步失败 → 保留**真实错误**（`Errors` 逐个记录，含底层服务返回的原因与回滚结果）

## Tests

新增 30 项（Harness 616 → **646**，原测试未删）：

| 组 | 覆盖 |
|---|---|
| 证据分级 | 单强信号不足 · 两条弱信号不足 · 无信号为 None · 弱证据最远到 Installed · 两信号含强证据才 Verified |
| Blocked 路径 | Unknown API → Blocked · **不下载** · **不写文件** · 原因保留 |
| 需确认路径 | 仅静态渲染证据 → NeedsConfirmation · 不写文件 |
| 正常路径 | 计划就绪 → Succeeded · 文件确实部署 · **只到 Installed** · **不等于 Verified** · 步骤记录完整 |
| 交叉验证 | 多信号后判为 Verified |
| 部署失败 | 失败 · **不配置 Profile**（`OpenCount == 0`）· 原因保留 · 不留文件 |
| 驱动失败 | 编排失败 · **文件被回滚** · 代理已移除 · 回滚记录为步骤 |
| Profile 成功 | 编排成功 · 状态 **≥ Requested** · **不等于 Verified** |
| 取消 | 以取消结束，而非静默成功 |

全部使用 fake detector / fake DRS / fake asset fetcher，**不触碰真实游戏目录与真实 NVIDIA Profile**。

## Baseline vs Current Build

```
Build:    0 warnings / 0 errors  →  0 warnings / 0 errors
Harness:  616 passed / 0 failed / 10 skipped  →  646 passed / 0 failed / 10 skipped
dotnet test: exit 0（无 VSTest 项目）
实机验证：未执行
```

## Known Issues

- 编排**未接入 UI**（Stage 10 范围）；当前只能由测试与将来的调用方驱动
- **`Verified` 在本阶段无法被自动达成**：强证据（Debug Bars / 补丁日志）需要真实游戏运行或用户确认，属 `Pending User Validation`
- 结果未持久化（`WorkflowResult` 返回给调用方；落库属 Stage 9）
- Harness 10 项跳过测试仍需 Mod 文件

## Remaining Risks

| ID | 风险 | 说明 |
|---|---|---|
| **S8-01** | 编排未经真实游戏端到端运行 | 全部路径由 fake 驱动；无实机验证 |
| **S8-02** | `ProxyLoadedInGame` 需调用方提供 | 进程模块观察的采集端未实现（与 S5-04 同源） |
| **S8-03** | `Verified` 依赖用户确认的强证据 | 设计使然；自动化只能到 `Applied` |
| **S8-04** | 两轮计划生成的第二次未做"选择是否变化"的比较 | 若两轮选出不同的 mode/proxy，当前不报警；实践中 recipe 固定时不会发生 |
| **S8-05** | `WorkflowResult` 未持久化 | 跨进程恢复与历史记录属 Stage 9 |

## Files Changed

```
src/DLSSGManager/Orchestration/SmoothMotionWorkflow.cs   (新增)
test/Harness/Program.cs                                  (+215)
test/Harness/Harness.csproj                              (+1)
docs/progress/STAGE_08.md                                (新增)
```

## Pending User Validation

- 真实游戏上的完整编排（检测 → 安装 → Profile → 验证）
- `Verified` 所需的强证据（Debug Bars 观察 / 补丁日志）

## Stage 8 Acceptance: PASS

Automated tests passed. **Real hardware / real game verified: NO** —— 编排未在真实游戏上运行过；`Verified` 级别在当前证据下无法自动达成，这是设计约束而非缺陷。
