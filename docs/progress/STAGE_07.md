# Stage 7 — Smooth Provider

> 基线 `c51f0c0` → 本阶段提交见文末。

## 上游真实结构（2026-09-26 实测，非假设）

| 项 | 实测值 | 影响 |
|---|---|---|
| 仓库 | `pipotoufikxyz-lgtm/dlssg_for_sm86-MFG-version` | — |
| **License** | **MIT**（`license.spdx_id`） | **更正 Phase 0 的「无 LICENSE」记录** |
| Tag | 18 个，**非版本号**：`smxbox` / `smfix` / `smdriverupdate` / `SMMANUAL` / `sm75` / `SM` / `asi` … | 必须由 Provider 自带版本规则 |
| Asset | 每个 Release 0 或 1 个 | 无 asset ≠ 可安装 |
| 版本位置 | **在 asset 文件名里**：`SmoothMotion-2.8.2-R3-GP8-Xbox-Detection.zip` → `2.8.2` | 「从 tag 读版本」在此完全失效 |
| Digest | 有（`sha256:347f9d5b…`） | Release Asset 才有的内容级校验 |

这组事实同时验证了两件事：Stage 4 的 `ReleaseVersion` **不假设 SemVer** 是必需的（否则这 18 个 tag 全部无法处理），以及 Stage 3 的 `DistributionModel` 必须能表达 `ReleaseAsset`。

## Implemented

- `src/DLSSGManager/Providers/MfgSmoothProvider.cs`
  - `MfgSmoothProvider : IPatchProvider, IReleaseVersionResolver`
  - `ParseVersionFromAssetName`（模式匹配，**不硬编码文件名**）
  - `SelectPayloadAsset`（唯一 zip 且名字含版本；0 个或多个 → `null`）
  - `CheckLatestAsync` / `DownloadAsync` / `VerifyPackage` / `Install` / `Restore` / `GetInstalledVersion`
  - `Metadata`：`DistributionModel.ReleaseAsset` · `LicenseClass.Mit` · `Experimental`
- `src/DLSSGManager/Providers/ReleaseAssetFetcher.cs`
  - `IReleaseAssetFetcher` + `ReleaseAssetFetcher`（手动逐跳重定向 + 摘要校验 + 解包）
  - `SafeZip`（zip slip 防护）
- `ModFetcher.cs`：白名单补 `objects.githubusercontent.com`（**基于实际需要**：Release Asset 会重定向到该 host，而逐跳校验会拒绝未列出的主机）
- `ProviderRegistry.CreateDefault` 注册两个 Provider；`AppProviders.Mfg` 新入口

## 三条安全规则（各自对应一种具体失败方式）

1. **逐跳校验，而非只验首个 URL**：GitHub 对 release 下载会返回重定向；只校验拿到的 URL 等于让重定向决定字节来自哪里。因此手动跟随（上限 5 跳），每跳重新过 host 策略。
2. **解包之前先验摘要**：用共享的 `DigestParser` 比对 release 自带的 `digest`，因此「上游未公布摘要」（`Unavailable`）与「摘要匹配」（`Verified`）保持可区分。
3. **解包不能写到目标目录之外**：zip 条目自带相对路径，`..\..\x` 会被写到指向的位置。先解析为完整路径并逐个校验，**全部通过后才开始写**——被拒绝的压缩包不留残留。有测试实际构造逃逸条目并断言逃逸文件未产生。

## 不识别时停止自动安装

上游结构变化 → `ProviderHealthState.ReleaseFormatChanged` 且 `DownloadAsync` 拒绝执行。三种情形都归入此类：
- 没有 Release 带可识别的 payload zip
- 一个 Release 里有多个候选（**两个都合理时不是本地代码有权做的选择**）
- asset 名里没有版本号（命名规则变了）

**没有回退到"猜一个文件装上去"**：在这类仓库里装错 zip 等于把一个未经审视的二进制放到游戏旁边。

## 复用了什么（未复制任何第二套）

| 能力 | 复用对象 |
|---|---|
| Release 读取 | Stage 4 `IGitHubReleaseClient`（结构化 `UpdateNetworkState`） |
| 版本比较 | Stage 4 `ReleaseVersion`（不假设 SemVer） |
| 摘要解析与判定 | Stage 4 `DigestParser` / `DigestCheck` |
| 签名原语 | Stage 2 `DeploymentService.ProbeSignature` |
| 部署 / 恢复 / 事务 | Stage 2 `DeploymentService.Deploy` / `Restore` |
| Provider 契约与注册 | Stage 3 `IPatchProvider` / `ProviderRegistry` |

新 Provider **没有**自己的下载器逻辑、摘要校验、事务部署、Backup 或 Rollback。

另外，健康状态现在直接来自结构化的 `UpdateNetworkState`，**不再依赖异常文本启发式**——这是 Stage 3 遗留风险 S3-02 在新 Provider 上的消除（旧 Provider 未改动，以免触及已测行为）。

## Tests

新增 41 项（Harness 575 → **616**，原测试未删）：

| 组 | 覆盖 |
|---|---|
| 版本规则 | 真实 asset 名解析 · 两位数段 · 无版本号 → null · 空名 → null · **真实 tag 集不被当作版本** · 非版本 tag 不抛异常 |
| payload 选择 | 唯一 zip · 无 asset → null · **多个候选 → null（不猜）** · 非 zip → null |
| 格式变化 | 不返回版本 · 报 `ReleaseFormatChanged` · **下载被拒绝** |
| 正常路径 | 跨 tag 选出最高版本 · 健康可用 · 下载委托 · 规范发布路径 · 携带摘要 |
| Metadata | `ReleaseAsset` · `Mit` · 保留 Phase 0 更正记录 · 不写 Profile · 实验性 |
| 注册表 | 两个 Provider · ID 互异 · 应用级入口 |
| 共享路径 | 安装走事务部署 · 建立记录 · 恢复清除代理 · 签名原语复用 |
| ZIP 安全 | 路径逃逸识别 · **恶意压缩包被拒且未写出逃逸文件** · 正常压缩包可解压 |
| Host 策略 | 重定向主机已允许 · 未列主机被拒 · 非 HTTPS 被拒 |

## Baseline vs Current Build

```
Build:    0 warnings / 0 errors  →  0 warnings / 0 errors
Harness:  575 passed / 0 failed / 10 skipped  →  616 passed / 0 failed / 10 skipped
dotnet test: exit 0（无 VSTest 项目，载体是 test/Harness）
真实下载：未执行（见 Pending）
```

## Known Issues

- **未对真实 Release 执行过一次完整下载与解包**（需联网 + 约 100 MB 级资产）；解析与选择逻辑由真实结构驱动，但端到端路径未经实测
- `ExternalSmoothMotionProvider` 未实现（xikarioz 为专有 EULA，仅作外部集成；见下）
- Harness 10 项跳过测试仍需 Mod 文件

## Remaining Risks

| ID | 风险 | 说明 |
|---|---|---|
| **S7-01** | 真实下载未验证 | fake fetcher 验证了委托关系；真实 HTTP、重定向链与解包未经端到端实测 |
| **S7-02** | 上游命名规则可能再变 | 变更时按设计会落到 `ReleaseFormatChanged` 并停止安装（安全方向），但需要人工更新解析规则 |
| **S7-03** | 未实现 `ExternalSmoothMotionProvider` | 专有/EULA 项目按 §6.2 只能作外部集成：不复制源码、不重分发二进制、不改品牌重打包；当前不提供该 Provider |
| **S7-04** | zip 内文件未做进一步分类 | 解包后交给 `DeploymentService` 与 `ModSource` 处理；未按扩展名白名单过滤压缩包内容 |

## Files Changed

```
src/DLSSGManager/Providers/MfgSmoothProvider.cs      (新增)
src/DLSSGManager/Providers/ReleaseAssetFetcher.cs     (新增)
src/DLSSGManager/Providers/ProviderRegistry.cs        (+1 注册)
src/DLSSGManager/Providers/AppProviders.cs            (+Mfg 入口)
src/DLSSGManager/ModFetcher.cs                        (+objects.githubusercontent.com)
test/Harness/Program.cs                               (+245)
test/Harness/Harness.csproj                           (+2)
docs/progress/STAGE_07.md                             (新增)
```

## Pending User Validation

- 真实 Release Asset 的下载链路（HTTP + 重定向 + 解包）
- MFG payload 在真实游戏上的安装与生效

## Stage 7 Acceptance: PASS

Automated tests passed. **Real hardware / real game verified: NO** —— 未执行真实下载与实机安装。
