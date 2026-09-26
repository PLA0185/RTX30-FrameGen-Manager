namespace DLSSGManager.Providers;

/// <summary>
/// The provider registry the application itself uses.
///
/// Exists so the interface can reach a provider without naming a concrete class: the deployment and
/// restore actions ask for "the provider that owns this flow" rather than calling
/// <see cref="DeploymentService"/> directly, which is what keeps a future provider from having to be
/// threaded through the window by hand.
///
/// The construction is deliberately lazy-free and I/O-free, so nothing here can fail on a machine
/// that is offline.
/// </summary>
public static class AppProviders
{
    /// <summary>Every provider this build ships.</summary>
    public static ProviderRegistry Registry { get; } = ProviderRegistry.CreateDefault();

    /// <summary>
    /// The provider behind the built-in dlssg_for_sm86 flow.
    ///
    /// Throws rather than returning null when it is missing: that can only mean the build was
    /// misconfigured, and silently falling back to a raw service call would hide it.
    /// </summary>
    public static IPatchProvider Patch =>
        Registry.Get(DlssgSm86Provider.ProviderId)
        ?? throw new InvalidOperationException($"默认 Provider 未注册：{DlssgSm86Provider.ProviderId}");
}
