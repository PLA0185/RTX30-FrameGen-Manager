# Stage 10 — UI 重构（进行中 · 呈现层已交付）

> 基线 `6ef8ed8`。**本阶段判定为 PARTIAL**，理由见文末「未完成项」。诚实记录比宣布 PASS 重要。

## 已完成：可测的呈现层

`src/DLSSGManager/UI/Presentation.cs`：

| 类型 | 作用 |
|---|---|
| `AppPage` + `PageDescriptor` + `NavigationModel` | 七页定义、顺序、主流程、高级页划分 |
| `StatusTone` + `StatusLine` + `StatusPresenter` | 把领域状态映射为「一句话 + 色调」 |
| `AdvancedGroup` + `AdvancedPanel` | 高级项分组，**全部默认折叠** |
| `GameRow` + `LibraryPresenter` | 游戏库行的状态与排序 |

## 七页结构（数据已定义）

| 页 | 用途 | 高级 |
|---|---|---|
| Dashboard 总览 | 这台机器现在是什么状态、下一步该做什么 | |
| Library 游戏库 | 已找到的游戏及各自状态 | |
| Game Details 游戏详情 | 选中游戏要做什么、结果如何 | |
| Updates 更新 | 本软件与补丁的更新 | |
| Downloads 下载 | 进行中的传输与历史 | |
| Diagnostics 诊断 | 日志、检测结果、兼容性记录 | ✔ |
| Settings 设置 | 语言、主题、代理、Provider、频道 | ✔ |

主流程固定为 **游戏库 → 游戏详情 → 更新**，有测试断言其顺序不会悄悄改变。

## 两条诚实规则（本阶段最重要的产出）

界面最容易失去诚实的地方是**把「不知道」和「没验证」显示成「没问题」**。这两条被写成纯函数并有专门测试：

1. **`Installed` 是 Warning，不是 Good**。文件复制成功只意味着文件在磁盘上；只有 `Verified`（需要交叉印证的信号）才显示为「已确认在生成帧」。文案明确写着「文件已安装，尚未验证」并附「文件复制成功并不表示功能生效」。
2. **`Unknown` 永不显示为正常**。Provider 状态未知 → 中性；更新检查失败 → 中性且文案为「无法确定更新状态」，而不是「已是最新」。

其余映射：`RateLimited` → Warning；`Broken` / `ReleaseFormatChanged` / `LicenseRestricted` → Bad；`Unavailable` → Warning（**不可用 ≠ 损坏**）；`Blocked` → Bad，`NeedsConfirmation` → Warning。

## 高级项：折叠而非隐藏

`AdvancedPanel.Groups` 覆盖要求的全部条目：Proxy · ASI · API · NVIDIA Profile · Flip Pacing · Low Latency · Provider · Release Channel · Logs。**全部 `ExpandedByDefault = false`**，有测试断言 `AllCollapsedByDefault`——默认值不能漂移。

Profile 设置经 `EditableProfileSettings` 携带**出处**（`Undocumented + Community Verified`），避免界面成为「这些是 NVIDIA 官方设置」这一误解的来源。

## 测试

新增 36 项（Harness 684 → **720**，原测试未删）：七页齐备与顺序 · 主流程不含高级页 · **高级项全部默认折叠** · 高级条目覆盖清单 · Profile 出处随界面携带 · Provider 六态映射 · **仅安装文件不显示为成功** · **只有 Verified 显示为成功** · **更新失败不显示为已是最新** · 编排结果映射 · 无需询问的判定与规划器一致 · 按钮文案随证据级别变化 · 游戏行未安装 ≠ 就绪 · 排序（有记录优先）。

## 未完成项（本阶段 PARTIAL 的原因）

**XAML 结构性重构未做，`MainWindow` 也未接入呈现层。**

具体剩余工作：

1. 把 `MainWindow.xaml`（**781 行**）改为七页导航结构（导航栏 + 内容容器），并把现有内容按页迁移；
2. 让 `MainWindow.xaml.cs` / `.Actions.cs` / `.Protection.cs` 改用 `StatusPresenter` / `LibraryPresenter` / `NavigationModel`，替换散落的状态文本；
3. 高级项在界面上以折叠容器呈现（当前只有数据模型）。

**为什么没有在本轮强行完成**：

- **XAML 不在 Harness 的编译白名单内**，任何改动都**无法被自动测试覆盖**；`MainWindow.xaml` 的 781 行里大量 `x:Name` 元素被三个 code-behind 文件引用，重写的失败模式是**运行时崩溃而非编译错误**——恰好是本项目红线最反对的那类改动（不可验证却看似完成）。
- 本轮 token 预算已用于 S6–S10 六个阶段的实现与验证；把 781 行 XAML 的重写压进剩余预算，得到的最可能是一个**编译通过但未被任何人运行过**的界面。
- 指令 §13 明确允许「完成一个明确工作单元 → 落盘 → 在后续轮次继续」。

**因此**：呈现层（**唯一能被自动验证的部分**）已交付并有 36 项测试；XAML 接入作为独立工作单元留待下一轮，届时按「先导航骨架 → 再逐页迁移 → 每批后运行验证」分批进行。

## Baseline vs Current Build

```
Build:    0 warnings / 0 errors  →  0 warnings / 0 errors
Harness:  684 passed / 0 failed / 10 skipped  →  720 passed / 0 failed / 10 skipped
dotnet test: exit 0（无 VSTest 项目）
界面运行验证：未执行（XAML 未改动，现有界面行为不变）
```

## Known Issues

- **七页结构目前只存在于数据模型，界面上仍是原有单页布局**
- 呈现层未被任何界面代码调用
- 界面改动无法被 Harness 覆盖（XAML 不在编译白名单内）

## Remaining Risks

| ID | 风险 | 说明 |
|---|---|---|
| **S10-01** | XAML 重构未完成 | 见上；这是本阶段唯一的实质缺口 |
| **S10-02** | 呈现层无界面消费者 | 逻辑正确但尚未接线；接入后需人工运行验证 |
| **S10-03** | 界面改动不可自动验证 | 本项目 Harness 不含 WPF 文件；UI 变更只能靠人工运行 |
| **S10-04** | 视觉设计未做 | 本阶段只做了结构与文案，未涉及视觉细化 |

## Files Changed

```
src/DLSSGManager/UI/Presentation.cs     (新增)
test/Harness/Program.cs                 (+180)
test/Harness/Harness.csproj             (+1)
docs/progress/STAGE_10.md               (新增)
```

## Pending User Validation

- 界面在真实运行下的表现（本阶段未改动 XAML，故沿用原有行为）

## Stage 10 Acceptance: PARTIAL

呈现层（含七页模型、状态映射、折叠规则）已完成并有 36 项自动测试；**XAML 导航重构与界面接线未完成**，原因与剩余清单见上。不宣布 PASS，因为界面上还看不到这七页。
