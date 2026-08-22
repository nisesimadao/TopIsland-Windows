using System.Text.RegularExpressions;

namespace TopIsland.Services;

internal static class DiscordVoiceParser
{
    private static readonly string[] VoiceMarkers = ["(ボイスチャンネル)", "(Voice Channel)"];
    private static readonly string[] CallDurationMarkers = ["通話時間", "Call Duration", "Call duration"];
    private static readonly Regex JapaneseExtraParticipants = new(@"(?:と他|ほか)(?<n>\d+)人", RegexOptions.Compiled);
    private static readonly Regex EnglishExtraParticipants = new(@"(?:and\s+)?(?<n>\d+)\s+others?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static bool TryParseActiveVoiceElement(
        string name,
        out string channel,
        out List<string> participants,
        out int extraParticipants)
    {
        channel = string.Empty;
        participants = [];
        extraParticipants = 0;

        var marker = VoiceMarkers
            .Select(candidate => (Value: candidate, Index: name.IndexOf(candidate, StringComparison.OrdinalIgnoreCase)))
            .Where(item => item.Index > 0)
            .OrderBy(item => item.Index)
            .FirstOrDefault();
        if (marker.Value is null || !CallDurationMarkers.Any(candidate => name.Contains(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        channel = CleanChannel(name[..marker.Index]);
        var remainder = name[(marker.Index + marker.Value.Length)..].TrimStart(' ', ',');
        foreach (var part in remainder.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (CallDurationMarkers.Any(candidate => part.Contains(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                break;
            }

            if (TryParseExtraParticipants(part, out var extraCount))
            {
                extraParticipants += extraCount;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(part))
            {
                participants.Add(part.Trim());
            }
        }

        return channel.Length > 0;
    }

    internal static string? TryParseParticipant(string name)
    {
        string[] prefixes = ["通話タイル、", "通話タイル,", "Call tile,", "Call tile、"];
        foreach (var prefix in prefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return name[prefix.Length..].Trim();
            }
        }

        return null;
    }

    internal static (string Channel, string Server) ParseWindowTitle(string title)
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

    internal static string CleanChannel(string value)
    {
        var clean = value.Trim();
        if (clean.StartsWith("🎤", StringComparison.Ordinal))
        {
            clean = clean["🎤".Length..];
        }

        return clean.TrimStart('｜', '|', ' ', '·').Trim();
    }

    private static bool TryParseExtraParticipants(string value, out int count)
    {
        foreach (var regex in new[] { JapaneseExtraParticipants, EnglishExtraParticipants })
        {
            var match = regex.Match(value);
            if (match.Success && int.TryParse(match.Groups["n"].Value, out count))
            {
                return true;
            }
        }

        count = 0;
        return false;
    }
}
