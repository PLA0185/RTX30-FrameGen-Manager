using System.IO;
using System.Windows;
using System.Windows.Controls;
using DLSSGManager.Compatibility;
using DLSSGManager.GameDetection;
using DLSSGManager.NvidiaProfile;
using DLSSGManager.Orchestration;
using Microsoft.Win32;

namespace DLSSGManager;

/// <summary>Deployment, restore and scan handlers.</summary>
/// <remarks>
/// Every operation here follows the same shape — set the busy flag, await the expensive work off
/// the UI thread, apply results back on it, and always release the flag in a finally. That shape
/// is deliberate: the synchronous originals froze the window (deployment enumerates every process,
/// hashes and trust-verifies a ~15 MB DLL, and scans the game folder), and the first async
/// conversion used ContinueWith chains whose t.Result access swallowed faults silently and could
/// leave the busy flag stuck — which locked every button until restart.
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// The configuration pipeline, built once on first use.
    ///
    /// <para>The window does not own this sequence. Detection, planning, installation and verification are a
    /// service, and calling it is what makes the install match the plan the user is shown — before this, the
    /// button went straight to a provider's <c>Install</c> and the plan was never consulted.</para>
    /// </summary>
    private GameConfigurationService? _configuration;

    private GameConfigurationService Configuration => _configuration ??= new GameConfigurationService(
        new SmoothMotionWorkflow(
            new AppWorkflowDetector(),
            new NvidiaProfileService(new NvApiDrsAdapter()),
            new CompatibilityMatrixStore(Path.Combine(AppPaths.Root, "compatibility.json")),
            new RecipeMemoryStore(Path.Combine(AppPaths.Root, "recipe-memory.json"))));

    /// <summary>
    /// Runs the workflow for one game and reports what happened, or null when the run could not start.
    ///
    /// Kept separate from the click handler so the anti-cheat dialog and the confirmation dialog can both
    /// happen <i>before</i> any write, and so a second run after confirmation uses the same path as the first.
    /// </summary>
    private async Task<ConfigurationOutcome?> RunConfigurationAsync(
        GameEntry game, Providers.IPatchProvider provider, bool allowProtected, bool confirmed)
    {
        try
        {
            var request = new ConfigurationRequest(
                Game: game,
                Provider: provider,
                ProviderVersion: provider.GetInstalledVersion(game) ?? game.Deployment?.ModVersion ?? "",
                // P0-07：null 让工作流按 provider id + 解析出的版本去 PayloadPaths.For(...) 取目录 —— 那才是
                // 真正的 Provider/版本隔离。此前这里传的是界面上那个 mod 源路径，于是所有 provider、所有版本
                // 共用同一个目录，「隔离」只存在于代码里而没有生效。只有测试与高级诊断才显式指定目录。
                PayloadDirectory: null,
                GpuName: _data.GpuName,
                DriverVersion: _data.GpuDriver,
                Store: game.Store,
                AllowProtected: allowProtected,
                HasKernelAntiCheat: game.Protection?.HasKernelAntiCheat ?? false,
                UserConfirmedUnverified: confirmed,

                // Asked for explicitly, because without it a run would install files and leave the driver
                // untouched. The workflow turns these into typed writes itself — it never invents a value, and
                // a setting whose values are not established is dropped rather than guessed.
                // §9/§10：Profile 设置由 Provider 决定，UI 不再无条件传。
                //
                // dlssg-sm86 的 RequiresSmoothMotionDrs = false —— 它只把代理 DLL 与 INI 放进游戏目录，
                // 驱动那边没有设置必须改。无条件传 Feature + Apis 会让它被强制走 Smooth Motion 路径，
                // 于是在非提权环境下整体 fail-closed —— **而它本来完全可用**。
                // 这正是「DRS 写门关闭 ≠ dlssg-sm86 不能部署」在代码里的落点。
                ProfileSettings: provider.RequiresSmoothMotionDrs
                    ? new[] { SmoothMotionSettings.Feature, SmoothMotionSettings.Apis }
                    : Array.Empty<NvidiaProfile.ProfileSetting>());

            return await Configuration
                .ConfigureAsync(request, new Progress<string>(s => BatchStatusText.Text = s))
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Write("✗ " + ex.Message);
            AppPaths.Log("配置失败: " + ex);
            return null;
        }
    }

    /// <summary>
    /// The provider to configure with: the user's choice when they made one, otherwise the built-in one.
    ///
    /// <para>An unknown or now-missing id falls back to the built-in provider rather than failing. A stored
    /// choice can outlive the provider it named, and refusing to configure anything because a build dropped a
    /// provider would be a worse outcome than using the one that is certainly present.</para>
    /// </summary>
    private Providers.IPatchProvider SelectedProvider()
    {
        var chosen = _data.PreferredProviderId;

        if (!string.IsNullOrWhiteSpace(chosen))
        {
            var provider = Providers.AppProviders.Registry.Get(chosen);
            if (provider is not null) return provider;
        }

        return Providers.AppProviders.Patch;
    }

    /// <summary>
    /// The provider that deployed this game's files, or null together with the reason it cannot be determined.
    ///
    /// <para><b>There is deliberately no fallback to the built-in provider.</b> Restoring through a provider that
    /// did not deploy these files either removes the wrong things or refuses after concluding the files belong to
    /// someone else — and either way the user is told nothing they can act on. Saying "the record does not name a
    /// provider" is the honest outcome, and it is what an older deployment will hit.</para>
    /// </summary>
    private (Providers.IPatchProvider? Provider, string Error) ProviderForRestore(GameEntry game)
    {
        var id = game.Deployment?.ProviderId;

        if (string.IsNullOrWhiteSpace(id))
            return (null, "该游戏的部署记录没有标注由哪个 Provider 部署，无法确定用哪一个来还原。");

        var provider = Providers.AppProviders.Registry.Get(id);

        return provider is null
            ? (null, $"部署记录标注的 Provider「{id}」当前不可用，无法还原。")
            : (provider, "");
    }

    // ---- single-game deployment --------------------------------------------

    private void Deploy_Click(object sender, RoutedEventArgs e)
    {
        var game = Selected;
        if (game is null) return;

        if (string.IsNullOrWhiteSpace(game.RenderDir) || !Directory.Exists(game.RenderDir))
        {
            _log.Write("✗ " + Loc.T("Deploy.NeedDir"));
            return;
        }

        RunDeploy(game);
    }

    /// <summary>
    /// Scans for anti-cheat off-thread, asks the user if needed, then deploys. The confirmation has
    /// to precede any file write, so the scan runs first and the dialog appears after it returns.
    /// </summary>
    private async void RunDeploy(GameEntry game)
    {
        if (_busy) { _log.Write(Loc.T("Scan.Busy")); return; }

        _busy = true;
        BatchStatusText.Text = Loc.T("Deploy.Starting", game.Name);

        try
        {
            var protection = await Task.Run(() => AntiCheat.Scan(game.RenderDir));
            game.Protection = protection;

            if (protection.HasKernelAntiCheat)
            {
                var body = Loc.T("Anti.OverrideBody", game.Name, protection.Products, protection.Evidence);

                var answer = MessageBox.Show(body, Loc.T("Anti.OverrideTitle"), MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes)
                {
                    _log.Write(Loc.T("Anti.CancelLog", game.Name, protection.Summary, protection.Evidence));
                    return;
                }

                _log.Write(Loc.T("Anti.OverrideLog", game.Name, protection.Summary));
            }

            var provider = SelectedProvider();
            var outcome = await RunConfigurationAsync(game, provider, protection.HasKernelAntiCheat, confirmed: false);
            if (outcome is null) return;

            // The run stopped because something needs the user's decision. Asking here — before the first write
            // — is the whole point of the confirmation step. Consent lets the run proceed; it does not change
            // what the compatibility state says.
            if (outcome.NeedsUserConfirmation)
            {
                var reasons = string.Join("\n", outcome.ConfirmationReasons.Take(6));

                var answer = MessageBox.Show(
                    Loc.T("Deploy.ConfirmBody", game.Name, reasons),
                    Loc.T("Deploy.ConfirmTitle"), MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No);

                if (answer != MessageBoxResult.Yes)
                {
                    _log.Write(Loc.T("Deploy.ConfirmDeclined", game.Name));
                    return;
                }

                outcome = await RunConfigurationAsync(game, provider, protection.HasKernelAntiCheat, confirmed: true);
                if (outcome is null) return;
            }

            _log.Details(outcome.Details);

            // The plan is shown even when the run did not succeed: it is what the user was told would happen,
            // and seeing it next to the outcome is how a mismatch becomes visible instead of silent.
            if (outcome.Plan is { } plan)
            {
                _log.Write($"计划：{plan.Mode} · 入口 {plan.ProxyChoice ?? "(未指定)"} · " +
                           $"待部署 {plan.FilesToDeploy.Count} 个文件 · 状态 {plan.Status}" +
                           (plan.Blockers.Count > 0 ? $" · 阻止原因 {plan.Blockers.Count} 条" : ""));
            }

            _log.Result(outcome.Succeeded, outcome.Summary);

            LibraryStore.Save(_data);
            await FinishGameActionAsync(game);
        }
        catch (Exception ex)
        {
            _log.Write("✗ " + ex.Message);
            AppPaths.Log("部署失败: " + ex);
        }
        finally
        {
            _busy = false;
            BatchStatusText.Text = "";
        }
    }

    /// <summary>
    /// Re-reads the game from disk and repaints its row. Without the re-check the list would keep
    /// showing the status from before the operation (Loc.T("Status.Missing") after a successful
    /// restore, etc.). The evaluation hashes and trust-verifies the deployed files, so it runs off
    /// the UI thread like every other status read; awaiting it keeps the busy flag held until the
    /// fresh state is applied, so a later evaluation cannot overwrite a newer one.
    /// </summary>
    private async Task FinishGameActionAsync(GameEntry game)
    {
        var check = await Task.Run(() => DeploymentService.Evaluate(game));
        DeploymentService.Apply(game, check);
        LibraryStore.Save(_data);
        UpdateStatusCard();
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        var game = Selected;
        if (game is null) return;

        if (game.Deployment is null)
        {
            var body = Loc.T("Restore.NoRecord", game.Name);
            if (MessageBox.Show(body, Loc.T("Restore.NoRecordTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;
        }

        RunRestore(game);
    }

    private async void RunRestore(GameEntry game)
    {
        if (_busy) { _log.Write(Loc.T("Scan.Busy")); return; }

        _busy = true;
        BatchStatusText.Text = Loc.T("Restore.Starting", game.Name);

        try
        {
            var removeLogs = RemoveLogsCheck.IsChecked == true;
            var (restoreProvider, restoreError) = ProviderForRestore(game);

            if (restoreProvider is null)
            {
                _log.Write("✗ " + restoreError);
                return;
            }

            var result = await Task.Run(() => restoreProvider.Restore(game, removeLogs));

            _log.Details(result.Lines);
            _log.Result(result.Ok, result.Message);
            await FinishGameActionAsync(game);
        }
        catch (Exception ex)
        {
            _log.Write("✗ " + ex.Message);
            AppPaths.Log("恢复失败: " + ex);
        }
        finally
        {
            _busy = false;
            BatchStatusText.Text = "";
        }
    }

    private async void Adopt_Click(object sender, RoutedEventArgs e)
    {
        var game = Selected;
        if (game is null) return;

        var body = Loc.T("Adopt.Confirm");
        if (MessageBox.Show(body, Loc.T("Adopt.ConfirmTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
            return;

        if (_busy) { _log.Write(Loc.T("Scan.Busy")); return; }

        _busy = true;
        BatchStatusText.Text = Loc.T("Adopt.Starting", game.Name);

        try
        {
            // Adoption trust-verifies every candidate DLL in the game folder, so it belongs off the
            // UI thread like deploy.
            var result = await Task.Run(() => DeploymentService.Adopt(game));
            _log.Details(result.Lines);
            _log.Result(result.Ok, result.Message);
            await FinishGameActionAsync(game);
        }
        catch (Exception ex)
        {
            _log.Write("✗ " + Loc.T("Adopt.Failed", ex.Message));
            AppPaths.Log("接管失败: " + ex);
        }
        finally
        {
            _busy = false;
            BatchStatusText.Text = "";
        }
    }

    // ---- batch --------------------------------------------------------------

    private async void DeployAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { _log.Write(Loc.T("Scan.Busy")); return; }

        var targets = _data.Games
            .Where(g => !string.IsNullOrWhiteSpace(g.RenderDir) && Directory.Exists(g.RenderDir))
            .ToList();

        if (targets.Count == 0) { _log.Write(Loc.T("Batch.NothingToDeploy")); return; }

        var protectedGames = targets.Where(g => g.HasKernelAntiCheat).ToList();

        // Protected games stay in the list: the user asked for a batch, and the scan cannot know whether
        // this game's protection lets the chosen entry name survive. They are flagged as a risk and
        // deployed on the strength of this one confirmation.
        // ── Preview every game first. The confirmation below is then about what will actually happen, built from
        //    the plans rather than from the game list: a blocked plan should be visible *before* the user agrees,
        //    not reported as a failure afterwards. This is the batch's version of "show the plan first".
        var provider = SelectedProvider();
        var previews = new List<(GameEntry Game, ConfigurationOutcome Outcome)>();

        foreach (var game in targets)
        {
            try
            {
                previews.Add((game, await Configuration.PreviewAsync(
                    new ConfigurationRequest(
                        Game: game, Provider: provider,
                        ProviderVersion: provider.GetInstalledVersion(game) ?? game.Deployment?.ModVersion ?? "",
                        // P0-07：与单游戏路径一致 —— 预览也必须按 provider/版本隔离取目录，否则预览看到的文件
                        // 和真正执行时用的文件来自两个不同的地方，而用户是同意的「后者」。
                        PayloadDirectory: null,
                        GpuName: _data.GpuName, DriverVersion: _data.GpuDriver, Store: game.Store,
                        HasKernelAntiCheat: game.HasKernelAntiCheat,
                        UserConfirmedUnverified: true,
                        // §9/§10：与单游戏路径一致 —— 预览也必须按 Provider 决定要写哪些设置，
                        // 否则预览里显示的 Profile 需求与真正执行时的不是同一件事。
                        ProfileSettings: provider.RequiresSmoothMotionDrs
                            ? new[] { SmoothMotionSettings.Feature, SmoothMotionSettings.Apis }
                            : Array.Empty<NvidiaProfile.ProfileSetting>()))
                    .ConfigureAwait(true)));
            }
            catch (Exception ex)
            {
                _log.Write($"  ⚠ {game.Name}：预览失败 —— {ex.Message}");
            }
        }

        var blockedPlans = previews
            .Where(p => p.Outcome.Plan is { } pl && pl.Blockers.Count > 0)
            .Select(p => $"· {p.Game.Name} — {string.Join("；", p.Outcome.Plan!.Blockers)}")
            .ToList();

        var needConfirm = previews.Where(p => p.Outcome.NeedsUserConfirmation).ToList();

        var planSummary = new System.Text.StringBuilder();

        if (blockedPlans.Count > 0)
            planSummary.Append(Loc.T("Batch.PlanBlocked", blockedPlans.Count)).Append('\n')
                       .Append(string.Join("\n", blockedPlans)).Append('\n');

        if (needConfirm.Count > 0)
            planSummary.Append(Loc.T("Batch.PlanNeedsConfirm", needConfirm.Count)).Append('\n')
                       .Append(string.Join("\n", needConfirm.Select(p => "· " + p.Game.Name))).Append('\n');

        var body = protectedGames.Count == 0
            ? Loc.T("Batch.DeployConfirm", targets.Count,
                string.Join("\n", targets.Select(t => "· " + t.Name)))
            : Loc.T("Batch.DeployConfirmWithRisk",
                targets.Count,
                string.Join("\n", targets.Select(t => "· " + t.Name)),
                protectedGames.Count,
                string.Join("\n", protectedGames.Select(t => $"· {t.Name} — {t.Protection!.Products}")));

        // The plans are appended to the risk text rather than replacing it: the anti-cheat warning is about the
        // games, and the plan summary is about this run. Both are things the user is agreeing to.
        if (planSummary.Length > 0)
            body += "\n\n" + Loc.T("Batch.PlanHeader") + "\n" + planSummary;

        if (MessageBox.Show(body, Loc.T("Batch.DeployTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        _log.Write(Loc.T("Batch.DeployStart", targets.Count));

        _busy = true;
        var progress = UiProgress();

        try
        {
            var ok = 0;

            foreach (var game in targets)
            {
                // Routed through the configuration service rather than the provider's Install: that is what makes a
                // batch obey the same plans, verification and confirmation rules as a single deploy. The direct call
                // it replaced bypassed every one of them.
                var outcome = await RunConfigurationAsync(game, provider, game.HasKernelAntiCheat, confirmed: true)
                    .ConfigureAwait(true);

                var succeeded = outcome?.Succeeded == true;
                var mark = succeeded ? "✓" : "✗";
                var risk = game.HasKernelAntiCheat ? Loc.T("Batch.RiskMark") : "";

                _log.Write($"  {mark}{risk} {game.Name}：{outcome?.Summary ?? Loc.T("Deploy.Failed")}");

                if (succeeded) ok++;
                progress.Report(Loc.T("Batch.Progress", ok, targets.Count));
            }

            _log.Write(Loc.T("Batch.Result", Loc.T("Batch.Deploy"), ok, targets.Count));
            BatchStatusText.Text = Loc.T("Batch.LastDeploy", ok, targets.Count);
            LibraryStore.Save(_data);
            RefreshAllStatus();
        }
        catch (Exception ex)
        {
            _log.Write("✗ " + ex.Message);
            AppPaths.Log("批量部署失败: " + ex);
        }
        finally
        {
            _busy = false;
        }
    }

    private async void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { _log.Write(Loc.T("Scan.Busy")); return; }

        var targets = _data.Games
            .Where(g => g.Deployment is not null && !string.IsNullOrWhiteSpace(g.RenderDir) && Directory.Exists(g.RenderDir))
            .ToList();

        if (targets.Count == 0) { _log.Write(Loc.T("Batch.NothingToRestore")); return; }

        var body = Loc.T("Batch.RestoreConfirm", targets.Count,
            string.Join("\n", targets.Select(t => "· " + t.Name)));
        if (MessageBox.Show(body, Loc.T("Batch.RestoreTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        _log.Write(Loc.T("Batch.RestoreStart", targets.Count));

        _busy = true;
        var removeLogs = RemoveLogsCheck.IsChecked == true;
        var progress = UiProgress();

        try
        {
            var ok = await Task.Run(() =>
            {
                var count = 0;

                foreach (var game in targets)
                {
                    var (restoreProvider, restoreError) = ProviderForRestore(game);

                    if (restoreProvider is null)
                    {
                        _log.Write($"  ✗ {game.Name}：{restoreError}");
                        continue;
                    }

                    var result = restoreProvider.Restore(game, removeLogs);
                    var mark = result.Ok ? "✓" : "✗";
                    _log.Write($"  {mark} {game.Name}：{result.Message}");
                    if (result.Ok) count++;
                    progress.Report(Loc.T("Batch.Progress", count, targets.Count));
                }

                return count;
            });

            _log.Write(Loc.T("Batch.Result", Loc.T("Batch.Restore"), ok, targets.Count));
            BatchStatusText.Text = Loc.T("Batch.LastRestore", ok, targets.Count);
            LibraryStore.Save(_data);
            RefreshAllStatus();
        }
        catch (Exception ex)
        {
            _log.Write("✗ " + ex.Message);
            AppPaths.Log("批量恢复失败: " + ex);
        }
        finally
        {
            _busy = false;
        }
    }

    // ---- scanning -----------------------------------------------------------

    private void ScanSteam_Click(object sender, RoutedEventArgs e)
    {
        var progress = UiProgress();
        StartScan(Loc.T("Scan.SteamLabel"), token => Detection.ScanSteam(progress, token));
    }

    private void ScanFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Scan.FolderTitle") };
        if (!string.IsNullOrWhiteSpace(_data.LastScanRoot) && Directory.Exists(_data.LastScanRoot))
            dialog.InitialDirectory = _data.LastScanRoot;

        if (dialog.ShowDialog() != true) return;

        var root = dialog.FolderName;
        _data.LastScanRoot = root;
        LibraryStore.Save(_data);

        var progress = UiProgress();
        StartScan(root, token => Detection.ScanFolder(root, progress, token));
    }

    /// <summary>
    /// Progress sink for the scan workers. Must be created on the UI thread: Progress&lt;T&gt; captures
    /// the SynchronizationContext it is constructed on, so building it inside a background lambda
    /// would run the callback on a pool thread and touching a control there kills the process.
    /// The dispatcher check keeps the sink correct even if that ever changes.
    /// </summary>
    private IProgress<string> UiProgress() => new Progress<string>(text =>
    {
        if (Dispatcher.CheckAccess()) BatchStatusText.Text = text;
        else Dispatcher.Invoke(() => BatchStatusText.Text = text);
    });

    private async void StartScan(string label, Func<CancellationToken, List<GameCandidate>> scan)
    {
        if (_busy) { _log.Write(Loc.T("Scan.Busy")); return; }

        _busy = true;
        _scanCts = new CancellationTokenSource();
        _log.Write(Loc.T("Scan.Start", label));

        var token = _scanCts.Token;
        List<GameCandidate>? found = null;

        try
        {
            found = await Task.Run(() =>
            {
                try { return scan(token); }
                catch (OperationCanceledException) { return null; }
                catch (Exception ex)
                {
                    AppPaths.Log("扫描失败: " + ex);
                    return null;
                }
            });
        }
        finally
        {
            _busy = false;
            _scanCts?.Dispose();
            _scanCts = null;
            BatchStatusText.Text = "";
        }

        if (found is null) { _log.Write(Loc.T("Scan.Cancelled")); return; }
        MergeCandidates(found);
    }

    /// <summary>
    /// Folds scan results into the library and refreshes the affected rows. The per-game evaluation
    /// (hashing, trust verification) runs off the UI thread; the anti-cheat summary prompt waits for
    /// those results because it reads the protection they carry.
    /// </summary>
    private async void MergeCandidates(List<GameCandidate> found)
    {
        var added = 0;
        var touched = new List<GameEntry>();
        var newlyAdded = new List<GameEntry>();

        foreach (var candidate in found)
        {
            var existing = _data.Games.FirstOrDefault(g =>
                string.Equals(g.RenderDir.TrimEnd('\\'), candidate.RenderDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                if (!string.IsNullOrWhiteSpace(candidate.ExePath)) existing.ExePath = candidate.ExePath;
                touched.Add(existing);
                continue;
            }

            var game = new GameEntry
            {
                Name = candidate.Name,
                RenderDir = candidate.RenderDir,
                ExePath = candidate.ExePath,
                PreferredProxy = DeploymentService.AutoProxy,
                Notes = candidate.Source ?? "",

                // The scanner knows where it found this. A Steam discovery and a hand-added folder must not end up
                // claiming the same provenance, because the store reaches the compatibility query: a result that
                // says "works on Steam" is not evidence about a manual install.
                Store = (candidate.Source ?? "").Contains("Steam", StringComparison.OrdinalIgnoreCase)
                    ? StoreKind.Steam
                    : StoreKind.Manual,
                Profile = new GameProfile { Router = _data.RecommendedRouter },
            };

            _data.Games.Add(game);
            touched.Add(game);
            newlyAdded.Add(game);
            added++;
        }

        _log.Write(Loc.T("Scan.Done", found.Count, added));

        // Land on something useful instead of leaving the detail pane empty after a scan.
        if (GameList.SelectedItem is null && _data.Games.Count > 0)
            GameList.SelectedIndex = 0;

        var games = touched;
        try
        {
            var results = await Task.Run(() =>
                games.Select(g => (Game: g, Check: DeploymentService.Evaluate(g))).ToList());

            foreach (var (game, check) in results) DeploymentService.Apply(game, check);

            LibraryStore.Save(_data);
            UpdateStatusCard();
        }
        catch (Exception ex)
        {
            _log.Write(Loc.T("Status.RefreshFailed", ex.Message));
            AppPaths.Log("扫描后状态检查失败: " + ex);
        }

        // One summary prompt for the whole scan rather than a dialog per protected title.
        WarnAboutProtected(newlyAdded);
    }

    // ---- path pickers -------------------------------------------------------

    private void BrowseRenderDir_Click(object sender, RoutedEventArgs e)
    {
        var game = Selected;
        if (game is null) return;

        var dialog = new OpenFolderDialog { Title = Loc.T("Detail.BrowseDirTitle") };
        if (!string.IsNullOrWhiteSpace(game.RenderDir) && Directory.Exists(game.RenderDir))
            dialog.InitialDirectory = game.RenderDir;

        if (dialog.ShowDialog() != true) return;

        // AttachFolder resolves the render directory and raises the anti-cheat warning.
        AttachFolder(game, dialog.FolderName);
    }

    private void DetectRenderDir_Click(object sender, RoutedEventArgs e)
    {
        var game = Selected;
        if (game is null) return;

        var root = game.RenderDir;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            _log.Write(Loc.T("Detail.DetectNeedFolder"));
            return;
        }

        _log.Write(Loc.T("Detail.DetectSearching", root));
        var hit = Detection.FindRenderTarget(root);
        if (hit is null)
        {
            _log.Write(Loc.T("Detail.DetectNoMarker"));
            return;
        }

        _log.Write(Loc.T("Detail.LocatedMessage", hit.RenderDir));
        AttachFolder(game, root);
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var game = Selected;
        if (game is null) return;

        var dialog = new OpenFileDialog { Title = Loc.T("Detail.BrowseExeTitle"), Filter = Loc.T("Detail.ExeFilter") };
        if (!string.IsNullOrWhiteSpace(game.ExePath) && File.Exists(game.ExePath))
            dialog.InitialDirectory = Path.GetDirectoryName(game.ExePath);

        if (dialog.ShowDialog() != true) return;

        game.ExePath = dialog.FileName;
        LibraryStore.Save(_data);
    }

    // ---- open / launch ------------------------------------------------------

    private void OpenRenderDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = Selected?.RenderDir;
        if (!Shell.OpenFolder(dir)) _log.Write(Loc.T("Error.DirOpenFailed"));
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        var game = Selected;
        if (game is null) return;

        if (Shell.LaunchExecutable(game.ExePath))
            _log.Write(Loc.T("Error.Launched", Path.GetFileName(game.ExePath)));
        else
            _log.Write(Loc.T("Error.LaunchFailed"));
    }

    private void OpenModLog_Click(object sender, RoutedEventArgs e)
    {
        var game = Selected;
        if (game is null) return;

        var latest = DeploymentService.LatestLogFile(game.RenderDir);
        if (latest is not null)
        {
            Shell.OpenDocument(latest);
            _log.Write(Loc.T("Error.LogOpened", Path.GetFileName(latest)));
            return;
        }

        var logsDir = Path.Combine(game.RenderDir, ModSource.LogDirName, "logs");
        if (Directory.Exists(logsDir))
        {
            Shell.OpenFolder(logsDir);
            return;
        }

        _log.Write(Loc.T("Error.NoModLog"));
    }

    private void OpenDataDir_Click(object sender, RoutedEventArgs e)
    {
        AppPaths.EnsureCreated();
        if (!Shell.OpenFolder(AppPaths.Root)) _log.Write(Loc.T("Error.CannotOpenDataDir", AppPaths.Root));
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _log.Clear();

    private void AdminButton_Click(object sender, RoutedEventArgs e)
    {
        if (Native.IsElevated()) return;

        if (Shell.RelaunchElevated())
            Application.Current.Shutdown();
        else
            _log.Write(Loc.T("Error.ElevationCancelled"));
    }
}
