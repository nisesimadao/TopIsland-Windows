using Windows.Storage.Streams;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace TopIsland.Services;

public sealed record NotificationItemSnapshot(string AppName, string Text, DateTimeOffset CreatedAt, byte[]? IconPng);
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
    private readonly Dictionary<string, byte[]?> _logoCache = new(StringComparer.Ordinal);

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

    public bool ClearAll()
    {
        if (!IsAccessAllowed()) return false;
        try
        {
            _listener.ClearNotifications();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<NotificationSnapshot> SampleAsync()
    {
        if (!IsAccessAllowed()) return new NotificationSnapshot(false, Array.Empty<NotificationItemSnapshot>());
        try
        {
            var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var items = new List<NotificationItemSnapshot>(3);
            foreach (var notification in notifications
                .OrderByDescending(notification => notification.CreationTime)
                .Take(3))
            {
                var appName = notification.AppInfo?.DisplayInfo?.DisplayName ?? "Notification";
                var binding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                var text = binding?.GetTextElements()
                    .Select(element => element.Text)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

                if (!_logoCache.TryGetValue(appName, out var iconPng))
                {
                    var logo = notification.AppInfo?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(24, 24));
                    iconPng = await ReadLogoAsync(logo);
                    if (_logoCache.Count >= 32)
                    {
                        _logoCache.Clear();
                    }
                    _logoCache[appName] = iconPng;
                }

                items.Add(new NotificationItemSnapshot(appName, text, notification.CreationTime, iconPng));
            }
            return new NotificationSnapshot(true, items);
        }
        catch
        {
            return new NotificationSnapshot(false, Array.Empty<NotificationItemSnapshot>());
        }
    }

    private static async Task<byte[]?> ReadLogoAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null)
        {
            return null;
        }

        try
        {
            using var stream = await reference.OpenReadAsync();
            var requested = (uint)Math.Min(stream.Size, 256_000UL);
            if (requested == 0)
            {
                return null;
            }

            using var reader = new DataReader(stream);
            var loaded = await reader.LoadAsync(requested);
            if (loaded == 0)
            {
                return null;
            }

            var bytes = new byte[loaded];
            reader.ReadBytes(bytes);
            reader.DetachStream();
            return bytes;
        }
        catch
        {
            return null;
        }
    }
}
