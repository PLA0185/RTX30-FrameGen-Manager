using System.IO;
using System.Windows;

namespace DLSSGManager;

/// <summary>
/// Anti-cheat warnings raised at the moment a folder is attached to a game, rather than only when a
/// deploy is attempted. A user adding a protected game should learn immediately that the mod cannot
/// work there, not after they have copied files into it.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Points a game at a folder, resolving the folder down to the real render directory when needed,
    /// then reports whether anti-cheat will block the mod there.
    ///
    /// Resolution matters for the scan: anti-cheat files sit beside the rendering executable, so
    /// pointing at a game root (Overwatch's "E:\Overwatch" instead of its "_retail_" folder) would
    /// otherwise walk up past them and miss the driver entirely.
    /// </summary>
    private void AttachFolder(GameEntry game, string folder)
    {
        var (renderDir, exe) = Detection.ResolveRenderDir(folder);

        if (!string.Equals(Path.GetFullPath(renderDir), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
            _log.Write(Loc.T("Detail.LocatedNotRenderDir", renderDir));

        game.RenderDir = renderDir;
        if (exe is not null) game.ExePath = exe;

        // **只判「名字是不是空的」，不判「它是不是那句本地化文案」。**（Pass F 报出。）
        //
        // 这里曾经是 `game.Name == Loc.T("List.NewGame")`，而全仓 `List.NewGame` **只有这一处比较
        // 加两个语言表，没有任何写入点** ⇒ **恒假**（`AddGame_Click` 用的是 `Detection.FriendlyName`）。
        // 更糟的是拿**本地化后的值**做比较：用户若真把游戏命名成「新游戏」，切到英文后这个比较就失效了
        //（而中文下会命中）。
        // **两个问题都是同一个根源：把「给人看的文案」当成「数据里的值」。**
        // 与 `ModSource.Version` / `Adopt.ModVersion` / `Adopt.DeployedAt` 是同一条判据 —— 这是本会话
        // 第四次修它，所以这里写清：**本地化文案永远不参与比较，只参与显示。**
        if (string.IsNullOrWhiteSpace(game.Name))
        {
            var friendly = Detection.FriendlyName(renderDir);
            if (!string.IsNullOrWhiteSpace(friendly)) game.Name = friendly;
        }

        _ = EvaluateAttachedFolderAsync(game);
    }

    /// <summary>
    /// The status evaluation hashes and trust-verifies the game folder's files, so it runs off the
    /// UI thread; the anti-cheat prompt reads the protection the evaluation carries and therefore
    /// waits for it.
    /// </summary>
    private async Task EvaluateAttachedFolderAsync(GameEntry game)
    {
        try
        {
            var check = await Task.Run(() => DeploymentService.Evaluate(game));
            DeploymentService.Apply(game, check);
        }
        catch (Exception ex)
        {
            AppPaths.Log("附加目录后状态检查失败: " + ex);
        }

        LibraryStore.Save(_data);
        UpdateStatusCard();
        WarnIfProtected(game);
    }

    /// <summary>
    /// Scans the game's folder and, when a kernel-mode anti-cheat is present, explains the consequence.
    /// Returns true when a warning was shown.
    /// </summary>
    private bool WarnIfProtected(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.RenderDir) || !Directory.Exists(game.RenderDir)) return false;

        var protection = AntiCheat.Scan(game.RenderDir);
        game.Protection = protection;
        if (!protection.HasKernelAntiCheat) return false;

        var name = string.IsNullOrWhiteSpace(game.Name) ? Detection.FriendlyName(game.RenderDir) : game.Name;
        _log.Write(Loc.T("Anti.ProtectedLog", name, protection.Summary, protection.Evidence));

        MessageBox.Show(this,
            AntiCheat.BuildUnsupportedNotice(name, protection),
            Loc.T("Anti.BlockTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        return true;
    }

    /// <summary>
    /// One summary prompt for a batch of newly added games, so scanning a whole drive does not produce
    /// a popup per protected title.
    /// </summary>
    private void WarnAboutProtected(List<GameEntry> newlyAdded)
    {
        var blocked = newlyAdded.Where(g => g.HasKernelAntiCheat).ToList();
        if (blocked.Count == 0) return;

        var lines = string.Join("\n", blocked.Select(g => $"· {g.Name} — {g.Protection!.Products}"));
        var body = Loc.T("Anti.BatchBody", blocked.Count, lines);

        _log.Write(Loc.T("Anti.BatchLog", blocked.Count, Loc.Join(blocked.Select(g => g.Name))));

        MessageBox.Show(this, body, Loc.T("Anti.BatchTitle"),
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
