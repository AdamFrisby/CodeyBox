namespace CodeyBox.Core;

/// <summary>
/// Source of <see cref="Project"/> configuration. Default impl reads from
/// <c>appsettings.json</c>; a future SQLite-backed CRUD impl can swap in
/// without touching the orchestrator.
/// </summary>
public interface IProjectRepository
{
    Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default);
    Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default);
    Task<ProjectListPage> ListPageAsync(int limit, CancellationToken ct = default);
}

public sealed record ProjectListPage(
    IReadOnlyList<Project> Projects,
    int TotalCount,
    bool HasMore);
