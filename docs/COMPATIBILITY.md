# 兼容性数据库与验证诚实性

> 本文说明本项目如何记录「某配方在某环境可用」，以及为什么它拒绝把「文档这么说」写成「已验证」。

## 1. 十二维匹配

一条兼容性记录描述的是**一个确切环境**，不是「RTX 30 大概能用」：

`GPU` · `Driver` · `GraphicsApi` · `Game` · `Store` · `RendererExe` · `Provider` · `ProviderVersion` · `InstallMode` · `ProxyAsi` · `LaunchMode` · `ValidationState`

匹配规则刻意保守：

| 情况 | 结果 |
|---|---|
| 记录未涵盖某维度 | **不匹配**（缺失不等于相同）→ 无法成为精确匹配 |
| 查询不知道某维度 | **无法核对** → 无法成为精确匹配 |
| 存在**明确矛盾**（如 GPU 不同） | **`None`** —— 不是 `Partial`。把矛盾说成「部分吻合」，正是无关记录被采用的路径 |
| 部分吻合且其余无法核对 | `Partial` —— 仍**不产生 `Compatible` 结论** |

只有**全部十二维精确匹配**且验证状态为「可用」时，才会得到 `Compatible`。

## 2. 证据来源

| 来源 | 含义 | 是第一手？ |
|---|---|---|
| `OfficialProviderReadme` | 上游文档 | 否 |
| `GitHubRelease` | 发布说明/资产 | 否 |
| `GitHubIssue` / `GitHubDiscussion` | 问题与讨论 | 否 |
| `CommunityReproduction` | 他人复现 | 否 |
| `LocalUserTest` | **本机**由使用者测试 | **是** |
| `ProjectMaintainerTest` | **本机**由维护者测试 | **是** |

**为什么只有两种算第一手**：README 是关于**别人**环境的陈述，社区复现是关于**他们**环境的陈述。重复多少次都不会变成对本机的观察。

## 3. 验证等级与升降级

```
Unverified → Documented → CommunityReported → PendingUserValidation → ProjectVerified
```

`ProjectVerified` **只在**同时满足以下两点时才成立：

1. 证据是**第一手**（`LocalUserTest` / `ProjectMaintainerTest`）
2. 有**验证日期**

**缺日期同样不成立**：驱动、Provider 与游戏都会变，一条没有日期的验证无法被复查，因此不是可用的验证。

越级的声明不会入库，也不会被静默丢弃——它会被**降级为 `PendingUserValidation` 并附带原因**。降级目标选 `PendingUserValidation` 而不是 `Unverified`，是因为「打算验证」这个意图真实存在、值得保留可见，只是尚未发生。

## 4. 合并规则：证据可升级，不可降级

同一配方对同一游戏的多次尝试会合并：

- **第一手观察可替换文档引用**
- **文档引用不得覆盖已经在本机观察到的东西**

没有这条规则，一条配方第一次拿到的引用就会把它的验证等级永久钉死，之后无论学到什么都无法提升。

## 5. 配方记忆

记录的是**尝试**，不是布尔：成功 2 次、失败 1 次的配方，与从未试过的配方是两回事，而**失败计数正是阻止坏配方被反复推荐的部分**。

`IsKnownGood` 要求：

```
HighestEvidence == Verified  且  Successes > 0  且  Failures == 0
```

由于 `Verified` 需要交叉印证的证据（见 `docs/progress/STAGE_08.md`），**仅仅把文件复制成功永远不会让一条配方变成 known-good**。

排序：known-good 优先 → 成功次数 → 失败次数。

## 6. 当前状态（诚实声明）

**随发行版一起发布的数据库是空的。** 没有任何随附的真实记录，也没有任何条目达到 `ProjectVerified`。

**但运行时不一定为空**：`SmoothMotionWorkflow` 在构造时会调用 `CompatibilityMatrixStore.Load()` 读取用户已有的配置文件。所以「空」只描述**发行时**的状态，不描述用户机器上的状态 —— 用户从旧版本带来、或自己准备过的记录**会被用上**。

这**不是缺陷，而是当前正确状态**：模型、守卫与存储已经就位并有测试覆盖，但把它们填满需要在真实机器上跑通一次完整流程——那属于 `Pending User Validation`。

**⚠️ 只有「读」接线了，「写」没有。** 本工具**不会自动积累**兼容性记录：`Add(...)` 在 `src/` 里没有任何调用者。原因是有意留待决定 ——「要不要让本工具自己积累记录」会改变产品行为（用户会因此少看到确认框），而且需要先定义**什么算一次可靠的记录**（证据等级）。**在那条规则定下来之前，宁可让它保持只读。**

在真实记录出现之前，更新系统不会给出 `LatestCompatible`，编排也不会自动进入 `Ready`。这是有意为之：**没有证据时，系统宁可停下来问，也不猜。**
