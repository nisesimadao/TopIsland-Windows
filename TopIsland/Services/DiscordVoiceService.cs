using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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
    bool IsDeafened,
    bool CanToggleMute,
    bool CanToggleDeafen,
    bool CanDisconnect)
{
    public static DiscordVoiceSnapshot Empty { get; } = new(false, false, string.Empty, string.Empty, 0, string.Empty, false, false, false, false, false);
}

public sealed class DiscordVoiceService
{
    private static readonly string[] DisconnectNames = ["Disconnect", "\u5207\u65AD"];
    private static readonly string[] MuteNames = ["Mute", "Unmute", "\u30DF\u30E5\u30FC\u30C8", "\u30DF\u30E5\u30FC\u30C8\u89E3\u9664"];
    private static readonly string[] DeafenNames = ["Deafen", "Undeafen", "\u30B9\u30D4\u30FC\u30AB\u30FC\u30DF\u30E5\u30FC\u30C8", "\u30B9\u30D4\u30FC\u30AB\u30FC\u30DF\u30E5\u30FC\u30C8\u89E3\u9664"];

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

    public async Task<bool> ToggleMuteAsync()
    {
        var result = await Task.Run(() => TryToggleControl(MuteNames));
        if (result)
        {
            // Give Discord's accessibility tree a moment to publish the new toggle state.
            // The caller performs the single refresh after this delay.
            await Task.Delay(180);
        }

        return result;
    }

    public async Task<bool> ToggleDeafenAsync()
    {
        var result = await Task.Run(() => TryToggleControl(DeafenNames));
        if (result)
        {
            await Task.Delay(180);
        }

        return result;
    }

    public async Task<bool> DisconnectAsync()
    {
        var result = await Task.Run(() => TryInvokeControl(DisconnectNames));
        if (result)
        {
            await Task.Delay(220);
        }

        return result;
    }

    private static DiscordVoiceSnapshot SampleCore()
    {
        var target = FindDiscordWindow();
        if (!target.IsRunning)
        {
            return DiscordVoiceSnapshot.Empty;
        }

        if (target.Handle == IntPtr.Zero)
        {
            return new DiscordVoiceSnapshot(true, false, string.Empty, string.Empty, 0, string.Empty, false, false, false, false, false);
        }

        var root = AutomationElement.FromHandle(target.Handle);
        if (root is null)
        {
            return new DiscordVoiceSnapshot(true, false, string.Empty, string.Empty, 0, string.Empty, false, false, false, false, false);
        }

        var elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var connected = false;
        var muted = false;
        var deafened = false;
        var canMute = false;
        var canDeafen = false;
        var canDisconnect = false;
        var activeVoiceChannel = string.Empty;
        var extraParticipantCount = 0;
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

            if (IsDisconnectName(name))
            {
                connected = true;
                try
                {
                    _ = element.GetCurrentPattern(InvokePattern.Pattern);
                    canDisconnect = true;
                }
                catch
                {
                }
            }

            if (name.Contains("\u901A\u8A71\u4E2D", StringComparison.Ordinal)
                || name.Contains("Connected", StringComparison.OrdinalIgnoreCase)
                || name.Contains("In call", StringComparison.OrdinalIgnoreCase))
            {
                connected = true;
            }

            if (MuteNames.Any(candidate => name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var pattern = (TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern);
                    canMute = true;
                    muted = pattern.Current.ToggleState == ToggleState.On;
                }
                catch
                {
                }
            }

            if (DeafenNames.Any(candidate => name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var pattern = (TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern);
                    canDeafen = true;
                    deafened = pattern.Current.ToggleState == ToggleState.On;
                }
                catch
                {
                }
            }

            if (activeVoiceChannel.Length == 0
                && DiscordVoiceParser.TryParseActiveVoiceElement(name, out var voiceChannel, out var voiceParticipants, out var extraParticipants))
            {
                connected = true;
                activeVoiceChannel = voiceChannel;
                extraParticipantCount = extraParticipants;
                foreach (var voiceParticipant in voiceParticipants)
                {
                    participants.Add(voiceParticipant);
                }
            }

            var participant = DiscordVoiceParser.TryParseParticipant(name);
            if (!string.IsNullOrWhiteSpace(participant))
            {
                participants.Add(participant);
            }
        }

        var (channel, server) = DiscordVoiceParser.ParseWindowTitle(target.Title);
        if (!string.IsNullOrWhiteSpace(activeVoiceChannel))
        {
            channel = activeVoiceChannel;
        }

        if (!connected)
        {
            return new DiscordVoiceSnapshot(true, false, string.Empty, server, 0, string.Empty, muted, deafened, canMute, canDeafen, canDisconnect);
        }

        var participantList = participants.Take(3).ToArray();
        var participantCount = participants.Count + extraParticipantCount;
        var hiddenParticipantCount = Math.Max(0, participantCount - participantList.Length);
        var summary = participantList.Length == 0
            ? string.Empty
            : string.Join(", ", participantList) + (hiddenParticipantCount > 0 ? $" +{hiddenParticipantCount}" : string.Empty);

        return new DiscordVoiceSnapshot(
            true,
            true,
            channel,
            server,
            participantCount,
            summary,
            muted,
            deafened,
            canMute,
            canDeafen,
            canDisconnect);
    }

    private static bool TryToggleControl(IEnumerable<string> names)
    {
        var target = FindDiscordWindow();
        if (target.Handle == IntPtr.Zero)
        {
            return false;
        }

        var root = AutomationElement.FromHandle(target.Handle);
        if (root is null)
        {
            return false;
        }

        var elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
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

            if (!names.Any(candidate => name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try
            {
                var pattern = (TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern);
                pattern.Toggle();
                return true;
            }
            catch
            {
            }
        }

        return false;
    }

    private static bool TryInvokeControl(IEnumerable<string> names)
    {
        var target = FindDiscordWindow();
        if (target.Handle == IntPtr.Zero)
        {
            return false;
        }

        var root = AutomationElement.FromHandle(target.Handle);
        if (root is null)
        {
            return false;
        }

        var elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
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

            if (!names.Any(candidate => name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try
            {
                var pattern = (InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern);
                pattern.Invoke();
                return true;
            }
            catch
            {
            }
        }

        return false;
    }

    private readonly record struct DiscordWindowTarget(bool IsRunning, IntPtr Handle, string Title);

    private static DiscordWindowTarget FindDiscordWindow()
    {
        var processes = Process.GetProcessesByName("Discord");
        if (processes.Length == 0)
        {
            return new DiscordWindowTarget(false, IntPtr.Zero, string.Empty);
        }

        try
        {
            var processIds = processes.Select(process => (uint)process.Id).ToHashSet();
            var bestHandle = IntPtr.Zero;
            var bestTitle = string.Empty;
            var bestScore = int.MinValue;

            EnumWindows((hwnd, lParam) =>
            {
                GetWindowThreadProcessId(hwnd, out var processId);
                if (!processIds.Contains(processId))
                {
                    return true;
                }

                var className = ReadWindowClass(hwnd);
                if (!string.Equals(className, "Chrome_WidgetWin_1", StringComparison.Ordinal))
                {
                    return true;
                }

                var title = ReadWindowTitle(hwnd);
                var score = title.EndsWith(" - Discord", StringComparison.OrdinalIgnoreCase) ? 100_000 : 0;
                score += title.Length;
                if (score <= bestScore)
                {
                    return true;
                }

                bestScore = score;
                bestHandle = hwnd;
                bestTitle = title;
                return true;
            }, IntPtr.Zero);

            return new DiscordWindowTarget(true, bestHandle, bestTitle);
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static string ReadWindowClass(IntPtr hwnd)
    {
        var buffer = new StringBuilder(128);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    private static string ReadWindowTitle(IntPtr hwnd)
    {
        var length = Math.Clamp(GetWindowTextLength(hwnd) + 1, 2, 1024);
        var buffer = new StringBuilder(length);
        return GetWindowText(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    private static bool IsDisconnectName(string name) =>
        DisconnectNames.Any(candidate => name.Equals(candidate, StringComparison.OrdinalIgnoreCase));

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

}
