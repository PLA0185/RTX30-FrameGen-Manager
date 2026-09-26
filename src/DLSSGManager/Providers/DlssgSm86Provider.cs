using System.IO;
using System.Net.Http;

namespace DLSSGManager.Providers;

/// <summary>
/// Binds the provider framework to the downloader that already exists.
///
/// Stage 2's security work lives inside <c>ModFetcher.Verify</c> and is reached through this call, so
/// a provider supplies no download logic of its own — it only needs a seam that lets the provider be
/// exercised without a network.
/// </summary>
public sealed class ModFetcherDownloader : IPatchDownloader
{
    private readonly string? _sourceId;

    /// <param name="sourceId">A specific source id, or null for the automatic order.</param>
    public ModFetcherDownloader(string? sourceId = null) => _sourceId = sourceId;

    public Task<OpResult> DownloadAsync(string destination, IProgress<string>? progress, CancellationToken ct) =>
        ModFetcher.DownloadIntoAsync(destination, progress, ct, _sourceId);

    public Task<string?> DetectLatestVersionAsync(CancellationToken ct) =>
        ModFetcher.DetectLatestVersionAsync(ct);
}

/// <summary>
/// The project's existing patch source: <c>sdli1995/dlssg_for_sm86</c>, the community Smooth Motion
/// build for RTX 30.
///
/// This class is an adapter, not a re-implementation. Every capability that Stage 2 hardened —
/// Authenticode integrity, the certificate pin, the atomic deployment, the backup-hash check, the
/// rollback — stays exactly where it was and is reached by delegation:
///
/// <code>
/// DlssgSm86Provider
///     └─ ModFetcher（下载 + 完整验证链）
///     └─ DeploymentService（事务部署 / 恢复 / 签名原语）
/// </code>
///
/// Copying any of that in here would create a second security path that could drift from the tested
/// one, which is precisely what the migration was told not to do.
/// </summary>
public sealed class DlssgSm86Provider : IPatchProvider
{
    /// <summary>Stable id. Persisted, so it must not change once shipped.</summary>
    public const string ProviderId = "dlssg-sm86";

    private readonly IPatchDownloader _downloader;

    /// <param name="downloader">Download seam; defaults to the real <see cref="ModFetcher"/>.</param>
    public DlssgSm86Provider(IPatchDownloader? downloader = null) =>
        _downloader = downloader ?? new ModFetcherDownloader();

    public string Id => ProviderId;

    /// <summary>
    /// Facts as researched in Phase 0 — no field is a guess.
    ///
    /// The licence is recorded as <see cref="LicenseClass.NoLicenseDeclared"/> because the repository
    /// carries no LICENSE file; its README claims GPLv3, and a README claim is not a licence grant,
    /// so calling it GPL would overstate what is actually established. The note keeps both halves of
    /// that situation visible instead of collapsing it into one flag.
    /// </summary>
    public ProviderMetadata Metadata { get; } = new(
        Id: ProviderId,
        DisplayName: "dlssg_for_sm86（Smooth Motion / Native）",
        UpstreamRepository: "sdli1995/dlssg_for_sm86",
        Distribution: DistributionModel.GitTree,
        License: LicenseClass.NoLicenseDeclared,
        LicenseNote: "仓库无 LICENSE 文件；README 自称 GPLv3。LICENSE 缺失是已查证事实，故不按 GPL 归类，"
                   + "也不打包其二进制 —— 由用户端从原作者来源获取。",
        // Two separate facts. This provider does not write driver settings itself, but the proxy it deploys is
        // inert until Smooth Motion is switched on for the game — so it does require that configuration.
        ProviderWritesNvidiaProfile: false,
        RequiresNvidiaProfileConfiguration: true,
        RequiresAdministrator: TriState.Conditional,
        TouchesGameProcess: true,
        Experimental: true);

    private ProviderHealth _health = ProviderHealth.Available("就绪（尚未探测）");

    /// <summary>
    /// Last known state. Reading it performs no I/O, so a status line can never block on a network.
    /// </summary>
    public ProviderHealth Health => _health;

    /// <summary>
    /// Asks the source for its newest version.
    ///
    /// The version rule belongs to this provider: it reads the banner the payload itself carries
    /// rather than trying to interpret a git ref as a version, which is what an upstream with
    /// non-semver tags would break on.
    /// </summary>
    public async Task<ReleaseInfo?> CheckLatestAsync(bool forceRefresh, CancellationToken ct)
    {
        try
        {
            var version = await _downloader.DetectLatestVersionAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(version))
            {
                // "Could not tell" is not "broken": the source may simply be unreachable right now.
                _health = ProviderHealth.Unavailable("无法确定最新版本（源码未提供版本信息，或网络不可用）。");
                return null;
            }

            _health = ProviderHealth.Available($"最新版本 {version}。");
            return new ReleaseInfo(Id, version, "raw.githubusercontent.com（git 树逐文件）");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _health = Classify(ex);
            return null;
        }
    }

    /// <summary>
    /// Version currently installed for a game, as recorded when it was deployed.
    ///
    /// Read from the deployment record rather than by re-parsing the payload: the record is what the
    /// manager itself wrote, so it cannot disagree with what the manager believes it installed.
    /// </summary>
    public string? GetInstalledVersion(GameEntry game)
    {
        var recorded = game?.Deployment?.ModVersion;
        return string.IsNullOrWhiteSpace(recorded) ? null : recorded;
    }

    /// <summary>Delegates to the shared downloader, which performs the full verification chain.</summary>
    public Task<OpResult> DownloadAsync(string destination, IProgress<string>? progress, CancellationToken ct) =>
        _downloader.DownloadAsync(destination, progress, ct);

    /// <summary>
    /// Applies this provider's verification rule to a single file — <b>the rule that fits the file</b>.
    ///
    /// <para>Delegates the signature question to the Stage 2 primitive rather than repeating it. Note the
    /// division of labour: this answers "are these bytes the signed bytes" for one file, while the subject and
    /// pinned-thumbprint checks stay in the shared download path, which is also where a tampered payload is
    /// refused outright.</para>
    ///
    /// <para><b>Configuration files take a different rule on purpose.</b> <c>dlssg_sm86.ini</c> is not a PE
    /// image and has never been signed, so asking for its signature would refuse every payload the provider
    /// ships. What can honestly be checked is that it exists and carries content; its integrity is covered by
    /// the payload manifest's hashes.</para>
    /// </summary>
    public PackageVerification VerifyPackage(string path)
    {
        if (PayloadFiles.Classify(path) == PayloadFileKind.Config)
        {
            if (!File.Exists(path))
                return new PackageVerification(false, SignatureStatus.Unknown, "配置文件不存在。");

            var length = new FileInfo(path).Length;

            return length > 0
                ? new PackageVerification(true, SignatureStatus.NotSigned,
                    $"配置文件按内容校验（{length} 字节）；文本文件不适用 Authenticode，其完整性由 payload 清单的哈希覆盖。")
                : new PackageVerification(false, SignatureStatus.Unknown, "配置文件为空。");
        }

        // **二进制：先确认文件在。**
        //
        // `ProbeSignature` 把「打不开 / 不存在」一并判成 `NotSigned`，而 `NotSigned` 是**接受**的
        // （红线：未签名可接受，但须说明不视为已验证）—— 两条合起来会让**不存在的文件「校验通过」**：
        // 用户看到一行假话「「version.dll」校验通过（NotSigned）」，而失败被推后到 `Deploy` 里
        // 变成 `FileNotFoundException`，那时错误信息已经离真正的原因很远了。
        //
        // `MfgSmoothProvider` 对同一输入是**拒绝**的（它先查存在性）—— **两个 provider 对同一事实
        // 语义相反**。这里补上存在性检查与它对齐。（上面 Config 分支本来就有这一条，只有二进制分支没有。）
        if (!File.Exists(path))
            return new PackageVerification(false, SignatureStatus.Unknown, $"文件不存在，无法校验（{path}）。");

        var status = DeploymentService.ProbeSignature(path);

        return status switch
        {
            SignatureStatus.Intact => new PackageVerification(true, status, "签名完整。"),

            // **未签名：接受，但必须说明「不视为已验证」。** 这是本项目的红线 ——
            // 上游 payload 里未签名的 DLL 是常态，一律拒绝会把可用的包全部挡掉；
            // 但接受 ≠ 已验证，所以措辞必须如实。
            //
            // 这里曾经是 `status == Intact ? 接受 : 拒绝`，于是未签名的 DLL 被拒，而 `MfgSmoothProvider`
            // 对同一份文件是**接受**的。**同一个概念在两个 provider 上语义相反**：用户换一个 provider 就会
            // 遇到「同样的文件，一个能装一个不能装」，而且没有任何提示说明为什么。
            SignatureStatus.NotSigned => new PackageVerification(true, status,
                "未签名：接受该文件，但不视为已验证 —— 其完整性只能由 payload 清单的哈希覆盖。"),

            // 其余（`BadDigest` / `Unknown`）：拒绝。**内容与签名不符必须全源拒绝**，这是红线，
            // 不受「未签名可接受」这一条的影响 —— 两者的区别正是「从未签过」与「签过之后被改过」。
            _ => new PackageVerification(false, status, $"签名校验未通过（{status}）。"),
        };
    }

    /// <summary>
    /// Entry selection is honoured: the shared deployment path reads the game's preferred entry, so a plan
    /// that names one gets exactly that entry.
    /// </summary>
    public bool SupportsProxyChoice => true;

    /// <summary>dlssg-sm86 提供的是 DLSS 帧生成（代理 DLL 路线）。</summary>
    public bool ProvidesDlssFrameGeneration => true;

    /// <summary>
    /// **不需要写 NVIDIA DRS Profile —— 这是它与 mfg-smooth 的根本区别。**
    ///
    /// <para>它只做一件事：把代理 DLL（如 <c>version.dll</c>）与 <c>dlssg_sm86.ini</c> 放进游戏目录。
    /// 驱动那边没有任何设置必须改。因此 <b>「DRS 写门关闭」不等于「dlssg-sm86 不能部署」</b> ——
    /// 非提权环境下它照常可用，只有 mfg-smooth 会 fail-closed。</para>
    /// </summary>
    public bool RequiresSmoothMotionDrs => false;

    /// <summary>它也不写这些设置 —— 依赖与能力在这里都是「无」。</summary>
    public bool SupportsSmoothMotionDrs => false;

    /// <summary>Installs through the shared transactional deployment path.</summary>
    public OpResult Install(GameEntry game, ModSource source, bool allowProtected = false) =>
        DeploymentService.Deploy(game, source, allowProtected);

    /// <summary>Restores through the shared restore path, including its backup-hash check.</summary>
    public OpResult Restore(GameEntry game, bool removeLogs) =>
        DeploymentService.Restore(game, removeLogs);

    /// <summary>
    /// Maps a thrown failure onto a state.
    ///
    /// A network fault is explicitly <see cref="ProviderHealthState.Unavailable"/>, never
    /// <see cref="ProviderHealthState.Broken"/>, so a transient outage does not tell the user their
    /// provider is defective. Rate limiting is matched on the text the HTTP layer produces; that is a
    /// heuristic, and a precise distinction would need the status code surfaced from the downloader —
    /// noted as a limitation rather than presented as exact.
    /// </summary>
    private static ProviderHealth Classify(Exception ex)
    {
        var text = ex.Message ?? "";

        if (text.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("rate-limit", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("429") || text.Contains("403"))
        {
            return ProviderHealth.RateLimited($"来源限流：{text}");
        }

        if (ex is HttpRequestException or TaskCanceledException or IOException or System.Net.Sockets.SocketException)
            return ProviderHealth.Unavailable($"网络不可用：{ex.Message}");

        return ProviderHealth.Broken($"探测失败：{ex.Message}");
    }
}
