using DLSSGManager.GameDetection;
using DLSSGManager.InstallPlanning;

namespace DLSSGManager.Orchestration;

/// <summary>
/// The detection steps, wired to the real detectors.
///
/// <para>This type exists because <see cref="IWorkflowDetector"/> had <b>no production implementation</b>:
/// the workflow was fully built and fully tested, and nothing in the application ever constructed it. Every
/// real deployment went through the older provider path instead — which is exactly why the plan a user could
/// be shown and the install that actually happened were free to disagree.</para>
/// </summary>
public sealed class AppWorkflowDetector : IWorkflowDetector
{
    private readonly Func<string, bool>? _isKnownCompatibleMod;

    /// <param name="isKnownCompatibleMod">
    /// Optional recogniser for other mods. Left as a hook rather than a guess: without it an unidentified DLL
    /// stays unidentified, which is the correct answer when nothing can recognise it.
    /// </param>
    public AppWorkflowDetector(Func<string, bool>? isKnownCompatibleMod = null) =>
        _isKnownCompatibleMod = isKnownCompatibleMod;

    public RendererDetection DetectRenderer(GameEntry game, string? userChoice) =>
        RendererDetector.Detect(game.RenderDir ?? "", userChoice: userChoice);

    public GraphicsApiDetection DetectApi(GameEntry game) =>
        GraphicsApiDetector.Detect(game.RenderDir ?? "");

    public ProxyConflictReport ScanProxyConflicts(GameEntry game) =>
        ProxyConflictScanner.Scan(game, _isKnownCompatibleMod);
}
