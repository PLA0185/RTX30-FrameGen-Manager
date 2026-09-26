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
        WritesNvidiaProfile: false,
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

        return result;
    }

    /// <summary>Delegates to the shared signature primitive rather than repeating it.</summary>
    public PackageVerification VerifyPackage(string path)
    {
        var status = DeploymentService.ProbeSignature(path);

        return status == SignatureStatus.Intact
            ? new PackageVerification(true, status, "签名完整。")
            : new PackageVerification(false, status, $"签名校验未通过（{status}）。");
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

    /// <summary>Installs through the shared transactional deployment path — the only write path.</summary>
    public OpResult Install(GameEntry game, ModSource source, bool allowProtected = false) =>
        DeploymentService.Deploy(game, source, allowProtected);

    public OpResult Restore(GameEntry game, bool removeLogs) =>
        DeploymentService.Restore(game, removeLogs);
}
