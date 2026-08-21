using System.Diagnostics;
using System.Windows.Automation;

namespace TopIsland.Services;

public sealed record DiscordVoiceSnapshot(
    bool IsRunning,
    bool IsConnected,
    string ChannelName,
    string ServerName,
    int ParticipantCount,
    string ParticipantSummary,
    bool IsMuted,
    bool IsDeafened)
{
    public static DiscordVoiceSnapshot Empty { get; } = new(false, false, string.Empty, string.Empty, 0, string.Empty, false, false);
}

public sealed class DiscordVoiceService
{
    private static readonly string[] DisconnectNames = ["Disconnect", "切断"];
    private static readonly string[] MuteNames = ["Mute", "Unmute", "ミュート", "ミュート解除"];
    private static readonly string[] DeafenNames = ["Deafen", "Undeafen", "スピーカーミュート", "スピーカーミュート解除"];

    private readonly object _gate = new();
    private DiscordVoiceSnapshot _snapshot = DiscordVoiceSnapshot.Empty;
    private bool _sampling;

    public DiscordVoiceSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public async Task RefreshAsync()
    {
        lock (_gate)
        {
            if (_sampling)
            {
                return;
            }
            _sampling = true;
        }

        try
        {
            var snapshot = await Task.Run(SampleCore);
            lock (_gate)
            {
                _snapshot = snapshot;
            }
        }
        catch
        {
            lock (_gate)
            {
                _snapshot = DiscordVoiceSnapshot.Empty;
            }
        }
        finally
        {
            lock (_gate)
            {
                _sampling = false;
            }
        }
    }

    private static DiscordVoiceSnapshot SampleCore()
    {
        var process = FindDiscordMainProcess();
        if (process is null)
        {
            return DiscordVoiceSnapshot.Empty;
        }

        var root = AutomationElement.FromHandle(process.MainWindowHandle);
        if (root is null)
        {
            return new DiscordVoiceSnapshot(true, false, string.Empty, string.Empty, 0, string.Empty, false, false);
        }

        var elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var connected = false;
        var muted = false;
        var deafened = false;
        var participants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            string name;
            try
            {
                name = element.Current.Name ?? string.Empty;
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (IsDisconnectName(name) || name.Contains("通話中", StringComparison.OrdinalIgnoreCase) || name.Contains("Connected", StringComparison.OrdinalIgnoreCase))
            {
                connected = true;
            }

            if (name.Equals("Unmute", StringComparison.OrdinalIgnoreCase) || name.Contains("ミュート解除", StringComparison.Ordinal))
            {
                if (!name.Contains("スピーカー", StringComparison.Ordinal))
                {
                    muted = true;
                }
            }

            if (name.Equals("Undeafen", StringComparison.OrdinalIgnoreCase) || name.Contains("スピーカーミュート解除", StringComparison.Ordinal))
            {
                deafened = true;
            }

            var participant = TryParseParticipant(name);
            if (!string.IsNullOrWhiteSpace(participant))
            {
                participants.Add(participant);
            }
        }

        var (channel, server) = ParseWindowTitle(process.MainWindowTitle);
        if (!connected)
        {
            return new DiscordVoiceSnapshot(true, false, string.Empty, server, 0, string.Empty, muted, deafened);
        }

        var participantList = participants.Take(3).ToArray();
        var summary = participantList.Length == 0
            ? string.Empty
            : string.Join(", ", participantList) + (participants.Count > participantList.Length ? $" +{participants.Count - participantList.Length}" : string.Empty);

        return new DiscordVoiceSnapshot(
            true,
            true,
            channel,
            server,
            participants.Count,
            summary,
            muted,
            deafened);
    }

    private static Process? FindDiscordMainProcess() =>
        Process.GetProcessesByName("Discord")
            .Where(process => process.MainWindowHandle != IntPtr.Zero)
            .OrderByDescending(process => process.MainWindowTitle.Length)
            .FirstOrDefault();

    private static bool IsDisconnectName(string name) =>
        DisconnectNames.Any(candidate => name.Equals(candidate, StringComparison.OrdinalIgnoreCase));

    private static string? TryParseParticipant(string name)
    {
        var prefixes = new[] { "通話タイル、", "通話タイル,", "Call tile,", "Call tile、" };
        foreach (var prefix in prefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return name[prefix.Length..].Trim();
            }
        }
        return null;
    }

    private static (string Channel, string Server) ParseWindowTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return (string.Empty, string.Empty);
        }

        var clean = title.EndsWith(" - Discord", StringComparison.OrdinalIgnoreCase)
            ? title[..^" - Discord".Length]
            : title;
        var parts = clean.Split(" | ", 2, StringSplitOptions.TrimEntries);
        var channel = parts.Length > 0 ? CleanChannel(parts[0]) : string.Empty;
        var server = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        return (channel, server);
    }

    private static string CleanChannel(string value)
    {
        var clean = value.Trim();
        if (clean.StartsWith("🎤", StringComparison.Ordinal))
        {
            clean = clean["🎤".Length..];
        }
        return clean.TrimStart('｜', '|', ' ', '·').Trim();
    }
}
