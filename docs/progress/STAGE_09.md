# Stage 9 — 游戏兼容数据库

> 基线 `af7af4b` → 本阶段提交见文末。

## Implemented

`src/DLSSGManager/Compatibility/CompatibilityEvidence.cs`：

- `EvidenceSource`（8 值）：`Unknown` / `OfficialProviderReadme` / `GitHubRelease` / `GitHubIssue` / `GitHubDiscussion` / `CommunityReproduction` / `LocalUserTest` / `ProjectMaintainerTest`
- `EvidenceType`（6 值）：`Unknown` / `Documentation` / `ReleaseNotes` / `BugReport` / `Reproduction` / `TestResult`
- `ValidationLevel`（5 值）：`Unverified` / `Documented` / `CommunityReported` / `PendingUserValidation` / `ProjectVerified`
- `EvidenceRef(Source, Type, Reference, ObservedAt)` + `IsFirstHand`
- `ValidationGuard`（守卫）＋ `RecipeMemoryEntry` / `RecipeMemoryStore`

`CompatibilityRecord` 追加两个字段（均带默认值，Stage 5 的构造与测试不受影响）：
`EvidenceRef? Evidence` 与 `ValidationLevel EvidenceValidation`，并已纳入 `Persist` / `Load`。
`CompatibilityMatrixStore` 增加只读快照 `All`。

## 核心规则：无实机证据不得标 Project Verified

**这不是约定，是守卫。** 所有写入都经过 `ValidationGuard.Normalize`：

| 主张 | 证据 | 结果 |
|---|---|---|
| `ProjectVerified` | 上游 README（非第一手） | **降级为 `PendingUserValidation`** + 说明原因 |
| `ProjectVerified` | 社区复现（非第一手） | **降级** + 说明原因 |
| `ProjectVerified` | 本地用户测试但**无日期** | **降级** + 说明原因 |
| `ProjectVerified` | 本地用户测试 + 日期 | 保留 |
| `ProjectVerified` | 维护者测试 + 日期 | 保留 |
| `Documented` 等 | 任意 | 不被改动 |

**为什么"无日期"也算不成立**：驱动、Provider 与游戏都在变，一条没有日期的验证**无法被复查**，因此不是可用的验证。降级目标选 `PendingUserValidation` 而非 `Unverified`，是因为"打算验证"这个意图真实存在、值得保留可见，只是尚未发生。

`IsFirstHand` 只对 `LocalUserTest` 与 `ProjectMaintainerTest` 为真：README 是关于**别人**环境的陈述，社区复现是关于**他们**环境的陈述——重复多少次都不会变成对本机的观察。

## Recipe Memory（成功配方记忆）

`RecipeMemoryEntry` 记录**尝试**而非布尔：成功 2 次失败 1 次的配方，与从未试过的配方是两回事，而失败计数正是阻止坏配方被反复推荐的部分。

- `IsKnownGood` 要求 `HighestEvidence == Verified && Successes > 0 && Failures == 0`。由于 `Verified` 在本项目的证据阶梯上需要交叉印证，**仅仅复制成功文件的运行永远不会让配方变成 known-good**
- `RankFor(game)`：known-good 优先 → 成功次数 → 失败次数
- 持久化：schemaVersion=1、原子写、**损坏即丢弃不抛**（损坏的记忆毫无价值，但绝不能阻止程序启动）

**证据合并规则（可升级、不可降级）**：第一手观察可替换文档引用；文档引用**不得**覆盖已经在本机观察到的东西。这条规则是本阶段测试暴露出来的——第一版实现让"首次写入的证据"永久固定，导致后来真正的第一手验证无法提升验证等级。

## Tests

新增 38 项（Harness 646 → **684**，原测试未删）：

| 组 | 覆盖 |
|---|---|
| 枚举完整性 | 来源 ≥8 · 类型 ≥6 · 等级 ==5 |
| 第一手判定 | 文档非第一手 · 社区非第一手 · 本地测试是第一手 |
| **越级拒绝** | 文档+`ProjectVerified` 被拒 · 社区复现被拒 · **无日期被拒** · 有日期通过 |
| 降级行为 | 降级到 `PendingUserValidation` · 附带原因 · 有据声明原样保留 · 非 `ProjectVerified` 不被改动 |
| 配方记忆 | 尝试被记录 · **仅文件部署不足以 known-good** · 多次尝试合并 · 有效验证后 known-good · 最高证据保留 |
| 越级入库 | 仅凭文档的 `ProjectVerified` 入库时即被降级，原因写入条目 |
| 失败计数 | 失败被计数 · 有失败即非 known-good |
| 排序 | known-good 首位 · 只返回该游戏 |
| 持久化 | 计数往返 · **证据来源往返** · 验证等级往返 · 最高证据往返 · 损坏容错 |
| 矩阵扩展 | 证据来源/等级/引用往返 · **新字段未破坏 12 维匹配** |

## Baseline vs Current Build

```
Build:    0 warnings / 0 errors  →  0 warnings / 0 errors
Harness:  646 passed / 0 failed / 10 skipped  →  684 passed / 0 failed / 10 skipped
dotnet test: exit 0（无 VSTest 项目）
实机验证：未执行
```

## Known Issues

- **数据库当前为空**：没有任何真实记录；Stage 9 交付的是模型、守卫与存储，不是数据
- 没有任何条目达到 `ProjectVerified`——这是**正确状态**，不是缺陷
- 配方记忆尚未接入编排（`SmoothMotionWorkflow` 不自动写入记忆；接线属后续工作）
- Harness 10 项跳过测试仍需 Mod 文件

## Remaining Risks

| ID | 风险 | 说明 |
|---|---|---|
| **S9-01** | 无真实数据 | 模型与守卫完整，但没有任何实测记录入库；需真机运行后才有内容 |
| **S9-02** | 配方记忆未接入编排 | 记录了能力，但没有自动记录路径；目前只能由调用方显式写入 |
| **S9-03** | 证据到期未处理 | `LastVerified` 只是记录；驱动/Provider 更新后如何让旧验证失效尚未实现 |
| **S9-04** | 无导入/导出 | 社区贡献证据需要交换格式（属后续） |

## Files Changed

```
src/DLSSGManager/Compatibility/CompatibilityEvidence.cs   (新增)
src/DLSSGManager/Compatibility/CompatibilityMatrix.cs     (+证据字段、+All)
test/Harness/Program.cs                                   (+190)
test/Harness/Harness.csproj                               (+1)
docs/COMPATIBILITY.md                                     (新增)
docs/progress/STAGE_09.md                                 (新增)
```

## Pending User Validation

- 任何真实的 `ProjectVerified` 记录（需要在本机跑通一次完整流程）
- 配方记忆在真实使用中的累积与排序效果

## Stage 9 Acceptance: PASS

Automated tests passed. **Real hardware / real game verified: NO** —— 数据库为空，无任何条目达到 `ProjectVerified`，这是设计要求的诚实状态。
