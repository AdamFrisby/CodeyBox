namespace CodeyBox.PluginSdk;

/// <summary>
/// Optional interface for plugins that need async initialization before they
/// serve their first request. Implement this alongside the Core interface(s)
/// your plugin contributes (e.g. <c>IAuditor</c>).
///
/// <para>The host calls <see cref="InitializeAsync"/> once per plugin type
/// during startup, after DI resolution and before any work items are processed.
/// If this method throws, the host logs the error and re-throws from
/// <c>IHostedService.StartAsync</c>, which causes the .NET Generic Host to abort
/// the process. Catch exceptions inside <see cref="InitializeAsync"/> and handle
/// them gracefully if you want the host to continue despite a plugin failure.</para>
///
/// <para>Hot-reload is not supported in v1; plugin changes require a host restart.</para>
/// </summary>
public interface IPluginInitializer
{
    Task InitializeAsync(PluginContext context, CancellationToken ct = default);
}
