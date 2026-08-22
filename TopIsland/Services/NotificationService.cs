using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace TopIsland.Services;

public sealed record NotificationItemSnapshot(string AppName, string Text, DateTimeOffset CreatedAt);
public sealed record NotificationSnapshot(bool AccessAllowed, IReadOnlyList<NotificationItemSnapshot> Items)
{
    public int Count => Items.Count;
    public bool HasNotifications => AccessAllowed && Items.Count > 0;
    public string AppName => Items.FirstOrDefault()?.AppName ?? string.Empty;
    public string Text => Items.FirstOrDefault()?.Text ?? string.Empty;
}

public sealed class NotificationService
{
    private readonly UserNotificationListener _listener = UserNotificationListener.Current;

    public bool IsAccessAllowed()
    {
        try { return _listener.GetAccessStatus() == UserNotificationListenerAccessStatus.Allowed; }
        catch { return false; }
    }

    public async Task<bool> RequestAccessAsync()
    {
        try { return await _listener.RequestAccessAsync() == UserNotificationListenerAccessStatus.Allowed; }
        catch { return false; }
    }

    public async Task<NotificationSnapshot> SampleAsync()
    {
        if (!IsAccessAllowed()) return new NotificationSnapshot(false, Array.Empty<NotificationItemSnapshot>());
        try
        {
            var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var items = notifications
                .OrderByDescending(notification => notification.CreationTime)
                .Take(3)
                .Select(notification =>
                {
                    var appName = notification.AppInfo?.DisplayInfo?.DisplayName ?? "Notification";
                    var binding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                    var text = binding?.GetTextElements()
                        .Select(element => element.Text)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
                    return new NotificationItemSnapshot(appName, text, notification.CreationTime);
                })
                .ToArray();
            return new NotificationSnapshot(true, items);
        }
        catch { return new NotificationSnapshot(false, Array.Empty<NotificationItemSnapshot>()); }
    }
}
