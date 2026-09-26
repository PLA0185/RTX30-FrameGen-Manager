using System.IO;
using System.Text.RegularExpressions;
using DLSSGManager.Update;

namespace DLSSGManager.Providers;

/// <summary>
/// The community "MFG" build of Smooth Motion for RTX 30.
///
/// <para><b>Distribution shape, as observed on the upstream repository on 2026-09-26</b> (not assumed):
/// its eighteen tags are labels, not versions — <c>smxbox</c>, <c>smfix</c>, <c>SMMANUAL</c>, <c>sm75</c>
/// and so on — and each release carries either nothing or a single zip whose <i>name</i> holds the
/// version, e.g. <c>SmoothMotion-2.8.2-R3-GP8-Xbox-Detection.zip</c>. The release assets do publish a
/// <c>digest</c>, which the git-tree provider cannot.</para>
///
/// <para>Two consequences shape this class. First, the version rule belongs here rather than in shared
/// code: reading a tag as a version would yield nothing for any of those eighteen releases. Second, when
/// the shape is not recognised the provider reports
/// <see cref="ProviderHealthState.ReleaseFormatChanged"/> and stops — it does not fall back to guessing
/// which file to install, because installing the wrong zip from this repository would put an unreviewed
/// binary beside a game.</para>
///
/// Nothing about downloading, verification or deployment is reimplemented: the asset fetch goes through
/// <see cref="IReleaseAssetFetcher"/>, and install/restore delegate to
/// <see cref="DeploymentService"/>, so Stage 2's transaction and rollback remain the only write path.
/// </summary>
public sealed class MfgSmoothProvider : IPatchProvider, IReleaseVersionResolver
{
    /// <summary>Stable id. Persisted, so it must not change once shipped.</summary>
    public const string ProviderId = "mfg-smooth";

    public const string Repository = "pipotoufikxyz-lgtm/dlssg_for_sm86-MFG-version";

    private readonly IGitHubReleaseClient _client;
    private readonly IReleaseAssetFetcher _fetcher;

    /// <summary>Payload manifests by the folder they were scanned from. See <see cref="ManifestOf"/>.</summary>
    private readonly Dictionary<string, PayloadManifest> _manifests = new(StringComparer.OrdinalIgnoreCase);

    private ProviderHealth _health = ProviderHealth.Available("就绪（尚未探测）");
    private ReleaseEntry? _resolved;

    public MfgSmoothProvider(IGitHubReleaseClient? client = null, IReleaseAssetFetcher? fetcher = null)
    {
        _client = client ?? new GitHubReleaseClient();
        _fetcher = fetcher ?? new ReleaseAssetFetcher();
    }

    public string Id => ProviderId;

    /// <summary>
    /// License is recorded as MIT on the strength of a direct query to the GitHub API on 2026-09-26
    /// (<c>license.spdx_id = "MIT"</c>), which corrects the Phase 0 note that this repository declared no
    /// license. The earlier note is kept visible in the text rather than silently overwritten.
    /// </summary>
    public ProviderMetadata Metadata { get; } = new(
        Id: ProviderId,
        DisplayName: "MFG Smooth Motion（社区修改版）",
        UpstreamRepository: Repository,
        Distribution: DistributionModel.ReleaseAsset,
        License: LicenseClass.Mit,
        LicenseNote: "GitHub API 实测 license.spdx_id = \"MIT\"（2026-09-26）。Phase 0 曾记为「无 LICENSE」，"
                   + "以 API 实测为准并在此保留该更正记录。",
        // Same two facts as the built-in provider: the payload is a proxy that needs the driver told to use it,
        // and this code is not what tells it.
        ProviderWritesNvidiaProfile: false,
        RequiresNvidiaProfileConfiguration: true,
        RequiresAdministrator: TriState.Conditional,
        TouchesGameProcess: true,
        Experimental: true);

    public ProviderHealth Health => _health;

    /// <summary>
    /// The version carried by an asset file name.
    ///
    /// Matches the first dotted number in the name, which is where upstream puts it. Returns null when
    /// there is no number at all — that is the signal that the naming scheme changed, and the caller
    /// treats it as unrecognised rather than inventing a version.
    /// </summary>
    public static string? ParseVersionFromAssetName(string? assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName)) return null;

        var match = Regex.Match(assetName, @"(\d+\.\d+(?:\.\d+)*)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Picks the payload, without hard-coding any file name.
    ///
    /// A candidate must be a zip whose name yields a version. If more than one qualifies, or none does,
    /// the answer is null: two plausible payloads is not a choice this code is entitled to make, and a
    /// release with no payload is not an installable release.
    /// </summary>
    public static ReleaseAssetInfo? SelectPayloadAsset(ReleaseEntry release)
    {
        var candidates = release.Assets
            .Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .Where(a => ParseVersionFromAssetName(a.Name) is not null)
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>The provider's own version rule, used by the Stage 4 update service.</summary>
    public string? ResolveVersion(ReleaseEntry release) =>
        ParseVersionFromAssetName(SelectPayloadAsset(release)?.Name);

    public async Task<ReleaseInfo?> CheckLatestAsync(bool forceRefresh, CancellationToken ct)
    {
        try
        {
            var fetched = await _client.FetchReleasesAsync(Repository, ct).ConfigureAwait(false);

            if (!fetched.Ok)
            {
                // Structured state from the release client, so a rate limit is never mistaken for an
                // outage or for a broken provider.
                _health = fetched.State switch
                {
                    UpdateNetworkState.RateLimited => ProviderHealth.RateLimited(fetched.Reason),
                    UpdateNetworkState.Offline or UpdateNetworkState.Timeout or UpdateNetworkState.ServerError =>
                        ProviderHealth.Unavailable(fetched.Reason),
                    _ => ProviderHealth.Broken(fetched.Reason),
                };
                return null;
            }

            var stable = fetched.Releases.Where(r => r.IsCandidateFor(ReleaseChannel.Stable)).ToList();
            if (stable.Count == 0)
            {
                _health = ProviderHealth.Unavailable("上游没有任何正式 Release。");
                return null;
            }

            ReleaseEntry? best = null;
            string? bestVersion = null;

            foreach (var release in stable)
            {
                var version = ResolveVersion(release);
                if (version is null) continue;

                if (bestVersion is null || ReleaseVersion.Compare(version, bestVersion) == VersionOrder.Greater)
                {
                    best = release;
                    bestVersion = version;
                }
            }

            if (best is null || bestVersion is null)
            {
                // The repository exists and has releases, but none of them looks like the shape this
                // provider knows how to install. That is a format change, not an outage.
                _health = ProviderHealth.ReleaseFormatChanged(
                    $"上游 Release 结构中没有任何一个带可识别的 payload zip（共 {stable.Count} 个正式 Release；"
                    + "该仓库的 tag 不是版本号，版本需从 asset 文件名解析）。已停止自动安装。");
                return null;
            }

            _resolved = best;
            _health = ProviderHealth.Available($"最新版本 {bestVersion}（{best.Tag}）。");
            return new ReleaseInfo(Id, bestVersion, $"github.com/{Repository}（Release Asset）");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _health = ProviderHealth.Broken($"探测失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Downloads the resolved payload and unpacks it into <paramref name="destination"/>.
    ///
    /// The asset is addressed through GitHub's canonical download path, and the fetcher re-checks every
    /// redirect hop plus the published digest before a single byte is unpacked.
    /// </summary>
    public async Task<OpResult> DownloadAsync(string destination, IProgress<string>? progress, CancellationToken ct)
    {
        var result = new OpResult();

        var release = _resolved;
        if (release is null)
        {
            await CheckLatestAsync(forceRefresh: false, ct).ConfigureAwait(false);
            release = _resolved;
        }

        if (release is null)
        {
            result.Fail(_health.Reason);
            return result;
        }

        var asset = SelectPayloadAsset(release);
        if (asset is null)
        {
            result.Fail("ReleaseFormatChanged：无法唯一确定 payload 资产，已停止。");
            return result;
        }

        var url = $"https://github.com/{Repository}/releases/download/{release.Tag}/{asset.Name}";
        var fetched = await _fetcher.FetchAndExtractAsync(url, destination, asset.DigestSha256, progress, ct)
            .ConfigureAwait(false);

        if (!fetched.Ok)
        {
            result.Fail(fetched.Message);
            return result;
        }

        result.Note(fetched.Message);

        if (fetched.Digest?.State == DigestState.Unavailable)
            result.Note("上游未提供该资产的发布摘要，按未验证处理（不视为已通过）。");

        // Scan what actually landed on disk. A plan's file list should come from here rather than from a
        // caller's expectations, so an archive that unpacked into something else is caught now.
        var manifest = PayloadScanner.Scan(destination);
        if (manifest.Files.Count == 0)
        {
            result.Fail("解包后 payload 目录为空，无法确定要安装什么。");
            return result;
        }

        try { _manifests[Path.GetFullPath(destination)] = manifest; } catch { /* Keying is best effort. */ }

        result.Note($"payload 清单：{manifest.Files.Count} 个文件，共 {manifest.TotalSize} 字节。");

        return result;
    }

    /// <summary>
    /// The manifest of the payload most recently unpacked into a folder.
    ///
    /// <para>Keyed by the folder that was scanned: a manifest belongs to a payload, not to the provider, and
    /// handing one folder's contents to a plan built for another is how a file list turns into fiction.</para>
    ///
    /// <para>Returns null when nothing has been scanned — which is not the same as an empty payload, and must
    /// not be read as one.</para>
    /// </summary>
    public PayloadManifest? ManifestOf(string payloadDirectory)
    {
        if (string.IsNullOrWhiteSpace(payloadDirectory)) return null;

        try
        {
            return _manifests.TryGetValue(Path.GetFullPath(payloadDirectory), out var manifest) ? manifest : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Applies this provider's verification rules to one file.
    ///
    /// <para><b>Three outcomes, not two.</b> An <c>Intact</c> signature is accepted. An <c>Absent</c> one is
    /// also accepted, because an unsigned payload is normal for this ecosystem and refusing it would reject
    /// the very builds this project exists to deploy — but the message says so, and the caller must not
    /// report it as verified. A <c>BadDigest</c> one is refused outright: a signature that exists and does not
    /// match means the file was modified after signing, which is a different thing from never being signed.</para>
    /// </summary>
    public PackageVerification VerifyPackage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new PackageVerification(false, SignatureStatus.Unknown, "文件不存在，无法校验。");

        var status = DeploymentService.ProbeSignature(path);

        return status switch
        {
            SignatureStatus.Intact => new PackageVerification(true, status, "签名完整。"),
            SignatureStatus.NotSigned => new PackageVerification(true, status,
                "文件未签名。未签名是这类补丁的常见状态，因此允许安装，但不视为已验证。"),
            SignatureStatus.BadDigest => new PackageVerification(false, status,
                "签名存在但与文件内容不符（文件在签名后被修改过），已拒绝。"),
            _ => new PackageVerification(false, status, $"无法确认签名状态（{status}），已拒绝。"),
        };
    }

    public string? GetInstalledVersion(GameEntry game)
    {
        var recorded = game?.Deployment?.ModVersion;
        return string.IsNullOrWhiteSpace(recorded) ? null : recorded;
    }

    /// <summary>
    /// Entry selection is honoured: the shared deployment path reads the game's preferred entry, so a plan
    /// that names one gets exactly that entry.
    /// </summary>
    public bool SupportsProxyChoice => true;

    /// <summary>MFG Smooth Motion 提供的是 DLSS 帧生成（代理 DLL 路线）。</summary>
    public bool ProvidesDlssFrameGeneration => true;

    /// <summary>
    /// **不写 Smooth Motion 的 DRS 设置，这个 provider 的配置就不算完成。**
    ///
    /// <para>这是它与 <c>dlssg-sm86</c> 的根本区别：后者只把文件放进游戏目录，前者还必须让驱动为这个
    /// 游戏启用 Smooth Motion。因此 DRS 写门关闭时，mfg-smooth 必须 fail-closed —— 而 dlssg-sm86
    /// 不受影响。</para>
    /// </summary>
    public bool RequiresSmoothMotionDrs => true;

    /// <summary>它确实会写这些设置（与「要求写」是两件事：这个是能力，上面那个是依赖）。</summary>
    public bool SupportsSmoothMotionDrs => true;

    /// <summary>Installs through the shared transactional deployment path — the only write path.</summary>
    public OpResult Install(GameEntry game, ModSource source, bool allowProtected = false) =>
        DeploymentService.Deploy(game, source, allowProtected);

    public OpResult Restore(GameEntry game, bool removeLogs) =>
        DeploymentService.Restore(game, removeLogs);

    /// <summary>
    /// 把真实 MFG payload（**嵌套发行包**）正规化成通用 <c>ModSource</c> 期望的 canonical 布局。
    ///
    /// <para><b>实测依据</b>：把真实 payload 直接交给 <c>ModSource</c> 会得到 <c>IsValid = False</c>，
    /// 校验消息是「缺少 dlssg_sm86.ini，且未找到任何代理 DLL」—— 因为解压出来的是
    /// <c>SmoothMotion-&lt;ver&gt;-&lt;rev&gt;/Manual/Version/version.dll</c> 这样的嵌套结构，而
    /// <c>ModSource</c> 只认根目录与 <c>altnative/</c>。**让通用类型去猜嵌套目录，等于把「上游布局可能变」
    /// 变成一次静默的错误安装**，所以正规化必须由知道这个布局的 provider 自己做。</para>
    ///
    /// <para><b>判定规则有依据，不猜目录名</b>：候选代理由叶子名命中 <see cref="ModSource.IsKnownProxyName"/>
    /// 决定（上游把目录叫 <c>Manual/Version</c> 还是 <c>payload/native</c> 会变，但「哪些名字是代理入口」
    /// 是本项目与上游共同的稳定概念）。<c>version.dll</c> 放根目录（上游的默认入口），其余放
    /// <c>altnative/</c>；另外写一个最小 INI 模板 —— 它<b>会被真实部署覆盖</b>，存在只是为了满足
    /// <c>ModSource</c> 对 canonical 布局的要求。</para>
    /// </summary>
    public string? PrepareCanonicalPayload(string payloadDirectory)
    {
        if (string.IsNullOrWhiteSpace(payloadDirectory) || !Directory.Exists(payloadDirectory)) return null;

        // 已经是 canonical 布局（根目录就有 INI）就不动它 —— 正规化是给嵌套 payload 用的。
        if (File.Exists(Path.Combine(payloadDirectory, ModSource.IniName))) return null;

        try
        {
            var canonical = Path.Combine(payloadDirectory, ".canonical");
            var marker = Path.DirectorySeparatorChar + ".canonical" + Path.DirectorySeparatorChar;

            if (Directory.Exists(canonical)) Directory.Delete(canonical, recursive: true);

            Directory.CreateDirectory(canonical);
            Directory.CreateDirectory(Path.Combine(canonical, "altnative"));

            // 按叶子名收集候选代理；同一叶子名只取第一个遇到的。
            var found = new List<(string Leaf, string Path)>();

            foreach (var file in Directory.EnumerateFiles(payloadDirectory, "*", SearchOption.AllDirectories))
            {
                if (file.Contains(marker, StringComparison.OrdinalIgnoreCase)) continue;

                var leaf = Path.GetFileName(file);

                // 判据与 planner 保持一致：用**可部署名**（ProxyCandidates），不用扫描名（IsKnownProxyName
                // 还含 winhttp.dll）。两处不一致的后果是具体的：canonical 里会出现一个计划永远不会选、
                // Deploy 也找不到的入口；而若 payload 里只有 winhttp.dll 这类扫描名，canonical 里就会
                // **一个可部署入口都没有**，计划选定的名字在源目录里不存在，部署直接失败。
                // 一个判据、两个地方，必须同一个集合。
                if (!ModSource.ProxyCandidates.Contains(leaf, StringComparer.OrdinalIgnoreCase)) continue;

                if (found.Any(f => f.Leaf.Equals(leaf, StringComparison.OrdinalIgnoreCase))) continue;

                found.Add((leaf, file));
            }

            if (found.Count == 0) return null;

            // 首选入口进根目录，其余作为备用入口进 altnative/。
            var primary = found.FirstOrDefault(f =>
                f.Leaf.Equals("version.dll", StringComparison.OrdinalIgnoreCase));

            if (primary.Path is null) primary = found[0];

            File.Copy(primary.Path, Path.Combine(canonical, primary.Leaf), overwrite: true);

            foreach (var (leaf, path) in found)
            {
                // 明确的字符串比较。`ReferenceEquals` 对 string 不是「同一个条目」的正确表达 ——
                // 它现在恰好成立，只因为值元组复制的是引用；任何一次重构（例如把 found 换成记录类型、
                // 或在中间插入一次字符串处理）都会让它悄悄失效，后果是主入口被重复写进 altnative/。
                if (string.Equals(leaf, primary.Leaf, StringComparison.OrdinalIgnoreCase)) continue;

                File.Copy(path, Path.Combine(canonical, "altnative", leaf), overwrite: true);
            }

            // 最小 INI 模板：只为满足 canonical 布局，真实部署会覆盖它。
            File.WriteAllText(Path.Combine(canonical, ModSource.IniName),
                "[DLSSG SM86]" + Environment.NewLine);

            return canonical;
        }
        catch
        {
            // 正规化失败就如实返回 null，让调用方沿用原目录 —— 那时 ModSource 会明确报它不兼容，
            // 而不是让一次半成品的复制变成一次错误的安装。
            return null;
        }
    }
}
