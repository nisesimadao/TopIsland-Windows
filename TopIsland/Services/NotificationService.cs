using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace TopIsland.Services;

public sealed record NotificationSnapshot(bool AccessAllowed, int Count, string AppName, string Text)
{
    public bool HasNotifications => AccessAllowed && Count > 0;
}

public sealed class NotificationService
{
    private readonly UserNotificationListener _listener = UserNotificationListener.Current;

    public bool IsAccessAllowed()
    {
        try
        {
            return _listener.GetAccessStatus() == UserNotificationListenerAccessStatus.Allowed;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> RequestAccessAsync()
    {
        try
        {
            return await _listener.RequestAccessAsync() == UserNotificationListenerAccessStatus.Allowed;
        }
        catch
        {
            return false;
        }
    }

    public async Task<NotificationSnapshot> SampleAsync()
    {
        if (!IsAccessAllowed())
        {
            return new NotificationSnapshot(false, 0, string.Empty, string.Empty);
        }

        try
        {
            var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var latest = notifications
                .OrderByDescending(notification => notification.CreationTime)
                .FirstOrDefault();
            if (latest is null)
            {
                return new NotificationSnapshot(true, 0, string.Empty, string.Empty);
            }

            var appName = latest.AppInfo?.DisplayInfo?.DisplayName ?? "Notification";
            var binding = latest.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            var text = binding?.GetTextElements()
                .Select(element => element.Text)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

            return new NotificationSnapshot(true, notifications.Count, appName, text);
        }
        catch
        {
            return new NotificationSnapshot(false, 0, string.Empty, string.Empty);
        }
    }
}
