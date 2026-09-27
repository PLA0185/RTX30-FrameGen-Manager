using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DLSSGManager.UI;
using Microsoft.Win32;

namespace DLSSGManager;

public partial class MainWindow : Window
{
    private readonly AppData _data;
    private OutputLog _log = null!;
    private CancellationTokenSource? _scanCts;

    /// <summary>Cancel + pause handles for the running mod-file download, when one is in flight.</summary>
    private CancellationTokenSource? _fetchCts;
    private ModFetcher.DownloadGate? _fetchGate;

    private bool _busy;

    /// <summary>Keeps overlapping status refreshes from racing over the same game properties.</summary>
    private bool _statusRefreshRunning;

    /// <summary>Prevents the language handler firing while the picker is being populated.</summary>
    private bool _suppressLanguageChange;

    /// <summary>Same, for the theme picker.</summary>
    private bool _suppressThemeChange;

    /// <summary>
    /// Set while the entry-name picker is being rebuilt: replacing the items raises SelectionChanged
    /// with a value the user never chose, which would otherwise overwrite the game's preference.
    /// </summary>
    private bool _suppressProxyChange;

    /// <summary>
    /// Whether the mod file source is usable, or null when nothing has been fetched yet. Kept so the
    /// badge colours can be re-applied after a theme switch, since they are set from code.
    /// </summary>
    private bool? _modSourceValid;

    /// <summary>
    /// Last GPU probe result, kept so the advice line can be re-rendered after a language change.
    /// The text is produced from code, so it does not follow the XAML bindings.
    /// </summary>
    private GpuInfo? _gpuInfo;

    public MainWindow()
    {
        // Loaded once and reused: the language has to be applied before the first XAML string is
        // resolved (otherwise the window renders in the default language and switches a moment later),
        // and the rest of the setup needs the same data.
        _data = LibraryStore.Load();

        // **库损坏时必须告诉用户**，否则界面会把「库读不出来」显示成**和「首次运行」一模一样**的空列表
        // —— 用户会以为游戏列表被删了，而它其实就在旁边那个 `.corrupt-<时间戳>` 文件里。
        // （Pass D 报出：字节保留只是这条缺陷的一半。）
        if (LibraryStore.LastLoadWasDegraded)
        {
            var detail = LibraryStore.LastLoadSalvagePath is { } saved
                ? $"原文件已保留为：\n{saved}"
                : "原文件无法保留，它可能在下一次保存时被覆盖。";

            MessageBox.Show(
                "游戏列表读不出来（文件可能已损坏），本次以空列表启动。\n\n"
                + detail + "\n\n你可以先退出本工具，用那个文件恢复。",
                "游戏列表读取失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Loc.SetLanguage(_data.InterfaceLanguage);
        Theme.Apply(_data.InterfaceTheme);

        InitializeComponent();

        AppPaths.EnsureCreated();
        _log = new OutputLog(OutputBox);

        VersionText.Text = AppVersion.Label;
        Title = Loc.T("App.Name") + "  " + AppVersion.Label;

        BuildLocalizedCombos();
        BuildLanguageCombo();
        BuildThemeCombo();
        BuildProxyCombo();

        // Bound straight to the persisted collection: no copy can drift out of sync with the file.
        GameList.ItemsSource = _data.Games;
        if (_data.Games.Count > 0) GameList.SelectedIndex = 0;

        Loaded += OnLoaded;
    }

    private GameEntry? Selected => GameList.SelectedItem as GameEntry;

    /// <summary>
    /// Fills the numeric dropdowns from the string table. Called again after a language change, since
    /// these items carry display text rather than a binding.
    /// </summary>
    private void BuildLocalizedCombos()
    {
        var frames = FrameCombo.SelectedValue;
        FrameCombo.ItemsSource = new[]
        {
            new Choice(1, Loc.T("Detail.Frames2X")),
            new Choice(2, Loc.T("Detail.Frames3X")),
            new Choice(3, Loc.T("Detail.Frames4X")),
            new Choice(4, Loc.T("Detail.Frames5X")),
            new Choice(5, Loc.T("Detail.Frames6X")),
        };
        if (frames is not null) FrameCombo.SelectedValue = frames;

        var level = LogCombo.SelectedValue;
        LogCombo.ItemsSource = new[]
        {
            new Choice(0, Loc.T("Detail.Log0")),
            new Choice(1, Loc.T("Detail.Log1")),
            new Choice(2, Loc.T("Detail.Log2")),
            new Choice(3, Loc.T("Detail.Log3")),
        };
        if (level is not null) LogCombo.SelectedValue = level;

        // The 0.3.3 consistency tier. Labels are translated, so the items are rebuilt on a
        // language change like the other numeric dropdowns; the selected value is the plain 0-3.
        var tier = TierCombo.SelectedValue;
        TierCombo.ItemsSource = new[]
        {
            new Choice(0, Loc.T("Detail.Tier0")),
            new Choice(1, Loc.T("Detail.Tier1")),
            new Choice(2, Loc.T("Detail.Tier2")),
            new Choice(3, Loc.T("Detail.Tier3")),
        };
        if (tier is not null) TierCombo.SelectedValue = tier;
    }

    /// <summary>Fills the language picker without triggering the change handler.</summary>
    private void BuildLanguageCombo()
    {
        _suppressLanguageChange = true;
        LanguageCombo.ItemsSource = Languages.All
            .Select(code => new TextChoice(code, Languages.DisplayName(code)))
            .ToList();
        LanguageCombo.SelectedValuePath = "Value";
        LanguageCombo.DisplayMemberPath = "Text";
        LanguageCombo.SelectedValue = Loc.Current;
        _suppressLanguageChange = false;
    }

    /// <summary>Fills the theme picker without triggering the change handler.</summary>
    private void BuildThemeCombo()
    {
        _suppressThemeChange = true;
        ThemeCombo.ItemsSource = new[]
        {
            new TextChoice(ThemeKeys.StorageValue(AppTheme.Dark), Loc.T("Theme.Dark")),
            new TextChoice(ThemeKeys.StorageValue(AppTheme.Light), Loc.T("Theme.Light")),
        };
        ThemeCombo.SelectedValuePath = "Value";
        ThemeCombo.DisplayMemberPath = "Text";
        ThemeCombo.SelectedValue = ThemeKeys.StorageValue(Theme.Current);
        _suppressThemeChange = false;
    }

    /// <summary>
    /// Fills the entry-name picker from the current mod source: the project's own candidates, then any
    /// proxy DLLs the user has added. <b>数量不要写在这里</b> —— 读 <c>ModSource.AvailableProxies</c>。
    ///
    /// Rebuilt rather than declared in XAML because the list changes when a file is added, and because
    /// an imported entry carries a suffix that has to follow the interface language. The value written
    /// back for a pick is the entry name itself — never the label, which is translated.
    /// </summary>
    private void BuildProxyCombo()
    {
        var wanted = Selected?.PreferredProxy ?? ProxyCombo.SelectedValue as string;

        _suppressProxyChange = true;
        try
        {
            var found = ModSourceLocator.FindExisting(_data.ModSourcePath);
            var imported = found is null ? Array.Empty<string>() : new ModSource(found).ImportedProxies.ToArray();

            ProxyCombo.Items.Clear();
            ProxyCombo.Items.Add(new ComboBoxItem { Content = Loc.T("Common.Auto"), Tag = DeploymentService.AutoProxy });

            foreach (var name in ModSource.ProxyCandidates.Concat(imported))
            {
                var label = ModSource.ProxyCandidates.Contains(name, StringComparer.OrdinalIgnoreCase)
                    ? name
                    : name + Loc.T("Proxy.ImportedSuffix");

                ProxyCombo.Items.Add(new ComboBoxItem { Content = label, Tag = name });
            }

            // A preference this source can no longer provide (its DLL was deleted from the mod folder)
            // leaves the picker empty; the deploy path reports it rather than silently choosing another.
            if (wanted is not null) ProxyCombo.SelectedValue = wanted;
        }
        finally
        {
            _suppressProxyChange = false;
        }
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressThemeChange) return;
        if (ThemeCombo.SelectedItem is not TextChoice choice) return;

        var wanted = ThemeKeys.Parse(choice.Value);
        if (wanted == Theme.Current) return;

        Theme.Apply(wanted);
        ColorFromCode();

        // The list's status dots and status text bind through ThemeBrushConverter, which only
        // re-evaluates when the source notifies — the dictionary swap alone leaves them in the
        // previous theme's colours.
        foreach (var game in _data.Games) game.RaiseThemeColors();

        _data.InterfaceTheme = ThemeKeys.StorageValue(wanted);
        LibraryStore.Save(_data);
    }

    /// <summary>
    /// Re-applies the colours that are assigned from code. These sit outside the DynamicResource
    /// mechanism, so a theme switch would otherwise leave the mod-source badge and status heading in
    /// the previous theme's colours.
    /// </summary>
    private void ColorFromCode()
    {
        var badgeKey = _modSourceValid switch
        {
            true => Palette.BadgeOk,
            false => ThemeKeys.DangerBackground,
            null => Palette.BadgeWarn,
        };

        ModSourceBadge.Background = Theme.Brush(badgeKey);
        ModSourceBadgeText.Foreground = Theme.Brush(_modSourceValid switch
        {
            true => "BadgeOkText",
            false => "DangerText",
            null => "BadgeWarnText",
        });

        UpdateStatusCard();
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLanguageChange) return;
        if (LanguageCombo.SelectedItem is not TextChoice choice) return;

        var language = choice.Value;

        // WPF raises SelectionChanged again while the dropdown's item containers are realised, and
        // that second event is not distinguishable from a user pick. Without this guard the handler
        // would run again with the same value, and — because it used to act unconditionally — it also
        // persisted the result, so a startup-time spurious event silently overwrote the saved
        // language. Acting only on a genuine change makes repeated events harmless.
        if (string.Equals(language, Loc.Current, StringComparison.Ordinal)) return;

        Loc.SetLanguage(language);

        // Text set from code does not follow the bindings, so it is re-applied here.
        BuildLocalizedCombos();
        BuildProxyCombo();
        RefreshCodeText();
        RefreshModSource();
        UpdateStatusCard();
        RefreshAllStatus();

        _data.InterfaceLanguage = language;
        LibraryStore.Save(_data);
    }

    /// <summary>
    /// Re-applies the interface text that is assigned from code rather than bound in XAML, so a
    /// language change does not leave part of the window in the previous language.
    /// </summary>
    private void RefreshCodeText()
    {
        // The version is language-independent, but the title text is not — and the code-assigned
        // title has replaced the loc binding from XAML, so re-apply it here.
        Title = Loc.T("App.Name") + "  " + AppVersion.Label;

        AdminButton.Content = Loc.T(Native.IsElevated() ? "Toolbar.AlreadyAdmin" : "Toolbar.RestartAdmin");

        // The game rows bind to computed properties on GameEntry, which the language change cannot
        // reach on its own — see RaiseLocalizedText.
        foreach (var game in _data.Games) game.RaiseLocalizedText();

        ApplyGpuText();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AdminButton.IsEnabled = !Native.IsElevated();
        BuildProviderCombo();
        RefreshCodeText();

        RefreshModSource();
        RefreshAllStatus();

        _log.Write(Loc.T("Status.DataDir", AppPaths.Root));
        if (!Native.IsElevated())
            _log.Write(Loc.T("Status.NoPermissionHint"));

        ProbeGpuInBackground();

        // A fresh install has no mod files: the installer deliberately does not download them (no
        // network access during setup, no half-finished downloads). Fetch them here instead, without
        // asking — it is the one thing a new install has to do before anything else works.
        if (!HasModSource) FetchModFilesOnFirstRun();
    }

    /// <summary>
    /// First run: detect which version upstream is publishing, then download it.
    ///
    /// The version probe is advisory — it reads the few hundred bytes of the published INI and reports
    /// the banner, so the log says which version was fetched rather than only how many files arrived.
    /// If the probe fails (a blocked endpoint, no network yet) the download runs anyway: detection must
    /// never be the reason a fresh install cannot get its files.
    /// </summary>
    private async void FetchModFilesOnFirstRun()
    {
        if (_busy) return;

        var target = ModSourceLocator.ResolveTarget(_data.ModSourcePath);
        _log.Write(Loc.T("Fetch.AutoStart", target));

        _busy = true;

        OpResult result;
        try
        {
            result = await RunModFetchAsync(target, ModFetcher.AutoSourceId);
        }
        catch (OperationCanceledException)
        {
            _busy = false;
            BatchStatusText.Text = "";
            _log.Write(Loc.T("Fetch.Cancelled"));
            return;
        }
        catch (Exception ex)
        {
            _busy = false;
            BatchStatusText.Text = "";
            // Kept non-blocking on purpose: the toolbar button and the status banner both point at
            // the same retry, so a failed first run still leaves a usable window.
            _log.Write(Loc.T("Fetch.AutoFailed", ex.Message));
            AppPaths.Log("首次获取 Mod 文件失败: " + ex);
            return;
        }

        _busy = false;
        BatchStatusText.Text = "";
        _log.Details(result.Lines);
        _log.Result(result.Ok, result.Message);

        RefreshModSource();
        if (result.Ok)
        {
            // The body already starts with the result message (its {0}) — do not repeat it.
            MessageBox.Show(this, Loc.T("Fetch.DoneBodyFirst", result.Message),
                Loc.T("Fetch.DoneTitleFirst"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            _log.Write(Loc.T("Fetch.AutoFailed", result.Message));
        }
    }

    /// <summary>
    /// Views and edits the GPU display name.
    ///
    /// Some games gate features such as frame generation on the reported model, which is the reason the
    /// edit exists. Only the registry display name is written — the hardware id, the driver and its
    /// capabilities are untouched — and "restore" puts back the name bound to the physical device, which
    /// is what undoes a spoof even when the spoofing tool is gone.
    /// </summary>
    private void GpuName_Click(object sender, RoutedEventArgs e)
    {
        var adapter = Gpu.NvidiaAdapter();
        if (adapter is null)
        {
            _log.Write(Loc.T("GpuName.NoGpu"));
            return;
        }

        var registryName = Gpu.RegistryDisplayName(adapter.DeviceId, adapter.Name);
        var realName = Gpu.PnpDeviceDescription(adapter.DeviceInstancePath);

        var dialog = new GpuNameDialog(adapter.Name, registryName, realName) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var target = dialog.Restore ? realName : dialog.NewName;
        if (string.IsNullOrWhiteSpace(target))
        {
            _log.Write("✗ " + Loc.T("GpuName.NoRealName"));
            return;
        }

        var error = dialog.Restore ? null : Gpu.InvalidDisplayNameReason(target);
        if (error is not null)
        {
            _log.Write("✗ " + error);
            return;
        }

        var message = Gpu.WriteRegistryDisplayName(adapter.DeviceId, adapter.Name, target);
        if (message is not null)
        {
            _log.Write("✗ " + message + "  " + Loc.T("GpuName.AdminHint"));
            MessageBox.Show(this, message + "\n\n" + Loc.T("GpuName.AdminHint"),
                Loc.T("GpuName.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _log.Write(dialog.Restore ? Loc.T("GpuName.Restored", target) : Loc.T("GpuName.Changed", target));

        // The running process may still report the old name until it restarts; re-probe anyway so the
        // toolbar refreshes whenever Windows decides to answer with the new value.
        ProbeGpuInBackground();
    }

    private async void ProbeGpuInBackground()
    {
        try
        {
            var info = await Task.Run(Gpu.Probe);
            _gpuInfo = info;

            _data.GpuName = info.Name;
            _data.GpuDriver = info.Driver;
            _data.RecommendedRouter = info.Router;
            LibraryStore.Save(_data);

            ApplyGpuText();
            _log.Write(Loc.T("Toolbar.Gpu") + " " + info.Name + " · " + info.Advice);
        }
        catch (Exception ex)
        {
            // The toolbar simply stays empty; a probe failure is not worth interrupting the user.
            AppPaths.Log("显卡探测失败: " + ex);
        }
    }

    /// <summary>
    /// Renders the GPU lines. Called both when the probe finishes and after a language change, since
    /// the advice text is built in code and would otherwise stay in the previous language.
    ///
    /// The row shows a one-line summary; the full advice (the long mismatch explanation, the
    /// machine-match note) goes on the tooltip, and the startup log already carries it too.
    /// </summary>
    private void ApplyGpuText()
    {
        if (_gpuInfo is null) return;

        GpuText.Text = _gpuInfo.Name;
        RouterHintText.Text = Gpu.CompactAdvice(_gpuInfo);
        RouterHintText.ToolTip = Gpu.AdviceFor(_gpuInfo);
    }

    // ---- mod source ---------------------------------------------------------

    /// <summary>
    /// Where mod files are read from. Resolved through ModSourceLocator so a source checkout run from
    /// bin\ still finds the repository's mod\ folder.
    /// </summary>
    private string SourcePath =>
        ModSourceLocator.FindExisting(_data.ModSourcePath) ?? _data.ModSourcePath;

    // ═══════════ Provider 选择 ═══════════

    /// <summary>Prevents the change handler from firing while the list is being rebuilt.</summary>
    private bool _suppressProviderChange;

    /// <summary>
    /// Builds the provider list from the registry.
    ///
    /// <para>Driven by what this build actually registers rather than by a hard-coded list, so a provider that
    /// is not present simply is not offered — instead of being offered and then failing when chosen.</para>
    /// </summary>
    private void BuildProviderCombo()
    {
        _suppressProviderChange = true;

        try
        {
            ProviderCombo.Items.Clear();

            foreach (var provider in Providers.AppProviders.Registry.All)
            {
                ProviderCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"{provider.Metadata.DisplayName}（{provider.Id}）",
                    Tag = provider.Id,
                });
            }

            var wanted = string.IsNullOrWhiteSpace(_data.PreferredProviderId)
                ? Providers.DlssgSm86Provider.ProviderId
                : _data.PreferredProviderId;

            foreach (ComboBoxItem item in ProviderCombo.Items)
            {
                if (item.Tag is string id && id == wanted)
                {
                    ProviderCombo.SelectedItem = item;
                    break;
                }
            }
        }
        finally
        {
            _suppressProviderChange = false;
        }
    }

    /// <summary>
    /// Remembers the chosen provider.
    ///
    /// <para>The choice is stored, not applied retroactively: changing it does not alter an installation that is
    /// already on disk, and the message says so rather than implying the previous one was undone.</para>
    /// </summary>
    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProviderChange) return;
        if (ProviderCombo.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not string id) return;

        _data.PreferredProviderId = id;
        LibraryStore.Save(_data);
        _log.Write($"配置方式已切换为「{item.Content}」，对之后的操作生效。");
    }

    // ═══════════ 页面导航 ═══════════

    private AppPage _currentPage = NavigationModel.DefaultPage;

    /// <summary>
    /// Switches the visible page.
    ///
    /// Both navigation lists share this handler, so selecting in one clears the other: two highlighted
    /// entries at once would leave it unclear which page is actually on screen.
    /// </summary>
    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox source || source.SelectedItem is not ListBoxItem item) return;

        var other = ReferenceEquals(source, NavPrimary) ? NavAdvanced : NavPrimary;
        if (other.SelectedIndex >= 0) other.SelectedIndex = -1;

        if (item.Tag is string tag && Enum.TryParse<AppPage>(tag, out var page))
            ShowPage(page);
    }

    /// <summary>Shows exactly one page, then refreshes what it says.</summary>
    private void ShowPage(AppPage page)
    {
        _currentPage = page;

        PageDashboard.Visibility = Visible(page == AppPage.Dashboard);
        PageLibrary.Visibility = Visible(page == AppPage.Library);
        PageGameDetails.Visibility = Visible(page == AppPage.GameDetails);
        PageUpdates.Visibility = Visible(page == AppPage.Updates);
        PageDownloads.Visibility = Visible(page == AppPage.Downloads);
        PageDiagnostics.Visibility = Visible(page == AppPage.Diagnostics);
        PageSettings.Visibility = Visible(page == AppPage.Settings);

        RefreshPageContent();

        static Visibility Visible(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Fills the page summaries from the same core state the rest of the window already uses.
    ///
    /// Plain text rather than resource keys, matching how the core services describe themselves
    /// (ProviderHealth.Reason, UpdateNotification). Two vocabularies for one fact is how an interface and
    /// its engine start disagreeing.
    /// </summary>
    private void RefreshPageContent()
    {
        var games = _data.Games;

        DashboardMachine.Text = $"{GpuText.Text}\n{SubtitleText.Text}";

        DashboardNextStep.Text = games.Count == 0
            ? "还没有游戏：先扫描 Steam 库，或手动添加一个游戏目录。"
            : Selected is null
                ? $"已有 {games.Count} 个游戏，但尚未选中。到「游戏库」里选择要配置的游戏。"
                : $"已选中「{Selected.Name}」。到「游戏详情」查看它的检测结果与安装计划。";

        DetailsSummary.Text = Selected is null
            ? "未选中游戏。"
            : $"{Selected.Name}\n{Selected.RenderDir}";

        // Filled from state the window already holds, not from a sentence about the page. A summary that only
        // describes where information lives cannot be wrong — and cannot be useful either. Each of these starts
        // with what is true right now, then says where the rest is.
        var deployed = games.Where(g => g.Deployment is not null).ToList();

        UpdatesSummary.Text = deployed.Count == 0
            ? $"当前版本 {AppVersion.Label}。还没有游戏安装过补丁，因此没有可显示的补丁版本。"
            : $"当前版本 {AppVersion.Label}。本软件与第三方补丁的更新是两套独立流程。\n已安装的补丁版本：\n" +
              string.Join("\n", deployed.Select(g =>
                  // **`ModVersion` 是非 nullable 的 `string`（默认 `""`），所以 `?? "…"` 永远不会触发。**
                  // 而版本读不到时它现在就是空串（`ModSource.Version` 不再回落到本地化文案）
                  // ⇒ 直接拼出来会显示成 `· 游戏名 — `（一个悬空的破折号）。
                  // 用 `IsNullOrWhiteSpace` 才是真的兜底。
                  $"· {g.Name} — {(string.IsNullOrWhiteSpace(g.Deployment!.ModVersion) ? "(未记录版本)" : g.Deployment.ModVersion)}" +
                  (string.IsNullOrEmpty(g.Deployment.ProviderId)
                      ? "（未记录来源 Provider）"
                      : $"（{g.Deployment.ProviderId}）")));

        // Read from disk: this is the one page whose subject is the payload store itself, and the store is the file
        // system. Listing nothing is a truthful answer when nothing has been downloaded.
        var payloadRoot = DLSSGManager.Providers.PayloadPaths.Root;

        var payloads = System.IO.Directory.Exists(payloadRoot)
            ? System.IO.Directory.GetDirectories(payloadRoot)
                .SelectMany(p => System.IO.Directory.GetDirectories(p)
                    .Select(v => $"· {System.IO.Path.GetFileName(p)} → {System.IO.Path.GetFileName(v)}"))
                .ToList()
            : new List<string>();

        DownloadsSummary.Text = payloads.Count == 0
            ? "传输进度显示在窗口底部的进度条中。当前没有已下载的 Mod 文件。"
            : "传输进度显示在窗口底部的进度条中。已下载的 Mod 文件：\n" + string.Join("\n", payloads);

        DiagnosticsSummary.Text = $"日志位于窗口底部。兼容性数据库与证据规则见 docs/COMPATIBILITY.md；" +
                                  "启动时会读取你已有的兼容性记录并用于判定，**只有它为空时**才会出现" +
                                  "「没有任何条目达到 Project Verified」的情况。\n" +
                                  $"已登记游戏 {games.Count} 个，其中 {deployed.Count} 个有部署记录。";

        SettingsSummary.Text = "语言与主题在上方工具栏；代理入口、图形 API 与 NVIDIA Profile 相关设置" +
                               "属于高级项，默认折叠在「游戏详情」的高级区域。";
    }

    private ModSource CurrentSource() => new(SourcePath);

    private bool HasModSource => ModSourceLocator.FindExisting(_data.ModSourcePath) is not null;

    private void RefreshModSource()
    {
        var existing = ModSourceLocator.FindExisting(_data.ModSourcePath);

        if (existing is null)
        {
            // Show where a download would land, and say plainly that files are missing.
            var target = ModSourceLocator.ResolveTarget(_data.ModSourcePath);
            ModSourceText.Text = target;
            ModSourceBadgeText.Text = Loc.T("Toolbar.ModNotReady");
            _modSourceValid = null;
            ColorFromCode();
            ModSourceText.ToolTip = Loc.T("Toolbar.ModMissingTip");
            ApplySettingsSchema(legacy: false);
            _log.Write(Loc.T("Fetch.NotReadyLog"));
            return;
        }

        var source = new ModSource(existing);
        ModSourceText.Text = existing;
        ModSourceBadgeText.Text = source.IsValid ? Loc.T("Toolbar.ModReady", source.DisplayVersion) : Loc.T("Toolbar.ModIncomplete");
        _modSourceValid = source.IsValid;
        ColorFromCode();
        ModSourceText.ToolTip = source.IsValid
            ? Loc.T("Toolbar.ModReadyTip", Loc.Join(source.AvailableProxies))
            : source.ValidationMessage;

        ApplySettingsSchema(source.IsLegacySchema);

        if (!source.IsValid) _log.Write(Loc.T("Fetch.IncompleteLog", source.ValidationMessage));
    }

    /// <summary>
    /// Shows the configuration controls the local payload actually reads.
    ///
    /// Upstream changed the INI schema in 0.3.0 (Enabled / Optimized / Preset replaced Router /
    /// KernelImage / HardwareBilinear), and the manager only writes keys the template defines. Leaving
    /// the other set on screen would offer controls that silently do nothing.
    /// </summary>
    private void ApplySettingsSchema(bool legacy)
    {
        ModernSettings.Visibility = legacy ? Visibility.Collapsed : Visibility.Visible;
        LegacySettings.Visibility = legacy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMod_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { _log.Write(Loc.T("Scan.Busy")); return; }

        var target = ModSourceLocator.ResolveTarget(_data.ModSourcePath);

        // The picker replaces a plain confirmation dialog: choosing where to download from is the one
        // decision worth surfacing here, and it doubles as the confirmation step.
        var picker = new SourcePickerDialog { Owner = this };
        if (picker.ShowDialog() != true) return;

        DownloadModFiles(target, picker.SelectedSourceId);
    }

    /// <summary>
    /// Adds a proxy DLL the user picked to the entry-name list, so it can be deployed like the bundled
    /// entries — the case this exists for is a community build such as d3d12.dll, which this project
    /// does not ship.
    ///
    /// The manager did not download the file and cannot vouch for it. What it does is copy it
    /// faithfully, say what signature (if any) it carries, and warn when the name is one games never
    /// load, because then the deployment would simply do nothing.
    /// </summary>
    private void AddProxy_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { _log.Write(Loc.T("Scan.Busy")); return; }

        var found = ModSourceLocator.FindExisting(_data.ModSourcePath);
        if (found is null)
        {
            _log.Write(Loc.T("Proxy.AddNeedSource"));
            MessageBox.Show(this, Loc.T("Proxy.AddNeedSource"), Loc.T("Proxy.AddTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFileDialog { Title = Loc.T("Proxy.AddTitle"), Filter = Loc.T("Proxy.AddFilter") };
        if (dialog.ShowDialog() != true) return;

        var name = Path.GetFileName(dialog.FileName);

        // The entry name is the DLL name the game resolves, so a name nothing loads means the mod never
        // runs. The file is the user's, which makes the decision theirs too; the manager only makes the
        // consequence explicit before the file is copied in.
        if (!ModSource.IsKnownProxyName(name))
        {
            if (MessageBox.Show(this, Loc.T("Proxy.AddUnknownName", name), Loc.T("Proxy.AddTitle"),
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;
        }

        var result = ModSource.ImportProxy(found, dialog.FileName);
        _log.Details(result.Lines);
        _log.Result(result.Ok, result.Message);

        if (!result.Ok) return;

        RefreshModSource();
        BuildProxyCombo();
        UpdateStatusCard();
    }

    /// <summary>
    /// Downloads the mod files into <paramref name="target"/>, for the toolbar button.
    /// </summary>
    /// <param name="sourceId">
    /// A specific source id, or <see cref="ModFetcher.AutoSourceId"/> to try each in turn.
    /// </param>
    private async void DownloadModFiles(string target, string sourceId = ModFetcher.AutoSourceId)
    {
        if (_busy) { _log.Write(Loc.T("Scan.Busy")); return; }

        _busy = true;
        _log.Write(Loc.T("Fetch.StartLog"));

        OpResult result;
        try
        {
            result = await RunModFetchAsync(target, sourceId);
        }
        catch (OperationCanceledException)
        {
            _busy = false;
            BatchStatusText.Text = "";
            _log.Write(Loc.T("Fetch.Cancelled"));
            return;
        }
        catch (Exception ex)
        {
            _busy = false;
            BatchStatusText.Text = "";
            _log.Write("✗ " + ex.Message);
            AppPaths.Log("下载 Mod 文件失败: " + ex);
            return;
        }

        _busy = false;
        BatchStatusText.Text = "";
        _log.Details(result.Lines);
        _log.Result(result.Ok, result.Message);

        // Re-read the profile defaults from the freshly downloaded INI text.
        RefreshModSource();
        if (result.Ok)
        {
            // The body already starts with the result message (its {0}), so it is not repeated here.
            MessageBox.Show(this,
                Loc.T("Fetch.DoneBody", result.Message),
                Loc.T("Fetch.DoneTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>
    /// Shared fetch runner for the toolbar button and the first-run fetch: probe, then download with
    /// the progress panel visible. The panel carries a byte-level progress bar, the transfer speed,
    /// a pause switch (effective at file boundaries) and cancel — cancel is what makes switching
    /// sources easy: nothing is written to the mod folder until verification passes, so an aborted
    /// download leaves the previous payload intact and the user can immediately pick another source.
    /// </summary>
    private async Task<OpResult> RunModFetchAsync(string target, string sourceId)
    {
        _fetchCts = new CancellationTokenSource();
        _fetchGate = new ModFetcher.DownloadGate();
        var ct = _fetchCts.Token;

        FetchPauseButton.Content = Loc.T("Fetch.Pause");
        FetchProgressBar.Value = 0;
        FetchProgressBar.IsIndeterminate = false;
        FetchProgressSpeed.Text = "";
        FetchProgressPanel.Visibility = Visibility.Visible;

        var textProgress = UiProgress();
        var byteProgress = new Progress<ModFetcher.FetchProgress>(UpdateFetchProgress);

        try
        {
            var detected = await ModFetcher.DetectLatestVersionAsync(ct);
            if (detected is not null) textProgress.Report(Loc.T("Fetch.AutoDetected", detected));

            return await ModFetcher.DownloadIntoAsync(target, textProgress, ct, sourceId, detected,
                                                      byteProgress, _fetchGate);
        }
        finally
        {
            FetchProgressPanel.Visibility = Visibility.Collapsed;
            _fetchCts.Dispose();
            _fetchCts = null;
            _fetchGate = null;
        }
    }

    /// <summary>Applies one byte-progress tick. Runs on the UI thread (Progress&lt;T&gt; posts).</summary>
    private void UpdateFetchProgress(ModFetcher.FetchProgress p)
    {
        FetchProgressText.Text = p.FileTotal <= 1
            ? Loc.T("Fetch.ProgressArchive", p.SourceName)
            : Loc.T("Fetch.ProgressFiles", p.SourceName, p.FileIndex, p.FileTotal);

        if (p.BytesTotal > 0)
        {
            FetchProgressBar.IsIndeterminate = false;
            FetchProgressBar.Value = Math.Clamp(p.BytesDone * 100.0 / p.BytesTotal, 0, 100);
        }
        else
        {
            // No Content-Length from this endpoint: the bar cannot show a fraction.
            FetchProgressBar.IsIndeterminate = true;
        }

        FetchProgressSpeed.Text = ModFetcher.FormatSpeed(p.SpeedBps);
    }

    private void FetchPause_Click(object sender, RoutedEventArgs e)
    {
        var gate = _fetchGate;
        if (gate is null) return;

        if (gate.Paused)
        {
            gate.Resume();
            FetchPauseButton.Content = Loc.T("Fetch.Pause");
        }
        else
        {
            gate.Pause();
            FetchPauseButton.Content = Loc.T("Fetch.Resume");
        }
    }

    private void FetchCancel_Click(object sender, RoutedEventArgs e) => _fetchCts?.Cancel();

    // ---- game list ----------------------------------------------------------

    private void GameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var game = Selected;
        NoSelectionText.Visibility = game is null ? Visibility.Visible : Visibility.Collapsed;
        DetailPanel.Visibility = game is null ? Visibility.Collapsed : Visibility.Visible;
        if (game is null) return;

        DataContext = game;
        ProxyCombo.SelectedValue = game.PreferredProxy;
        UpdateStatusCard();
    }

    private void ProxyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProxyChange) return;
        if (Selected is null) return;
        if (ProxyCombo.SelectedValue is string value) Selected.PreferredProxy = value;
    }

    private void UpdateStatusCard()
    {
        var game = Selected;
        if (game is null) return;

        StatusTitle.Text = game.StatusText + " — " + game.StatusDetail;
        StatusTitle.Foreground = Theme.Brush(game.StatusColor);
        StatusBody.Text = StatusDetailText(game);

        var deployed = game.Deployment is not null;

        // **恢复按钮问的是「能不能真的恢复」，不是「有没有记录」。**
        //
        // 两者对**老记录**（Pass A 之前，没有 `ProviderId`）给出不同答案：记录在，但恢复必然失败。
        // 用它当启用条件，用户看到的会是一个**可点、点了只得到一行错误**的按钮。
        // （Pass E 报出：这是「判据只有一份」的第三处遗漏。）
        RestoreButton.IsEnabled = CanAttemptRestore(game);

        // **接管按钮问的也必须是「能不能真的接管」**（Pass H 报出：我上一轮在这里写「刻意不改」，
        // 而那个「刻意」建立在一条错误的前提上 —— 我说 `deployed` 的含义是「这份安装已被本工具
        // 接管过」，**它其实只是「有部署记录」**）。
        //
        // `Adopt` 的第一道门是**目录内容**（`FindInstalledProxy(...) is null ⇒ Adopt.NotFound`），
        // 所以「已添加、未部署、目录里没有可识别代理」这个**最常见的初始状态**下，按钮此前是可点的：
        // 用户点它 → 弹一个承诺「之后恢复只能按记录里的 SHA256 删除这些文件」的确认框 → **然后才失败**。
        //
        // ⚠️ **判据只有一份，UI 只读它**（Pass J 的 P1-B：我上一次只修了 `NotDeployed` 那一支，
        // 而第三支仍用 `!deployed || Modified` ⇒ 另外两种状态仍然不同宽）：
        //   · `Modified`（代理被换成无法识别的 DLL）⇒ 旧判据**可点**而 `Adopt` 必 `NotFound`；
        //   · `Missing`（INI 被删、签名代理仍在）⇒ 旧判据**禁用**而 `Adopt` 本可成功。
        // 现在 `Evaluate` **在所有分支**都算 `CanAdopt`（用的就是 `Adopt` 自己那道门
        // `FindInstalledProxy(renderDir, prev: null)`），所以这里**直接读它**、不做任何回退判断。
        // **回退判断 = 第二份判据 = 迟早与第一份不同宽** —— 这正是本会话修过三次的那个形态。
        //
        // 它由 `Evaluate` 在**本来就要做**那次检查的层里算出，所以这里**不再重查**
        // （重查会让窗口冻结数秒）。
        AdoptButton.IsEnabled = game.CanAdopt;
        OpenLogButton.IsEnabled = game.RenderDir is not null &&
                                  Directory.Exists(Path.Combine(game.RenderDir, ModSource.LogDirName));

        // Deployment stays available on games with a kernel anti-cheat; RunDeploy warns and asks for
        // confirmation first. The scan cannot know whether this game's protection lets a given entry
        // name survive — a community entry such as d3d12.dll exists precisely because some do — so the
        // decision is the user's, not the scan's.
        DeployButton.ToolTip = game.HasKernelAntiCheat ? Loc.T("Deploy.BlockedTooltip") : null;
    }

    private static string StatusDetailText(GameEntry game)
    {
        if (game.Deployment is null)
            return Loc.T("Status.DeployHint");

        var parts = new List<string>
        {
            game.Deployment.ProxyName,
            // 同上：空版本不能让这里拼出 `Mod `（尾随空格）。
            string.IsNullOrWhiteSpace(game.Deployment.ModVersion)
                ? Loc.T("ModSource.UnknownVersion")
                : "Mod " + game.Deployment.ModVersion,
            game.Deployment.DeployedAt,
        };

        if (game.Deployment.Backups.Count > 0)
            parts.Add(Loc.T("Detail.BackupCount", game.Deployment.Backups.Count));

        return string.Join(" · ", parts);
    }

    private void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("List.AddFolderTitle") };
        if (!string.IsNullOrWhiteSpace(_data.LastScanRoot) && Directory.Exists(_data.LastScanRoot))
            dialog.InitialDirectory = _data.LastScanRoot;
        if (dialog.ShowDialog() != true) return;

        var folder = dialog.FolderName;
        _data.LastScanRoot = folder;

        var game = new GameEntry
        {
            Name = Detection.FriendlyName(folder),

            // A folder picked by hand is not a Steam install. Recording that keeps a store-specific compatibility
            // result from being applied to something it never covered.
            Store = GameDetection.StoreKind.Manual,

            PreferredProxy = DeploymentService.AutoProxy,
            Profile = new GameProfile { Router = _data.RecommendedRouter },
        };

        _data.Games.Add(game);
        GameList.SelectedItem = game;
        _log.Write(Loc.T("List.AddedMessage", folder));

        // AttachFolder fills in the render directory, runs the anti-cheat scan and warns if needed.
        AttachFolder(game, folder);
    }

    private void RemoveGame_Click(object sender, RoutedEventArgs e)
    {
        var game = Selected;
        if (game is null) return;

        var body = Loc.T("List.RemoveConfirm", game.Name);
        if (game.Deployment is not null)
            body += Loc.T("List.RemoveWarnDeployed");

        if (MessageBox.Show(body, Loc.T("List.RemoveTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        _data.Games.Remove(game);
        LibraryStore.Save(_data);
        GameList.SelectedIndex = _data.Games.Count > 0 ? 0 : -1;
    }

    // ---- status -------------------------------------------------------------

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        var game = Selected;
        if (game is null) return;

        var check = await Task.Run(() => DeploymentService.Evaluate(game));
        DeploymentService.Apply(game, check);

        UpdateStatusCard();
        _log.Write(Loc.T("Status.CheckResult", game.Name, game.StatusText, game.StatusDetail));
    }

    private void RefreshAll_Click(object sender, RoutedEventArgs e) => RefreshAllStatus();

    /// <summary>
    /// Re-reads every game's state.
    ///
    /// The inspection runs on the thread pool because it is expensive: each candidate entry name is
    /// verified with WinVerifyTrust over a ~15 MB DLL, and a deployed game is hashed again. Doing
    /// that inline froze the window for seconds once a few games were listed. Results are applied
    /// back here, since assigning those properties is what raises the change notifications the list
    /// binds to.
    /// </summary>
    private async void RefreshAllStatus()
    {
        // Called at startup, after batch operations, and by the refresh button, so two runs can
        // otherwise overlap and fight over the same properties.
        if (_statusRefreshRunning) return;
        _statusRefreshRunning = true;

        try
        {
            var games = _data.Games.ToList();
            var results = await Task.Run(() =>
                games.Select(g => (Game: g, Check: DeploymentService.Evaluate(g))).ToList());

            foreach (var (game, check) in results) DeploymentService.Apply(game, check);

            LibraryStore.Save(_data);
            UpdateStatusCard();
        }
        catch (Exception ex)
        {
            _log.Write(Loc.T("Status.RefreshFailed", ex.Message));
        }
        finally
        {
            _statusRefreshRunning = false;
        }
    }
}
