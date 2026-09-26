using DLSSGManager.Compatibility;
using DLSSGManager.NvidiaProfile;
using DLSSGManager.Orchestration;
using DLSSGManager.Providers;
using DLSSGManager.Update;

namespace DLSSGManager.UI;

/// <summary>The seven surfaces the interface is organised into.</summary>
public enum AppPage
{
    Dashboard,
    Library,
    GameDetails,
    Updates,
    Downloads,
    Settings,
    Diagnostics,
}

/// <summary>One page plus how it should be presented.</summary>
/// <param name="Advanced">
/// True for pages whose content is for people who already know what the settings mean. The navigation
/// lists them, but nothing on them is surfaced on the path a first-time user walks.
/// </param>
public sealed record PageDescriptor(AppPage Page, string Title, string Purpose, bool Advanced = false);

/// <summary>
/// The page set, in the order it is shown.
///
/// Ordered by the user's own journey rather than by the code's structure: what the machine is, what
/// games are there, what the selected game needs, then updates and transfers, and only then settings and
/// diagnostics.
/// </summary>
public static class NavigationModel
{
    public static IReadOnlyList<PageDescriptor> Pages { get; } = new[]
    {
        new PageDescriptor(AppPage.Dashboard, "总览", "这台机器现在是什么状态、下一步该做什么。"),
        new PageDescriptor(AppPage.Library, "游戏库", "已找到的游戏，以及每个游戏当前的状态。"),
        new PageDescriptor(AppPage.GameDetails, "游戏详情", "选中游戏要做什么，以及结果如何。"),
        new PageDescriptor(AppPage.Updates, "更新", "本软件与补丁的更新。"),
        new PageDescriptor(AppPage.Downloads, "下载", "正在进行的传输与已完成的历史。"),
        new PageDescriptor(AppPage.Diagnostics, "诊断", "日志、检测结果与兼容性记录。", Advanced: true),
        new PageDescriptor(AppPage.Settings, "设置", "语言、主题、代理、Provider 与频道。", Advanced: true),
    };

    /// <summary>Where a run starts. The dashboard answers "what now" before anything is selected.</summary>
    public static AppPage DefaultPage => AppPage.Dashboard;

    public static IReadOnlyList<PageDescriptor> PrimaryPages => Pages.Where(p => !p.Advanced).ToList();

    public static IReadOnlyList<PageDescriptor> AdvancedPages => Pages.Where(p => p.Advanced).ToList();

    public static PageDescriptor Describe(AppPage page) => Pages.First(p => p.Page == page);

    /// <summary>
    /// The order a first-time user is walked through: pick a game, let it configure, launch, read the
    /// result. Expressed as data so a test can assert the flow did not quietly change.
    /// </summary>
    public static IReadOnlyList<AppPage> PrimaryFlow { get; } = new[]
    {
        AppPage.Library, AppPage.GameDetails, AppPage.Updates,
    };
}

/// <summary>How a status should read at a glance.</summary>
public enum StatusTone
{
    /// <summary>Nothing to say yet, or nothing known.</summary>
    Neutral,

    /// <summary>Working as intended.</summary>
    Good,

    /// <summary>Needs attention but is not wrong.</summary>
    Warning,

    /// <summary>Failed, or would be wrong to proceed with.</summary>
    Bad,
}

/// <summary>One line of status: what it says, and how it should look.</summary>
public sealed record StatusLine(string Text, StatusTone Tone, string Detail = "")
{
    public bool IsActionable => Tone is StatusTone.Warning or StatusTone.Bad;
}

/// <summary>
/// Turns domain states into something a person can read at a glance.
///
/// <para>This is a pure function of the domain state, which is the point: the mapping is where honesty is
/// either kept or lost, and here it can be tested. Two mappings in particular exist to prevent a
/// reassuring display that is not true — an unknown state is never shown as good, and an installation
/// that has merely copied files is never shown as working.</para>
/// </summary>
public static class StatusPresenter
{
    public static StatusLine ForProvider(ProviderHealth health) => health.State switch
    {
        ProviderHealthState.Available => new StatusLine("可用", StatusTone.Good, health.Reason),
        ProviderHealthState.RateLimited => new StatusLine("来源限流", StatusTone.Warning, health.Reason),
        ProviderHealthState.ReleaseFormatChanged => new StatusLine("上游结构已变，已停止自动安装", StatusTone.Bad, health.Reason),
        ProviderHealthState.LicenseRestricted => new StatusLine("许可证不允许使用", StatusTone.Bad, health.Reason),
        ProviderHealthState.Deprecated => new StatusLine("已弃用", StatusTone.Warning, health.Reason),
        ProviderHealthState.Unavailable => new StatusLine("来源暂不可用", StatusTone.Warning, health.Reason),
        ProviderHealthState.Broken => new StatusLine("来源不可用", StatusTone.Bad, health.Reason),

        // Unknown is deliberately neutral. Showing it as good would be a claim nobody made.
        _ => new StatusLine("状态未知", StatusTone.Neutral, health.Reason),
    };

    /// <summary>
    /// The evidence ladder as a display.
    ///
    /// <para><see cref="SmoothMotionEvidence.Installed"/> is a <b>warning</b>, not a success: files are on
    /// disk and nothing more has been shown. Only <see cref="SmoothMotionEvidence.Verified"/> — which
    /// requires corroborating signals — is shown as good.</para>
    /// </summary>
    public static StatusLine ForEvidence(SmoothMotionEvidence evidence) => evidence switch
    {
        SmoothMotionEvidence.Verified => new StatusLine("已确认在生成帧", StatusTone.Good, "多信号交叉确认。"),
        SmoothMotionEvidence.Applied => new StatusLine("驱动已接受设置", StatusTone.Warning, "尚无生成帧的外部证据。"),
        SmoothMotionEvidence.Requested => new StatusLine("已请求启用，未确认生效", StatusTone.Warning),
        SmoothMotionEvidence.Loaded => new StatusLine("游戏已加载代理，未验证生成帧", StatusTone.Warning),
        SmoothMotionEvidence.Installed => new StatusLine("文件已安装，尚未验证", StatusTone.Warning,
            "文件复制成功并不表示功能生效。"),
        _ => new StatusLine("未安装", StatusTone.Neutral),
    };

    public static StatusLine ForOutcome(WorkflowOutcome outcome) => outcome switch
    {
        WorkflowOutcome.Succeeded => new StatusLine("已完成", StatusTone.Good),
        WorkflowOutcome.NeedsConfirmation => new StatusLine("需要你确认后继续", StatusTone.Warning),
        WorkflowOutcome.Blocked => new StatusLine("已阻止：前置条件不满足", StatusTone.Bad),
        _ => new StatusLine("失败", StatusTone.Bad),
    };

    public static StatusLine ForUpdate(UpdateCheckResult result) => result.State switch
    {
        UpdateState.UpToDate => new StatusLine("已是最新", StatusTone.Good, result.Reason),
        UpdateState.UpdateAvailable => new StatusLine($"可更新到 {result.RecommendedVersion ?? result.LatestAvailable}",
            StatusTone.Good, result.Reason),
        UpdateState.Pinned => new StatusLine("已固定版本", StatusTone.Warning, result.Reason),
        UpdateState.Held => new StatusLine("已暂停自动更新", StatusTone.Warning, result.Reason),
        UpdateState.RateLimited => new StatusLine("更新检查受限流影响", StatusTone.Warning, result.Reason),
        UpdateState.ProviderUnavailable => new StatusLine("更新来源暂不可用", StatusTone.Warning, result.Reason),

        // "Unknown" must never render as reassurance: a failed check is not an up-to-date result.
        _ => new StatusLine("无法确定更新状态", StatusTone.Neutral, result.Reason),
    };

    /// <summary>
    /// Whether a game can be configured without further input.
    ///
    /// Mirrors the planner's own rule rather than restating it: anything that is not an exact, working
    /// compatibility record means the user decides.
    /// </summary>
    public static bool CanConfigureWithoutAsking(CompatibilityMatchKind kind, ValidationState validation) =>
        kind == CompatibilityMatchKind.Exact && validation == ValidationState.ReportedWorking;

    /// <summary>What the primary button should say for a game, given what is known about it.</summary>
    public static string ConfigureButtonText(SmoothMotionEvidence evidence) => evidence switch
    {
        SmoothMotionEvidence.None => "自动配置",
        SmoothMotionEvidence.Installed => "重新配置",
        SmoothMotionEvidence.Verified => "已配置（可重新配置）",
        _ => "继续配置",
    };
}

/// <summary>A settings group that stays out of the way until asked for.</summary>
public sealed record AdvancedGroup(string Title, string Purpose, IReadOnlyList<string> Items, bool ExpandedByDefault = false);

/// <summary>
/// The advanced settings, pre-collapsed.
///
/// None of these are expanded by default, and that is the design rather than a default that happens to
/// be off: proxy entry, ASI, API, flip pacing and low latency are all things a user has to have a reason
/// to want. Everything here stays reachable — it is folded, not hidden.
/// </summary>
public static class AdvancedPanel
{
    public static IReadOnlyList<AdvancedGroup> Groups { get; } = new[]
    {
        new AdvancedGroup("代理与注入", "选择代理入口与 ASI 方式。",
            new[] { "Proxy", "ASI" }),
        new AdvancedGroup("图形 API", "覆盖自动检测到的图形 API。",
            new[] { "API" }),
        new AdvancedGroup("NVIDIA Profile", "查看将要写入驱动的设置及其当前值。",
            new[] { "NVIDIA Profile", "Flip Pacing", "Low Latency" }),
        new AdvancedGroup("来源与版本", "选择 Provider 与发布频道。",
            new[] { "Provider", "Release Channel" }),
        new AdvancedGroup("日志", "查看详细日志与导出诊断信息。",
            new[] { "Logs" }),
    };

    /// <summary>True when nothing is expanded; asserted by a test so the default cannot drift.</summary>
    public static bool AllCollapsedByDefault => Groups.All(g => !g.ExpandedByDefault);

    public static IReadOnlyList<string> AllItems => Groups.SelectMany(g => g.Items).ToList();

    /// <summary>
    /// The DRS setting ids that may be changed, with their provenance attached.
    ///
    /// Surfaced here because the interface is where a user would otherwise be left believing these are
    /// documented NVIDIA settings.
    /// </summary>
    public static IReadOnlyList<ProfileSetting> EditableProfileSettings => SmoothMotionSettings.All;
}

/// <summary>One row in the game library.</summary>
public sealed record GameRow(string Name, string StatusText, StatusTone Tone, string Location, bool CanConfigure);

/// <summary>Builds library rows from domain state.</summary>
public static class LibraryPresenter
{
    /// <summary>
    /// A row per game, with its status taken from the deployment record rather than from optimism.
    ///
    /// A game with no record is "未安装" — not "已就绪", because nothing has been done for it yet.
    /// </summary>
    public static GameRow For(GameEntry game)
    {
        var installed = game.Deployment is not null;
        var status = installed
            ? StatusPresenter.ForEvidence(SmoothMotionEvidence.Installed)
            : new StatusLine("未安装", StatusTone.Neutral);

        return new GameRow(
            Name: string.IsNullOrWhiteSpace(game.Name) ? "(未命名)" : game.Name,
            StatusText: status.Text,
            Tone: status.Tone,
            Location: game.RenderDir,
            CanConfigure: true);
    }

    /// <summary>Rows sorted by how much they want attention: problems first, then untouched games.</summary>
    public static IReadOnlyList<GameRow> Build(IEnumerable<GameEntry> games) =>
        games.Select(For)
            .OrderByDescending(r => r.Tone == StatusTone.Bad)
            .ThenByDescending(r => r.Tone == StatusTone.Warning)
            .ThenBy(r => r.Name, StringComparer.CurrentCulture)
            .ToList();
}
