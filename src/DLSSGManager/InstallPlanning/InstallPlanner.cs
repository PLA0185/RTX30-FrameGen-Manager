using System.IO;
using DLSSGManager.Compatibility;
using DLSSGManager.GameDetection;
using DLSSGManager.Update;

namespace DLSSGManager.InstallPlanning;

/// <summary>
/// How a proxy is expected to be arranged in the game folder.
///
/// There is deliberately no "best" value. The three real strategies are <see cref="SafeSingle"/>
/// (one hot-path entry), <see cref="UtilitySet"/> (several non-hot-path entries) and
/// <see cref="KnownRecipe"/> (a documented combination) — and whether they are equivalent is still
/// <c>Unresolved / Needs Validation</c> per the proxy design, so the planner records which one a plan
/// uses without claiming one is superior.
/// </summary>
public enum ProxyStrategy
{
    Unknown,
    SafeSingle,
    UtilitySet,
    KnownRecipe,
}

/// <summary>Who owns a DLL already sitting in a hot-path entry name.</summary>
public enum ProxyOwnership
{
    Unknown,

    /// <summary>Written by this manager, per the deployment record.</summary>
    OwnedByThisTool,

    /// <summary>Recognised as belonging to the game itself.</summary>
    KnownGameFile,

    /// <summary>Recognised as another mod this project already knows about.</summary>
    KnownCompatibleMod,
}

/// <summary>
/// What kind of occupancy a loader entry name has.
///
/// <para>Four states rather than two, because "occupied" is not one situation. An entry we filled ourselves
/// can simply be written again; an entry the game or another mod filled cannot; and an entry nobody can
/// identify must be left alone. Without the first of those, re-running an installation deadlocks on its own
/// files — the tool refuses to touch what it wrote itself.</para>
/// </summary>
public enum ProxySlotClass
{
    /// <summary>Nothing is there. A new deployment may take it.</summary>
    FreeCandidate,

    /// <summary>
    /// Occupied by a file this manager wrote, and the hash still matches what was recorded — safe to reuse.
    /// A record alone is not enough: the user may have replaced the file since.
    /// </summary>
    ReusableOwnedCandidate,

    /// <summary>Occupied by something identifiable as not ours (the game, or a known mod).</summary>
    ForeignConflict,

    /// <summary>Occupied by something we cannot identify. Never treated as free.</summary>
    UnknownConflict,
}

/// <summary>One loader entry name and what occupies it.</summary>
public sealed record ProxySlot(
    string FileName,
    string Path,
    bool Exists,
    ProxyOwnership Ownership,
    string Reason,
    ProxySlotClass Class = ProxySlotClass.UnknownConflict)
{
    /// <summary>Only a free slot may be taken without further evidence.</summary>
    public bool IsFree => !Exists;

    /// <summary>True when this slot is the tool's own file and its hash still matches.</summary>
    public bool IsReusable => Class == ProxySlotClass.ReusableOwnedCandidate;
}

/// <summary>The full occupancy picture for one game folder.</summary>
public sealed record ProxyConflictReport(
    IReadOnlyList<ProxySlot> Slots,
    IReadOnlyList<string> SafeCandidates,
    IReadOnlyList<ProxySlot> Conflicts,
    IReadOnlyList<string>? ReusableCandidates = null)
{
    /// <summary>Entries we wrote ourselves and can safely write again when nothing is free.</summary>
    public IReadOnlyList<string> Reusable => ReusableCandidates ?? Array.Empty<string>();

    public bool HasConflict => Conflicts.Count > 0;

    public bool HasSafeSlot => SafeCandidates.Count > 0;

    /// <summary>True when a deployment can proceed at all: something free, or something of ours to reuse.</summary>
    public bool HasUsableSlot => SafeCandidates.Count > 0 || Reusable.Count > 0;
}

/// <summary>
/// Works out which loader entry names are free and which are occupied.
///
/// Occupancy is not a boolean: a file we wrote, a file the game shipped, and an unrecognised file are
/// three different situations with three different correct responses. Unknown occupancy is never
/// treated as free — overwriting something unidentified is the one mistake this scan exists to prevent.
/// </summary>
public static class ProxyConflictScanner
{
    /// <param name="game">Game whose folder is inspected.</param>
    /// <param name="isKnownCompatibleMod">
    /// Optional recogniser for other known mods. Left as a hook rather than a guess: without it, an
    /// unidentified DLL stays <see cref="ProxyOwnership.Unknown"/>.
    /// </param>
    public static ProxyConflictReport Scan(GameEntry game, Func<string, bool>? isKnownCompatibleMod = null)
    {
        var slots = new List<ProxySlot>();
        var safe = new List<string>();
        var conflicts = new List<ProxySlot>();

        if (game is null || string.IsNullOrWhiteSpace(game.RenderDir) || !Directory.Exists(game.RenderDir))
            return new ProxyConflictReport(slots, safe, conflicts);

        var ourFiles = OurRecordedFiles(game);
        var reusable = new List<string>();

        foreach (var name in ModSource.KnownProxyNames)
        {
            var path = Path.Combine(game.RenderDir, name);

            bool exists;
            try { exists = File.Exists(path); }
            catch { exists = false; }

            if (!exists)
            {
                slots.Add(new ProxySlot(name, path, false, ProxyOwnership.Unknown, "入口未被占用。",
                    ProxySlotClass.FreeCandidate));

                // **只有可部署名才能进 `safe`。**
                //
                // `KnownProxyNames` 是**扫描**集合（多一个 `winhttp.dll`：0.3.0 起不再部署，只在旧安装里
                // 可能残留）。作为扫描判据它是对的 —— 扫描要能看见并如实报告残留。但 `SafeCandidates` 会被
                // planner 拿去**选入口**，而部署侧只认 `ProxyCandidates`：
                //
                //   6 个可部署名全被占用 + `winhttp.dll` 不存在
                //   → safe = ["winhttp.dll"] → HasSafeSlot = true → 计划给出 **Ready**（本应 Blocked）
                //   → 用户拿到一个必然失败的入口：部署侧 PickFreeProxy 在 AvailableProxies 里永远找不到它，
                //     直接拒绝；更早一步，payload 核对会先报「缺少 winhttp.dll」。
                //
                // 这是「一个判据、两个集合」在本项目的第四处。`Slots` / `Conflicts` 继续用扫描集合
                // （那是对的），只有 `safe` —— 即「可以拿去用的入口」—— 必须与部署侧同集合。
                if (ModSource.ProxyCandidates.Contains(name, StringComparer.OrdinalIgnoreCase))
                    safe.Add(name);

                continue;
            }

            ProxyOwnership ownership;
            ProxySlotClass classification;
            string reason;

            if (ourFiles.TryGetValue(name, out var recordedSha))
            {
                // A record proves we wrote something here once; it does not prove the file is still ours.
                // Reuse is only safe while the bytes still match, so a changed file becomes unidentified
                // rather than silently reused.
                if (StillOurs(path, recordedSha))
                {
                    ownership = ProxyOwnership.OwnedByThisTool;
                    classification = ProxySlotClass.ReusableOwnedCandidate;
                    reason = "由本工具部署且哈希仍与部署记录一致，可以安全复用。";
                    reusable.Add(name);
                }
                else
                {
                    ownership = ProxyOwnership.Unknown;
                    classification = ProxySlotClass.UnknownConflict;
                    reason = "部署记录显示此处曾有本工具的文件，但当前内容的哈希与记录不符（可能被替换过），不得覆盖。";
                }
            }
            else if (isKnownCompatibleMod is not null && isKnownCompatibleMod(path))
            {
                ownership = ProxyOwnership.KnownCompatibleMod;
                classification = ProxySlotClass.ForeignConflict;
                reason = "识别为已知兼容 Mod，不由本计划接管。";
            }
            else
            {
                ownership = ProxyOwnership.Unknown;
                classification = ProxySlotClass.UnknownConflict;
                reason = "存在同名文件但无法确认归属，不得覆盖。";
            }

            var occupied = new ProxySlot(name, path, true, ownership, reason, classification);
            slots.Add(occupied);

            if (classification != ProxySlotClass.ReusableOwnedCandidate) conflicts.Add(occupied);
        }

        return new ProxyConflictReport(slots, safe, conflicts, reusable);
    }

    /// <summary>
    /// Files this manager wrote into the game folder, as name → hash recorded at write time.
    ///
    /// Reads the deployment's own file list first — that is the shape that carries a hash per file, which is
    /// what lets a proxy the user added by hand be recognised later. Falls back to the older single-name
    /// field and to the backup list so records written before <c>Files</c> existed still resolve.
    /// </summary>
    private static Dictionary<string, string> OurRecordedFiles(GameEntry game)
    {
        var recorded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var deployment = game.Deployment;
        if (deployment is null) return recorded;

        try
        {
            foreach (var file in deployment.Files ?? new List<DeployedFile>())
            {
                if (!string.IsNullOrWhiteSpace(file.FileName) && !recorded.ContainsKey(file.FileName))
                    recorded[file.FileName] = file.Sha256 ?? "";
            }

            if (!string.IsNullOrWhiteSpace(deployment.ProxyName) && !recorded.ContainsKey(deployment.ProxyName))
                recorded[deployment.ProxyName] = deployment.ProxySha256 ?? "";

            // Oldest records only listed what they displaced.
            foreach (var backup in deployment.Backups ?? new List<BackupItem>())
            {
                if (!string.IsNullOrWhiteSpace(backup.FileName) && !recorded.ContainsKey(backup.FileName))
                    recorded[backup.FileName] = "";
            }
        }
        catch
        {
            // A malformed record must not crash a scan; it just means nothing is recognised as ours.
        }

        return recorded;
    }

    /// <summary>
    /// Whether the file on disk is still byte-for-byte the one we recorded.
    ///
    /// An empty recorded hash means "we cannot prove this is ours", which is deliberately <b>false</b>:
    /// treating an unverifiable file as reusable is exactly how a tool overwrites something it did not write.
    /// </summary>
    private static bool StillOurs(string path, string recordedSha)
    {
        if (string.IsNullOrWhiteSpace(recordedSha)) return false;

        try
        {
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            return string.Equals(actual, recordedSha, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// A documented way to install one provider into one kind of game.
///
/// The field list is the compatibility contract: a recipe only applies when the store, API, renderer
/// pattern, provider and provider-version range all line up. Everything else — files, proxy strategy,
/// install mode, ASI strategy, profile changes, launch arguments, validation and rollback steps — is
/// carried so the planner never has to invent it.
/// </summary>
public sealed record InstallRecipe(
    string Id,
    string Game,
    StoreKind Store,
    GraphicsApi GraphicsApi,
    string RendererExePattern,
    string ProviderId,
    string ProviderVersionRange,
    IReadOnlyList<string> RequiredFiles,
    ProxyStrategy ProxyStrategy,
    InstallMode Mode,
    string AsiStrategy,
    IReadOnlyList<string> NvidiaProfileChanges,
    IReadOnlyList<string> LaunchArguments,
    IReadOnlyList<string> ValidationSteps,
    IReadOnlyList<string> RollbackSteps,
    string Note = "")
{
    /// <summary>
    /// Whether this recipe may be applied.
    ///
    /// An unset field on either side is not a wildcard: a recipe that does not pin the API cannot be
    /// applied to a game whose API is unknown, and a game whose API is unknown cannot be matched by a
    /// recipe that pins one. Guessing here would apply a documented procedure to an undocumented case.
    /// </summary>
    public bool AppliesTo(GraphicsApi api, StoreKind store, string? rendererExe, string providerId, string? providerVersion)
    {
        if (GraphicsApi != GraphicsApi.Unknown && GraphicsApi != api) return false;
        if (Store != StoreKind.Unknown && Store != store) return false;
        if (!string.Equals(ProviderId, providerId, StringComparison.OrdinalIgnoreCase)) return false;

        if (!string.IsNullOrWhiteSpace(ProviderVersionRange) && !string.IsNullOrWhiteSpace(providerVersion) &&
            !VersionInRange(providerVersion!, ProviderVersionRange))
            return false;

        if (!string.IsNullOrWhiteSpace(RendererExePattern) && !string.IsNullOrWhiteSpace(rendererExe))
        {
            var pattern = RendererExePattern.Replace("*", "");
            if (pattern.Length > 0 && !Path.GetFileName(rendererExe!).Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return api != GraphicsApi.Unknown && !string.IsNullOrWhiteSpace(providerVersion);
    }

    /// <summary>Accepts "0.3.0-0.3.9", "0.3.x" or an exact version; anything else is not a range.</summary>
    private static bool VersionInRange(string version, string range)
    {
        if (range.Contains('x', StringComparison.OrdinalIgnoreCase))
        {
            var prefix = range.Split('x')[0].TrimEnd('.');
            return version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        var parts = range.Split('-', 2);
        if (parts.Length == 2)
        {
            var low = ReleaseVersion.Compare(version, parts[0].Trim());
            var high = ReleaseVersion.Compare(version, parts[1].Trim());
            return low is VersionOrder.Greater or VersionOrder.Equal && high is VersionOrder.Less or VersionOrder.Equal;
        }

        return ReleaseVersion.Compare(version, range.Trim()) == VersionOrder.Equal;
    }
}

/// <summary>Where a plan ended up.</summary>
public enum PlanStatus
{
    Unknown,

    /// <summary>Every precondition is satisfied; the plan may be carried out.</summary>
    Ready,

    /// <summary>Not blocked, but something must be confirmed first.</summary>
    NeedsConfirmation,

    /// <summary>A precondition failed. The plan must not be carried out.</summary>
    Blocked,
}

/// <summary>
/// One driver setting the plan says must be written for the payload to do anything.
///
/// <para><b>Typed on purpose.</b> A bare name like "Smooth Motion Enable" cannot be checked against anything — the
/// plan could list a setting that does not exist and a reader could not tell. Carrying the
/// <see cref="NvidiaProfile.ProfileSetting"/> itself means the planner and the profile writer refer to the same
/// thing, and <see cref="ValueResolver"/> records <i>how</i> a value is decided rather than fixing one here.</para>
/// </summary>
public sealed record ProfileSettingRequirement(
    NvidiaProfile.ProfileSetting Setting,
    string ValueResolver,
    bool Required,
    GraphicsApi ApplicableApi,
    string Reason);

/// <summary>Everything the planner is allowed to look at. Assembled by the caller, never fetched inside.</summary>
public sealed record InstallPlanInput(
    GameEntry Game,
    RendererDetection Renderer,
    GraphicsApiDetection Api,
    string ProviderId,
    string? ProviderVersion,
    IReadOnlyList<string> ProviderPayloadFiles,
    ProxyConflictReport ProxyConflicts,
    CompatibilityDecision Compatibility,
    InstallRecipe? Recipe,
    bool HasKernelAntiCheat,
    bool AllowProtected,
    InstallMode RequestedMode = InstallMode.Unknown,

    /// <summary>
    /// The settings the caller intends to write, or null for none.
    ///
    /// <para>This is what lets the plan state its requirements from the same source the writer actually uses,
    /// instead of from a recipe's free-text notes — which is how a run could install a payload whose driver
    /// settings the plan never mentioned.</para>
    /// </summary>
    IReadOnlyList<NvidiaProfile.ProfileSetting>? ProfileSettings = null);

/// <summary>计划里的一个文件是从哪来的 —— 它决定了安装前该不该要求它在 payload 里存在。</summary>
public enum DeploymentFileSource
{
    /// <summary>来自 payload 解压目录。安装前可以（也应当）检查它存在。</summary>
    Payload,

    /// <summary>
    /// **由程序生成**（例如 <c>dlssg_sm86.ini</c>）。
    ///
    /// <para>它<b>不在 payload 里</b>，所以任何「payload 中缺少计划要求的文件」式的检查都必须跳过它 ——
    /// 否则一个由我们自己写出来的文件会被拿去要求它在下载包里存在。这正是本项目真实发生过的误判：
    /// 第三轮为修 P1-16 把 <c>dlssg_sm86.ini</c> 放进了 <c>FilesToDeploy</c>，而工作流在安装前的存在性
    /// 检查会因为它不在 payload 里而报「缺少」。</para>
    /// </summary>
    Generated,

    /// <summary>游戏目录里已经存在、可以复用的文件（例如其它 Mod 留下的代理入口）。</summary>
    /// <summary>
    /// 游戏目录里已经存在、可以复用的文件（例如其它 Mod 留下的代理入口）。
    ///
    /// <para><b>当前没有任何生产者</b>：`BuildPlannedFiles` 只产出 <see cref="Payload"/> 与
    /// <see cref="Generated"/>。这个值是**指令 §13 明确点名要求的三个之一**
    /// （「至少：Payload / Generated / ExistingReusable」），所以保留 —— 缺了它，枚举就无法表达
    /// 「复用已有文件」这种将来可能出现的情形。</para>
    ///
    /// <para><b>保留与「已接线」是两件事，这里如实标注为前者。</b>一个零生产者、零消费者的枚举值
    /// 有两种可能：为将来预留（保留 + 标注），或废弃残留（删除更安全）。这一个属于前者。</para>
    /// </summary>
    ExistingReusable,
}

/// <summary>
/// 计划要写入的一个文件，带来源与角色。
///
/// <para><b>为什么不能只存文件名。</b><c>dlssg_sm86.ini</c> 与 <c>version.dll</c> 在字符串层面没有区别，
/// 但前者由程序生成、后者必须来自 payload —— 安装前的存在性检查、以及安装后的一致性比对，对两者的要求
/// 完全不同。只存字符串会让这两种情况无法区分，于是「生成的文件」被迫去通过「payload 文件」的检查。</para>
/// </summary>
public sealed record PlannedFile(
    string TargetRelativePath,
    DeploymentFileSource SourceKind,

    /// <summary>payload 里的相对路径；<see cref="DeploymentFileSource.Generated"/> 时为 null。</summary>
    string? SourcePath = null,

    /// <summary>这个文件扮演什么角色（代理入口 / 配置 / 载荷），用于报告与校验分组。</summary>
    string? Role = null);

/// <summary>
/// The plan itself: what would be done, what would be changed, and what must be true beforehand.
///
/// Holds no file handles and performs no writes — it is a value the caller can inspect, show and refuse.
/// </summary>
public sealed record InstallPlan(
    PlanStatus Status,
    string? TargetRendererExe,
    GraphicsApi Api,
    string ProviderId,
    string? ProviderVersion,
    InstallMode Mode,
    ProxyStrategy ProxyStrategy,
    IReadOnlyList<string> FilesToDeploy,
    string? ProxyChoice,
    string? AsiChoice,
    IReadOnlyList<string> NvidiaProfileRequirements,

    /// <summary>
    /// The same requirements in the typed form the profile writer can act on.
    ///
    /// <para>Both are carried: the string list is what the interface and existing recipes speak, and this one is
    /// what can be checked against an actual setting. New callers should read this one.</para>
    /// </summary>
    IReadOnlyList<ProfileSettingRequirement> ProfileRequirements,
    IReadOnlyList<string> LaunchArguments,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Blockers,
    CompatibilityDecision Compatibility,
    IReadOnlyList<string> RollbackRequirements,

    /// <summary>
    /// Set only when the user was shown why the run stopped and chose to continue anyway.
    ///
    /// <para>This makes a <see cref="PlanStatus.NeedsConfirmation"/> plan executable <b>without changing what
    /// the plan says</b>: the status stays <c>NeedsConfirmation</c> and the compatibility state stays whatever
    /// it was, because consent is not evidence. Recording it as "Compatible" — or as verified — would turn
    /// "the user accepted the risk" into "this was checked".</para>
    /// </summary>
    bool UserApprovedUnverified = false)
{
    /// <summary>
    /// Whether execution may proceed: a cleared plan, or one the user explicitly approved despite it needing
    /// confirmation first. Everything else stays refused.
    /// </summary>
    public bool CanExecute =>
        Status == PlanStatus.Ready ||
        (Status == PlanStatus.NeedsConfirmation && UserApprovedUnverified);

    /// <summary>
    /// 计划要写入的每一个文件，带来源与角色 —— **最终校验以它为准**。
    ///
    /// <para><see cref="InstallPlan.FilesToDeploy"/> 是它的目标路径视图，保留下来是为了不破坏既有调用点；
    /// 但凡需要区分「这个文件从哪来」的地方（安装前的存在性检查、安装后的一致性比对），必须读这一个。</para>
    ///
    /// <para>默认空数组而不是 null：这个 record 的构造参数很多且都是必填，加一个必填参数会波及所有调用点，
    /// 而「没有文件」本身就是一个合法状态（例如 Blocked 的计划）。</para>
    /// </summary>
    public IReadOnlyList<PlannedFile> PlannedFiles { get; init; } = Array.Empty<PlannedFile>();
}

/// <summary>
/// Turns detection results into a plan.
///
/// Pure and side-effect free by construction: it receives everything it needs, reads only to check
/// whether a slot is free, and never writes. That is what makes the rules below testable without a game
/// folder, and what keeps "compute a plan" from ever turning into "change the user's installation".
/// </summary>
public static class InstallPlanner
{
    /// <summary>
    /// 计划要写入的文件清单，带来源。
    ///
    /// <para><b>这里是 §13 那个区分的落点。</b><c>dlssg_sm86.ini</c> 由管理器生成、<b>不在 payload 里</b>；
    /// payload 里的代理 DLL 则必须真的存在。两者若都用字符串表示，「安装前检查文件是否存在」这一步就会把
    /// 生成的文件也拿去 payload 里找，然后报「缺少」—— 那是本项目真实发生过的误判。</para>
    ///
    /// <para><b>代理入口的判据是「可部署名」（<c>ModSource.ProxyCandidates</c>），不是「扫描名」
    /// （<c>ModSource.IsKnownProxyName</c>，它多一个 <c>winhttp.dll</c>）。</b>这两个集合不可混用：
    /// <c>DeploymentService.PickFreeProxy</c> 从 <c>source.AvailableProxies</c>（基于 <c>ProxyCandidates</c>）
    /// 里找计划选定的名字，所以计划若按扫描名收进一个 <c>winhttp.dll</c>，部署侧会找不到它并<b>拒绝部署</b>
    /// ——「计划说会写、部署却拒绝」，而且不报错。**本项目在这个判据上已经犯过四次，方向始终一致。**</para>
    ///
    /// <para>放行的是「可能被写入」而不是「一定被写入」：部署侧还会写入 0.3.3+ payload 里的待机代理，
    /// 那些名字 planner 事先不知道。宁可放行得宽一点，也不能让真实的写入被自己的校验判成意外文件。</para>
    /// </summary>
    private static IReadOnlyList<PlannedFile> BuildPlannedFiles(
        IReadOnlyList<string> payloadFiles, string? proxyChoice, string? asiChoice)
    {
        static string Leaf(string path) => Path.GetFileName(path.Replace('/', '\\'));

        var result = new List<PlannedFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string target, DeploymentFileSource kind, string? source, string role)
        {
            if (!seen.Add(target)) return;

            result.Add(new PlannedFile(target, kind, source, role));
        }

        // payload 里命中**可部署**代理入口名的文件 —— 0.3.3+ 会把这些作为「待机代理」一并写入，planner 事先
        // 不知道具体是哪几个，所以按名字放行。判据是 `ProxyCandidates` 而不是 `IsKnownProxyName`：
        // 后者是扫描集合（多一个 winhttp.dll），而部署侧的 `AvailableProxies` 基于前者。
        foreach (var file in payloadFiles)
        {
            var leaf = Leaf(file);

            // 判据必须是**可部署名**（ProxyCandidates），不是**扫描名**（IsKnownProxyName 还含 winhttp.dll ——
            // 那是 0.3.0 起就不再部署、只在旧安装里可能残留的名字）。
            //
            // 两处判据不一致的后果是具体的：DeploymentService.PickFreeProxy 从 source.AvailableProxies 里找
            // 计划选定的名字，而 AvailableProxies 基于 ProxyCandidates。若计划用 KnownProxyNames 判据收进一个
            // winhttp.dll，Deploy 找不到它就会返回 null 并拒绝部署 —— **计划说会写、部署却拒绝**，
            // 正是「Plan 与实际不一致」的一种（§15/§16）。
            if (!ModSource.ProxyCandidates.Contains(leaf, StringComparer.OrdinalIgnoreCase)) continue;

            Add(leaf, DeploymentFileSource.Payload, file, "proxy");
        }

        // INI 是**生成**的：它由管理器写出来，payload 里没有这个文件。
        Add(ModSource.IniName, DeploymentFileSource.Generated, null, "config");

        // 选定值可能是 payload 里没有的（例如本地导入的入口），所以单独补。
        foreach (var chosen in new[] { proxyChoice, asiChoice })
        {
            if (chosen is null) continue;

            // 已经登记过的（含上面那条生成类）不重复登记。
            if (result.Any(f => string.Equals(f.TargetRelativePath, chosen, StringComparison.OrdinalIgnoreCase)))
                continue;

            Add(chosen, DeploymentFileSource.Payload, chosen, "proxy");
        }

        return result;
    }

    // 这里曾经有一个 `Summarize(IReadOnlyList<string> payloadFiles, string? proxyChoice, string? asiChoice)`：
    // 它把 payload 里命中 `ModSource.IsKnownProxyName`（**扫描**集合，含 `winhttp.dll`）的文件与 INI 拼成一个
    // 扁平路径列表。
    //
    // **它已被 `BuildPlannedFiles` 取代，且没有任何调用者** —— 但留在文件里就是一个陷阱：谁把它接回去，
    // §16 修掉的「计划用扫描名、部署用可部署名」缺陷会立刻复活，**而且没有任何测试会响**（新断言守的是
    // `BuildPlannedFiles`）。**删除比留一句「已废弃」的注释更安全：没人会误用一个不存在的方法。**

    public static InstallPlan Plan(InstallPlanInput input)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();
        var profile = new List<string>();
        var launch = new List<string>();
        var rollback = new List<string>();

        // 1. Kernel anti-cheat is a hard stop unless the user explicitly accepted it.
        if (input.HasKernelAntiCheat && !input.AllowProtected)
            blockers.Add("检测到内核级反作弊；未获显式授权前不得安装。");

        // 2. Graphics API. §9.3 is explicit that an unknown API must produce an error rather than a
        //    default choice, so this returns Blocked before anything else can be proposed.
        if (input.Api.Api == GraphicsApi.Unknown)
        {
            blockers.Add("UnknownApi：无法判定图形 API，安装计划不得默认选择其中一个。");
            return Blocked(input, blockers, warnings, profile, launch, rollback);
        }

        if (input.Api.HasConflict)
            warnings.Add($"图形 API 证据存在冲突，已按优先级取 {input.Api.Api}：{input.Api.Reason}");

        // 3. Renderer executable. Unknown is a stop for automatic installation, not a guess.
        if (input.Renderer.IsUnknown || string.IsNullOrWhiteSpace(input.Renderer.RendererExe))
        {
            warnings.Add($"渲染 EXE 尚未确定：{input.Renderer.Reason}");
        }
        else if (!input.Renderer.CanPlanWithoutAsking)
        {
            warnings.Add($"渲染 EXE 仅有静态证据（{input.Renderer.Level}），需要用户确认：{input.Renderer.Reason}");
        }

        // 4. Proxy occupancy. A free entry is taken first; failing that, an entry this tool wrote itself and
        //    whose hash still matches is reused — refusing that would make a re-install impossible without the
        //    user deleting our own files by hand. An unrecognised file is never overwritten, and "back it up
        //    first" is not an acceptable substitute.
        var proxyChoice = input.ProxyConflicts.SafeCandidates.FirstOrDefault();
        var reusedOwnEntry = false;

        if (proxyChoice is null)
        {
            proxyChoice = input.ProxyConflicts.Reusable.FirstOrDefault();
            reusedOwnEntry = proxyChoice is not null;
        }

        if (proxyChoice is null)
        {
            blockers.Add("所有热路径代理入口都被占用；不得覆盖来源不明的 DLL。");
        }
        else
        {
            if (reusedOwnEntry)
                warnings.Add($"没有空闲入口，将复用本工具先前部署的入口「{proxyChoice}」（哈希与部署记录一致）。");

            if (input.ProxyConflicts.HasConflict)
                warnings.Add($"以下入口已被占用，本计划不使用：{string.Join("、", input.ProxyConflicts.Conflicts.Select(c => c.FileName))}");
        }

        // 5. Compatibility. Only an exact, working record clears the plan; everything else means the
        //    user decides, which is what NeedsConfirmation expresses.
        var compatible = input.Compatibility.State == CompatibilityState.Compatible;
        if (!compatible)
            warnings.Add($"兼容性为 {input.Compatibility.State}：{input.Compatibility.Reason}");

        if (input.Compatibility.State == CompatibilityState.Incompatible)
            blockers.Add($"该组合已被记录为不兼容：{input.Compatibility.Reason}");

        // 6. Recipe-driven extras.
        var mode = input.Recipe?.Mode ?? input.RequestedMode;
        var strategy = input.Recipe?.ProxyStrategy ?? ProxyStrategy.Unknown;
        string? asi = null;

        // Typed requirements, built from the settings this run intends to write. The recipe's free-text notes stay
        // alongside rather than being replaced: those describe a documented procedure, these describe what is
        // actually about to happen — and only these can be checked against a real setting.
        var typedProfile = new List<ProfileSettingRequirement>();

        if (input.ProfileSettings is { Count: > 0 })
        {
            var api = input.Api.Api;

            foreach (var write in NvidiaProfile.SmoothMotionSettings.EnableWrites(api))
            {
                if (!input.ProfileSettings.Any(s => s.Id == write.Setting.Id)) continue;

                typedProfile.Add(new ProfileSettingRequirement(
                    Setting: write.Setting,
                    ValueResolver: "按设置定义与本次 API 推导（未知 API 时只写主开关）",
                    Required: write.Required,
                    ApplicableApi: api,
                    Reason: write.Reason));
            }
        }

        if (input.Recipe is not null)
        {
            profile.AddRange(input.Recipe.NvidiaProfileChanges);
            launch.AddRange(input.Recipe.LaunchArguments);
            rollback.AddRange(input.Recipe.RollbackSteps);

            if (input.Recipe.Mode == InstallMode.AsiLoader)
            {
                asi = input.Recipe.AsiStrategy;
                if (string.IsNullOrWhiteSpace(asi))
                    warnings.Add("该配方要求 ASI 加载方式，但未给出 ASI 策略。");
            }
        }
        else
        {
            // 措辞必须说清「配方」与「计划」的区别 —— 这一条在生产路径上**恒成立**。
            //
            // `ConfigurationRequest` 没有 `Recipe` 字段，所以 `WorkflowRequest.Recipe` 永远是 null，
            // 这个 else 分支每次都会走到。原措辞「计划仅包含代理部署，未包含 NVIDIA Profile 与启动参数」
            // 会让用户以为**整个计划不写 Profile** —— 而实际上写入哪些 Profile 设置由 `ProfileRequirements`
            // （来自 provider 与界面上的选择）单独决定，**与配方无关**。
            //
            // 恒成立的 warning 还有一个副作用：它会**淹没**真正需要注意的那些 warning。
            warnings.Add("未提供安装配方（配方驱动的 Profile 变更与启动参数因此留空）；"
                + "本次要写的 NVIDIA Profile 设置由 Provider 与界面上的选择单独决定。");

            rollback.Add("移除本次部署的代理文件与 INI，并恢复被覆盖的同名文件。");
        }

        // 7. A profile change is never implicit. The provider must say it writes one, and Stage 5 does not
        //    write profiles at all — so a requirement here is reported, not acted on.
        if (profile.Count > 0)
            warnings.Add($"该配方要求修改 NVIDIA Profile（{profile.Count} 项）；本阶段不执行 Profile 写入。");

        var status = Determine(input, blockers, compatibilityIsExact: compatible);

        return new InstallPlan(
            Status: status,
            TargetRendererExe: input.Renderer.RendererExe,
            Api: input.Api.Api,
            ProviderId: input.ProviderId,
            ProviderVersion: input.ProviderVersion,
            Mode: mode,
            ProxyStrategy: strategy,
            // 计划描述的是**部署**，不是 payload。解压出来的是整个发行包 —— 文档、符号、源码、验证报告、
            // 第三方许可 —— 而真正会落进游戏目录的只有代理入口和 INI。把解压目录整个列进去，等于让计划对
            // 用户说「要写 313 个文件」，而实际部署只写两个。（P1-16 用真实 MFG payload 实测到的就是这个数。）
            //
            // 保留三类：选定的代理 · INI · payload 里其它**已知代理入口名**的文件 —— 0.3.3+ 会把这些作为
            // 「待机代理」一并写入，planner 事先不知道具体是哪几个，所以必须按名字放行，否则单向校验会把
            // 那些文件判成「计划外的意外文件」，把一次成功的安装判成失败。
            FilesToDeploy: BuildPlannedFiles(input.ProviderPayloadFiles, proxyChoice, asi)
                .Select(f => f.TargetRelativePath)
                .ToList(),
            ProxyChoice: proxyChoice,
            AsiChoice: asi,
            NvidiaProfileRequirements: profile,
            ProfileRequirements: typedProfile,
            LaunchArguments: launch,
            Warnings: warnings,
            Blockers: blockers,
            Compatibility: input.Compatibility,
            RollbackRequirements: rollback)
        {
            // §13/§14/§15：每个文件的来源必须显式记录，而 FilesToDeploy 从**同一个方法**派生 ——
            // 两者必然一致，不存在「计划列了 A、校验看的是 B」的余地。
            PlannedFiles = BuildPlannedFiles(input.ProviderPayloadFiles, proxyChoice, asi),
        };
    }

    /// <summary>
    /// Ready requires all three: nothing blocking, a renderer good enough to act on, and an exact
    /// compatibility record. Anything less is a question for the user.
    /// </summary>
    private static PlanStatus Determine(InstallPlanInput input, List<string> blockers, bool compatibilityIsExact)
    {
        if (blockers.Count > 0) return PlanStatus.Blocked;
        if (!input.Renderer.CanPlanWithoutAsking) return PlanStatus.NeedsConfirmation;
        // A reusable entry of our own keeps the plan viable: blocking here would deadlock a re-install on the
        // files this tool itself wrote.
        if (!input.ProxyConflicts.HasUsableSlot) return PlanStatus.Blocked;
        if (!compatibilityIsExact) return PlanStatus.NeedsConfirmation;
        return PlanStatus.Ready;
    }

    private static InstallPlan Blocked(
        InstallPlanInput input, List<string> blockers, List<string> warnings,
        List<string> profile, List<string> launch, List<string> rollback) =>
        new(
            Status: PlanStatus.Blocked,
            TargetRendererExe: input.Renderer.RendererExe,
            Api: input.Api.Api,
            ProviderId: input.ProviderId,
            ProviderVersion: input.ProviderVersion,
            Mode: InstallMode.Unknown,
            ProxyStrategy: ProxyStrategy.Unknown,
            FilesToDeploy: Array.Empty<string>(),
            ProxyChoice: null,
            AsiChoice: null,
            NvidiaProfileRequirements: profile,

            // A blocked plan writes nothing, so it requires nothing typed either.
            ProfileRequirements: Array.Empty<ProfileSettingRequirement>(),
            LaunchArguments: launch,
            Warnings: warnings,
            Blockers: blockers,
            Compatibility: input.Compatibility,
            RollbackRequirements: rollback);
}
