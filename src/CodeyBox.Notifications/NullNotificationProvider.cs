using CodeyBox.Core;

namespace CodeyBox.Notifications;

/// <summary>
/// No-op provider used when a provider is referenced in rules but not
/// configured (or when notifications are disabled).
/// </summary>
public sealed class NullNotificationProvider : INotificationProvider
{
    private readonly string _name;

    public string Name => _name;

    public NullNotificationProvider(string name = "null")
    {
        _name = name;
    }

    public Task SendAsync(Notification notification, CancellationToken ct) => Task.CompletedTask;
}
