namespace DLSSGManager.Providers;

/// <summary>
/// How a provider actually obtains its payload.
///
/// Phase 0 established that the upstream projects do not share one distribution model, so the
/// framework has to be able to say which one a provider uses rather than assuming GitHub release
/// assets are the only shape:
/// <c>sdli1995/dlssg_for_sm86</c> publishes binaries in the git tree and all seven of its releases
/// carry <c>assets=0</c>, while MFG / xikarioz ship release assets.
/// </summary>
public enum DistributionModel
{
    /// <summary>Files live in a git tree; there may be no usable release asset at all.</summary>
    GitTree,

    /// <summary>A GitHub release carries the payload as one or more assets.</summary>
    ReleaseAsset,
}

/// <summary>
/// License standing of the upstream project.
///
/// Deliberately not a bool: the researched projects sit in genuinely different states — MIT, no
/// LICENSE file at all, a proprietary EULA, and GPL-3.0 that may be referenced but never copied —
/// and a single "open source" flag would erase the distinctions the redistribution rules depend on.
/// </summary>
public enum LicenseClass
{
    /// <summary>Permissive licence that allows reuse with attribution.</summary>
    Mit,

    /// <summary>Copyleft: may be read for reference, but its code cannot be copied into this MIT project.</summary>
    Gpl3,

    /// <summary>Proprietary end-user licence; forbids redistribution and bundling.</summary>
    ProprietaryEula,

    /// <summary>No LICENSE file in the repository. A README claim is <b>not</b> a licence grant.</summary>
    NoLicenseDeclared,

    /// <summary>Usable as documentation or evidence only; no code or binaries may be reused.</summary>
    ReferenceOnly,

    /// <summary>Not yet established. Never guess a licence.</summary>
    Unknown,
}

/// <summary>Three-valued switch, so "not established yet" is distinguishable from "no".</summary>
public enum TriState
{
    No,
    Yes,
    Conditional,
}

/// <summary>
/// What a provider is, in terms the manager must act on rather than merely display.
///
/// The risk-bearing fields (writes the NVIDIA profile, needs administrator, touches the game
/// process, is experimental) are part of the contract because the UI has to warn on them and the
/// deployment rules have to respect them.
/// </summary>
public sealed record ProviderMetadata(
    string Id,
    string DisplayName,
    string UpstreamRepository,
    DistributionModel Distribution,
    LicenseClass License,
    string LicenseNote,

    /// <summary>
    /// Whether this provider writes NVIDIA Profile settings itself, as part of its own install.
    ///
    /// <para>This is about who does it, not about whether it is needed.</para>
    /// </summary>
    bool ProviderWritesNvidiaProfile,

    /// <summary>
    /// Whether the deployed payload <b>needs</b> driver profile settings in order to do anything.
    ///
    /// <para>Separate from <see cref="ProviderWritesNvidiaProfile"/> because the two come apart in both
    /// directions: a provider can ship a proxy that is inert until the driver is told to use it (needs
    /// configuration, does not write it), and one can write a setting for a payload the user installs elsewhere
    /// (writes it, does not require it here). Collapsing them made every payload look self-sufficient — which is
    /// how a run could copy files, report success, and leave the feature switched off.</para>
    /// </summary>
    bool RequiresNvidiaProfileConfiguration,

    TriState RequiresAdministrator,
    bool TouchesGameProcess,
    bool Experimental);

/// <summary>
/// A provider's operational state.
///
/// The list is deliberately finer than healthy/broken: a rate-limited source, an upstream that
/// changed its release format, and a licence that forbids use all need different wording and
/// different remedies, and none of them is the same thing as a provider that is simply broken.
/// </summary>
public enum ProviderHealthState
{
    Available,
    Unavailable,
    RateLimited,
    ReleaseFormatChanged,
    LicenseRestricted,
    Deprecated,
    Broken,
}

/// <summary>A state plus one short reason. Never a bare enum: "why" is what the user acts on.</summary>
public sealed record ProviderHealth(ProviderHealthState State, string Reason)
{
    /// <summary>True only for <see cref="ProviderHealthState.Available"/>.</summary>
    public bool IsUsable => State == ProviderHealthState.Available;

    public static ProviderHealth Available(string reason = "") => new(ProviderHealthState.Available, reason);

    public static ProviderHealth Unavailable(string reason) => new(ProviderHealthState.Unavailable, reason);

    public static ProviderHealth RateLimited(string reason) => new(ProviderHealthState.RateLimited, reason);

    public static ProviderHealth ReleaseFormatChanged(string reason) => new(ProviderHealthState.ReleaseFormatChanged, reason);

    public static ProviderHealth LicenseRestricted(string reason) => new(ProviderHealthState.LicenseRestricted, reason);

    public static ProviderHealth Deprecated(string reason) => new(ProviderHealthState.Deprecated, reason);

    public static ProviderHealth Broken(string reason) => new(ProviderHealthState.Broken, reason);
}

/// <summary>What a provider reports as its newest release, without assuming a version scheme.</summary>
/// <param name="ProviderId">Owning provider.</param>
/// <param name="Version">The provider's own version label, exactly as its parser produced it.</param>
/// <param name="SourceDescription">Where it came from, for the log and the UI.</param>
public sealed record ReleaseInfo(string ProviderId, string Version, string SourceDescription);

/// <summary>Outcome of checking one payload against the provider's rules.</summary>
/// <param name="Accepted">Whether the payload may be installed.</param>
/// <param name="Signature">The Authenticode verdict, so a caller can tell tampering from "unsigned".</param>
/// <param name="Message">Human-readable summary.</param>
public sealed record PackageVerification(bool Accepted, SignatureStatus Signature, string Message);

/// <summary>
/// The download capability a provider needs, behind an interface.
///
/// <see cref="ModFetcher"/> is a static class, so without this seam a provider could only be tested
/// by actually reaching the network — which would leave the "migration did not bypass the security
/// path" claims untestable.
/// </summary>
public interface IPatchDownloader
{
    /// <summary>Downloads the payload into <paramref name="destination"/>, replacing what is there.</summary>
    Task<OpResult> DownloadAsync(string destination, IProgress<string>? progress, CancellationToken ct);

    /// <summary>The newest version the source advertises, or null when it cannot be established.</summary>
    Task<string?> DetectLatestVersionAsync(CancellationToken ct);
}

/// <summary>
/// One managed patch source.
///
/// Shaped against the code that already exists rather than transcribed from a diagram: results reuse
/// <see cref="OpResult"/>, games are <see cref="GameEntry"/>, payloads are <see cref="ModSource"/>,
/// and install/restore delegate to <see cref="DeploymentService"/> so the security work done in
/// Stage 2 is shared, not re-implemented per provider.
/// </summary>
public interface IPatchProvider
{
    /// <summary>Stable identity. Unique across a registry; used for lookups and persistence.</summary>
    string Id { get; }

    ProviderMetadata Metadata { get; }

    /// <summary>Last known state. Cheap to read; never performs I/O.</summary>
    ProviderHealth Health { get; }

    /// <summary>Asks the source what its newest release is. Fails soft: null means "could not tell".</summary>
    Task<ReleaseInfo?> CheckLatestAsync(bool forceRefresh, CancellationToken ct);

    /// <summary>Version of what is currently installed for this game, or null when unknown.</summary>
    string? GetInstalledVersion(GameEntry game);

    /// <summary>Fetches the payload into a mod-source folder.</summary>
    Task<OpResult> DownloadAsync(string destination, IProgress<string>? progress, CancellationToken ct);

    /// <summary>Applies this provider's verification rules to one file.</summary>
    PackageVerification VerifyPackage(string path);

    /// <summary>Installs into the game folder.</summary>
    OpResult Install(GameEntry game, ModSource source, bool allowProtected = false);

    /// <summary>Removes what this provider installed.</summary>
    OpResult Restore(GameEntry game, bool removeLogs);

    /// <summary>
    /// Whether <see cref="Install"/> can honour a specific loader entry name.
    ///
    /// <para>The default is <b>false</b>, and that default is the honest one: a provider that cannot be told
    /// which entry to use must not be handed a plan that names one. Declaring this instead of silently
    /// ignoring the plan is what keeps the installed result equal to what the user approved.</para>
    /// </summary>
    bool SupportsProxyChoice => false;

    /// <summary>
    /// Whether <see cref="Install"/> can honour an ASI strategy.
    ///
    /// Also false by default: turning an ASI plan into a plain proxy install changes the shape of what gets
    /// installed, which is not a decision a provider may make on the user's behalf.
    /// </summary>
    bool SupportsAsiStrategy => false;

    /// <summary>
    /// What the payload in <paramref name="payloadDirectory"/> actually contains, or <b>null</b> when this
    /// provider cannot say.
    ///
    /// <para>Null is the honest default, and it is not the same as "empty": a provider that has not scanned
    /// the folder has no manifest, and a plan must not be built from a guess. A caller that falls back to its
    /// own expectations is then doing so knowingly rather than silently.</para>
    /// </summary>
    PayloadManifest? ManifestOf(string payloadDirectory) => null;

    /// <summary>
    /// 把 provider 自己的 payload 布局**正规化**成通用 <c>ModSource</c> 能消费的 canonical 目录，返回该目录；
    /// 若这个 provider 的 payload 本来就是 canonical 布局，返回 <b>null</b>（调用方沿用原目录）。
    ///
    /// <para><b>为什么不能靠通用类型去猜。</b>真实 MFG payload 是<b>嵌套发行包</b>
    /// （<c>Manual/Version/version.dll</c> · <c>payload/native/normal.asi</c> …），而 <c>ModSource</c> 期望
    /// 扁平的 canonical 布局（根目录有 <c>dlssg_sm86.ini</c>、代理 DLL 在根或 <c>altnative/</c>）。
    /// <b>实测</b>把前者直接交给后者会得到 <c>IsValid = False</c>，校验消息是「缺少 dlssg_sm86.ini，
    /// 且未找到任何代理 DLL」。让通用类型去猜嵌套目录，等于把「上游布局可能变」这件事变成一次静默的错误安装。</para>
    ///
    /// <para>默认 <b>null</b>：不声明就没有正规化，调用方沿用原目录 —— 对已经是 canonical 布局的 provider
    /// 来说这是正确行为，也让既有路径零改动。</para>
    /// </summary>
    string? PrepareCanonicalPayload(string payloadDirectory) => null;

    /// <summary>
    /// 这个 provider 是否**要求**写 NVIDIA DRS Profile 才算配置完成。
    ///
    /// <para><b>为什么必须与「能不能部署文件」分开。</b>DRS 写入需要管理员权限（实测：非提权返回
    /// <c>NVAPI_INVALID_USER_PRIVILEGE</c>，提权后成功）。如果所有 provider 共用一个「写门」，那么
    /// <b>「DRS 写门关闭」就会被误读成「这个 provider 不能部署」</b> —— 而 <c>dlssg-sm86</c> 只是把代理
    /// DLL 与 INI 放进游戏目录，**根本不需要碰 DRS**。把两者绑在一起，会让一个完全可用的能力在非提权
    /// 环境下被整体禁用。</para>
    ///
    /// <para>默认 <b>true</b> 是保守的那一侧：不声明就不许悄悄跳过 Profile 配置。要拿到「不需要 DRS」
    /// 这个结论，provider 必须显式说出来。</para>
    /// </summary>
    bool RequiresSmoothMotionDrs => true;

    /// <summary>
    /// 这个 provider 是否**会**在驱动可用时写 Smooth Motion 相关的 DRS 设置。
    ///
    /// <para>与 <see cref="RequiresSmoothMotionDrs"/> 的分工：一个是「不写就不算配置完成」，一个是
    /// 「有能力写、且写了才生效」。两者都默认为保守值，避免 provider 靠沉默获得额外权限。</para>
    /// </summary>
    bool SupportsSmoothMotionDrs => false;

    /// <summary>
    /// 这个 provider 是否提供 DLSS 帧生成（代理 DLL 路线）。
    ///
    /// <para>它描述的是<b>能力</b>而不是<b>依赖</b>：<c>dlssg-sm86</c> 提供帧生成但不需要 DRS；
    /// <c>mfg-smooth</c> 提供帧生成<b>且</b>需要 Smooth Motion DRS。把这两个事实拆成两个字段，
    /// 规划层才能对「哪些设置必须写」与「写不了时是否还能继续」分别作答 —— 而不是用一句
    /// 「这个 provider 需要 Profile」把两件事糊在一起。</para>
    /// </summary>
    bool ProvidesDlssFrameGeneration => false;
}

/// <summary>What a payload file is, which decides how it may be verified.</summary>
public enum PayloadFileKind
{
    /// <summary>A PE image. Authenticode is meaningful here.</summary>
    Binary,

    /// <summary>
    /// A text configuration file.
    ///
    /// <para>Authenticode does not apply: these are not PE images and are never signed. Demanding a signature
    /// would reject every payload that contains one — which is exactly what happened to
    /// <c>dlssg_sm86.ini</c>. Their integrity is covered by the payload manifest's hashes instead.</para>
    /// </summary>
    Config,
}

/// <summary>Classifies payload files by the rule that can legitimately be applied to them.</summary>
public static class PayloadFiles
{
    /// <summary>
    /// Classifies by extension rather than by content, because the question being answered is "may a signature
    /// check be required of this file at all" — and for a configuration file the answer is no, regardless of
    /// what its bytes happen to look like.
    /// </summary>
    public static PayloadFileKind Classify(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".dll" or ".exe" or ".sys" or ".node" => PayloadFileKind.Binary,
            _ => PayloadFileKind.Config,
        };
}
