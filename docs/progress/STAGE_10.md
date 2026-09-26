# Stage 10 — UI 重构（进行中 · 呈现层与导航骨架已交付）

> 基线 `6ef8ed8`。**判定 PARTIAL**：七页结构与导航已在界面中实现且编译通过；**详情页内的高级控件尚未折叠**，且**人工运行验证未执行**。

## 已完成（本轮）

### 1. 呈现层（可自动测试）

`src/DLSSGManager/UI/Presentation.cs`：`NavigationModel` / `StatusPresenter` / `AdvancedPanel` / `LibraryPresenter`，36 项测试覆盖。

**两条诚实规则**（本阶段核心）：`Installed` 显示为 **Warning** 而非 Good（文件复制成功只表示文件在磁盘上）；`Unknown` **永不**显示为正常（更新检查失败不显示为「已是最新」）。

### 2. XAML 七页导航（本轮新增）

`MainWindow.xaml`（781 → 约 1000 行）：

| 改动 | 说明 |
|---|---|
| Row 2 内新增导航栏（184px，两列→三列的外层） | 「主要」组 5 页 + 「高级」组 2 页 |
| 新增 `PageLibrary` 容器 | **包裹**现有「侧边栏 + 详情」联动视图，**未重写任何既有内容** |
| 新增六页 | `PageDashboard`（默认可见）/ `PageGameDetails` / `PageUpdates` / `PageDownloads` / `PageDiagnostics` / `PageSettings` |
| `MainWindow.xaml.cs` | `Nav_SelectionChanged`（两个列表互斥选中）+ `ShowPage` + `RefreshPageContent` |

**改造策略：包裹 + 追加，而非重写。** 全部既有 `x:Name` 原样保留（`GameList` / `DetailPanel` / `StatusCard` / `OutputBox` / `FetchProgressPanel` 等 30 余个），因此三个 code-behind 的引用**在编译期即被验证完整**——`dotnet build` 0 错误即为此提供了证据。

新增 20 个本地化键**同时**写入 `Strings.zh.cs` 与 `Strings.en.cs`；`LocalizationAudit` 会扫描 XAML 中的 `{loc:Tr}` 并对未定义键报错，本轮该审计通过。

### 3. 页面内容来源

`RefreshPageContent` 用**已有的核心状态**填充各页摘要（`GpuText` / `SubtitleText` / `_data.Games` / `Selected` / `AppVersion.Label`），未新增任何业务逻辑——符合「不得把 Core 逻辑塞回 UI」。

## 未完成项（PARTIAL 的原因）

1. **详情页内的高级控件（Proxy 下拉、Tier/Frame/Log 下拉、AddProxy 等）仍在页面上直接可见**，尚未折叠进「高级」折叠容器。导航层已分组，但页内分组未做。
2. **人工运行验证未执行**。XAML 改动**无法被 Harness 覆盖**（WPF 文件不在 `<Compile Include>` 白名单内），编译通过只证明引用完整与语法正确，**不证明运行时布局与交互正确**。
3. 视觉细化（间距、配色、图标）未做。

## 测试

| 项 | 结果 |
|---|---|
| Harness | **720 通过 / 0 失败 / 10 跳过**（与上一轮相同——XAML 改动不增加也不减少自动测试） |
| `dotnet build -c Release` | **0 警告 / 0 错误** |
| 本地化审计 | 通过（新增 20 键在两个表中齐备） |
| 人工运行 | **未执行** |

## 需要人工确认的具体点（交接给运行者）

1. 启动后默认停在「总览」，左侧导航显示 5 + 2 两页；
2. 点击任一导航项能切换页面，且**同一时刻只高亮一项**（两个列表互斥）；
3. 「游戏库」页与改动前的界面**外观一致**（侧边栏 + 详情联动未受影响）；
4. 选中游戏、扫描 Steam、部署/恢复、下载进度条、底部日志等**既有功能仍然工作**；
5. 窗口缩放时导航栏宽度（158–230px）与主内容区布局正常。

## Remaining Risks

| ID | 风险 | 说明 |
|---|---|---|
| **S10-01** | **人工运行验证未执行** | 编译通过不证明运行时正确；这是本阶段最大的未验证面 |
| **S10-02** | 页内高级项未折叠 | 导航层已分组，详情页内控件仍直接可见 |
| **S10-03** | 界面改动不可自动验证 | WPF 文件不在 Harness 白名单内，UI 变更只能人工验证 |
| **S10-04** | 视觉设计未做 | 只做了结构与文案 |

## Files Changed（累计）

```
src/DLSSGManager/UI/Presentation.cs        (新增)
src/DLSSGManager/MainWindow.xaml           (+约 220 行：导航栏 + 六个新页 + 包裹)
src/DLSSGManager/MainWindow.xaml.cs        (+约 70 行：导航逻辑)
src/DLSSGManager/Strings.zh.cs             (+20 键)
src/DLSSGManager/Strings.en.cs             (+20 键)
test/Harness/Program.cs                    (+180)
test/Harness/Harness.csproj                (+1)
docs/progress/STAGE_10.md                  (本文件)
```

## Pending User Validation

- **本阶段全部界面改动均未在真实运行中验证**（见上面「需要人工确认的具体点」）

## Stage 10 Acceptance: PARTIAL

七页结构与导航已实现、编译通过、本地化合规；**页内高级项折叠未完成**，且**界面未经过任何一次真实运行**。不宣布 PASS：一个从未被运行过的界面不算完成。
