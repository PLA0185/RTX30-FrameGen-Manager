# 更新系统

> 本软件与第三方补丁的更新是**两套独立系统**。它们共享基础设施，但**不共享**策略、安装逻辑或回滚逻辑。

## 1. 为什么必须分开

`SelfUpdateService` 更新的是**这个管理器自身**；`PatchUpdateService` 更新的是**第三方 Smooth Motion 补丁**。两者对「什么算新版本」「新版是否适用于本机」「失败了要回滚什么」的答案是**不同的**：

| | Self Update | Patch Update |
|---|---|---|
| 目标 | 本软件 | 第三方补丁 |
| 更新源 | 本仓库的正式 GitHub Release | 各 Provider 的上游 Release |
| 兼容性维度 | 运行环境（由发布流程固定） | GPU / 驱动 / 游戏 / API / 安装方式… |
| 安装 | 需要独立 Updater（后续阶段） | 走 Stage 2 的事务部署 |

把两者塞进一个 `if (self)` 会让一个策略同时服务两种需求，这正是它们被拆开的理由。**两者互不调用，故障互不影响**（有测试）。

**`Push Commit ≠ Client Update`**：客户端更新源只认**正式发布的 GitHub Release**，不认 main 分支上的提交——否则用户会拿到未经审核的代码。

## 2. 基础设施（共享）

| 组件 | 职责 |
|---|---|
| `IGitHubReleaseClient` | 读取 Release 列表；把 HTTP 结果**结构化**归一化 |
| `ReleaseCache` | 磁盘缓存（schemaVersion / 原子写 / 损坏即丢弃 / TTL 30 分钟） |
| `ReleaseVersion` | 版本比较，**不假设 SemVer** |
| `DigestParser` | 发布摘要解析与判定 |
| `RequestDeduplicator` | 相同请求合并为一次后端调用 |

### 版本比较不假设 SemVer
MFG 仓库的 18 个 tag **全是标签不是版本号**（`smxbox` / `smfix` / `SMMANUAL` / `sm75`…）。全局 `new Version(tag)` 在这里会全部失效。因此：**两侧都能解析为点分数字才比较**，否则返回 `Unordered`，而 `IsNewer` 对 `Unordered` 一律返回 false——**不猜顺序**。

### 不信任列表顺序
GitHub 返回的 Release 顺序**不是版本顺序**。候选从全部 Release 中按版本取最大；永不把首项当最新。

### 请求去重
同一时刻可能有三个地方问同一个 Provider（自动检查、手动刷新、健康探测），消耗三次配额。去重器把它们合并为一次调用。**共享任务不绑定任何调用者的 `CancellationToken`**——一个等待者取消不得拖垮其他等待者（有测试）。

## 3. 网络状态是结构化的

调用方**不再依赖异常文本**判断发生了什么：

| 情况 | 状态 |
|---|---|
| 429，或 403 且有 `X-RateLimit-Remaining: 0` / `X-RateLimit-Reset` | `RateLimited`（并解析重置时间） |
| 403 但**没有**限流头 | `Forbidden`（**不误判为限流**） |
| 5xx | `ServerError` |
| `HttpRequestException`（DNS / 连接失败） | `Offline` |
| 超时 | `Timeout` |
| JSON 解析失败 | `Malformed` |

**限流 ≠ 不可用 ≠ 损坏**：三者对应不同的提示与不同的补救方式。API 不可达时检查以状态返回，**不阻止程序启动**。

## 4. Latest Available ≠ Latest Compatible

这是两套更新系统共同的核心约束（任务书 §69）。

- `LatestAvailable`：上游有什么
- `LatestCompatible`：**这台机器能安全运行什么**（无证据时为 `null`）
- `RecommendedVersion`：策略允许实际采用的目标

**兼容性未知时 `LatestCompatible = null` 且不给推荐目标**，但 `UpdateAvailable` 仍为 true——如实报告有新版本，同时不假装它适合这台机器。

兼容性证据来自 12 维矩阵；`ProjectVerified` 只在**第一手证据 + 验证日期**齐备时成立（见 `docs/COMPATIBILITY.md`）。

## 5. Version Pin 与 Hold

用三态而非布尔：

| 状态 | 行为 |
|---|---|
| `NotPinned` | 自动目标可随上游移动 |
| `Pinned` | **仍报告**上游最新版，但自动目标不得越过固定版本 |
| `Held` | **仍允许检查、仍报告**新版本，但不给出任何自动目标 |

## 6. 频道

`Stable` 排除预发布；`Prerelease` 接受两者（回滚时需要）。**草稿在任何频道都被排除**——未发布的 Release 不是 Release。

缓存键**包含频道**：Stable 与 Prerelease 的答案不会互相串用。

## 7. Provider 的上游结构

各 Provider 自带版本规则。以 MFG 为例（2026-09-26 实测）：

- tag **不是**版本号，版本写在 asset 文件名里（`SmoothMotion-2.8.2-R3-GP8-Xbox-Detection.zip` → `2.8.2`）
- 每个 Release 有 0 或 1 个资产
- 资产**有** `digest`（git 树分发的上游没有）

**结构不可识别时**（无可识别 payload / 多个候选中无法唯一确定 / 文件名里没有版本），Provider 报告 `ReleaseFormatChanged` 并**停止自动安装**——不回退到「猜一个文件装上去」。在这类仓库里装错 zip 等于把一个未经审视的二进制放到游戏旁边。

## 8. 当前状态（诚实声明）

- **未对真实 GitHub API 运行 smoke test**；结构由 fake 客户端验证
- **未执行真实 Release Asset 的端到端下载与解包**
- **未实现独立 Updater**：Self Update 目前只做到「发现 + 解析 + 决策」，替换正在运行的可执行文件属于后续阶段
- 兼容性数据库为空，因此在真实记录出现前 `LatestCompatible` 恒为 `null`——这是**有意**的诚实结果
