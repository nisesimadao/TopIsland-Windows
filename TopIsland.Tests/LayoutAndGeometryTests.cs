using System.Windows;
using TopIsland.Controls;
using TopIsland.Models;
using TopIsland.Services;

namespace TopIsland.Tests;

public sealed class LayoutAndGeometryTests
{
    [Fact]
    public void CompactNotchIdleUsesExpectedShell()
    {
        var settings = new AppSettings { Style = IslandStyle.Notch, WidthPreset = WidthPreset.Compact };
        var layout = IslandLayoutCalculator.Resolve(settings, SurfaceState.Idle, 1706.6667);
        Assert.Equal(352, layout.Width, 3);
        Assert.Equal(56, layout.Height, 3);
        Assert.Equal(0, layout.Top, 3);
    }

    [Fact]
    public void CompactNotchPeekGrowthIsSubtle()
    {
        var settings = new AppSettings { Style = IslandStyle.Notch, WidthPreset = WidthPreset.Compact };
        var layout = IslandLayoutCalculator.Resolve(settings, SurfaceState.Peek, 1706.6667);
        Assert.Equal(409.6, layout.Width, 3);
        Assert.Equal(68, layout.Height, 3);
    }

    [Fact]
    public void ExpandedCompactUsesReadableMinimumWidth()
    {
        var settings = new AppSettings { Style = IslandStyle.Notch, WidthPreset = WidthPreset.Compact };
        var layout = IslandLayoutCalculator.Resolve(settings, SurfaceState.Expanded, 1706.6667);
        Assert.Equal(1072, layout.Width, 3);
        Assert.Equal(350, layout.Height, 3);
    }

    [Fact]
    public void FullWidthPreservesSurfaceSideMargins()
    {
        const double screenWidth = 1706.6667;
        var settings = new AppSettings
        {
            Style = IslandStyle.Notch,
            WidthPreset = WidthPreset.FullWidth,
            SideMargin = 20
        };
        var layout = IslandLayoutCalculator.Resolve(settings, SurfaceState.Expanded, screenWidth);
        var surfaceWidth = layout.Width - IslandGeometryFactory.ShadowPadding * 2;
        Assert.Equal(screenWidth - 40, surfaceWidth, 3);
    }

    [Fact]
    public void DynamicHoverUsesTenDipTopOffset()
    {
        var settings = new AppSettings { Style = IslandStyle.DynamicIsland, WidthPreset = WidthPreset.Standard };
        var layout = IslandLayoutCalculator.Resolve(settings, SurfaceState.Hover, 1706.6667);
        Assert.Equal(648, layout.Width, 3);
        Assert.Equal(80, layout.Height, 3);
        Assert.Equal(10, layout.Top, 3);
    }

    [Theory]
    [InlineData(100, 2560)]
    [InlineData(125, 2048)]
    [InlineData(150, 1706.6666666667)]
    public void MonitorDescriptorConvertsPixelsToDip(int scalePercent, double expectedDipWidth)
    {
        var monitor = new MonitorDescriptor(IntPtr.Zero, "DISPLAY", 0, 0, 2560, 1440, scalePercent, true);
        Assert.Equal(expectedDipWidth, monitor.DipWidth, 6);
    }

    [Theory]
    [InlineData(IslandStyle.DynamicIsland, 0.0)]
    [InlineData(IslandStyle.DynamicIsland, 1.0)]
    [InlineData(IslandStyle.Notch, 0.0)]
    [InlineData(IslandStyle.Notch, 0.5)]
    [InlineData(IslandStyle.Notch, 1.0)]
    public void SurfaceGeometryIsHorizontallySymmetric(IslandStyle style, double progress)
    {
        var size = new Size(720, 260);
        var geometry = IslandGeometryFactory.Create(style, size, progress);
        for (var y = 0; y <= 240; y += 8)
        {
            for (var x = 0; x <= 360; x += 8)
            {
                var left = geometry.FillContains(new Point(x, y));
                var right = geometry.FillContains(new Point(size.Width - x, y));
                Assert.Equal(left, right);
            }
        }
    }
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 1.0)]
    public void MotionEaseKeepsEndpoints(double input, double expected)
    {
        Assert.Equal(expected, MotionProfile.Ease(input), 8);
    }

    [Fact]
    public void MotionEaseIsResponsiveAndMonotonic()
    {
        var previous = MotionProfile.Ease(0);
        for (var i = 1; i <= 20; i++)
        {
            var current = MotionProfile.Ease(i / 20.0);
            Assert.True(current >= previous);
            previous = current;
        }

        Assert.True(MotionProfile.Ease(0.25) > 0.4);
        Assert.True(MotionProfile.Ease(0.5) > 0.75);
    }

    [Fact]
    public void ShortMotionReversalsUseShorterDuration()
    {
        var full = MotionProfile.ScaleDuration(200, 900, 320);
        var shortMove = MotionProfile.ScaleDuration(200, 20, 8);

        Assert.Equal(200, full);
        Assert.InRange(shortMove, 65, 150);
        Assert.True(shortMove < full);
    }

    [Fact]
    public void DiscordParserParsesJapaneseVoiceSummary()
    {
        const string label = "Lobby (ボイスチャンネル), Alpha, Bravo, と他3人, 通話時間12分";

        Assert.True(DiscordVoiceParser.TryParseActiveVoiceElement(label, out var channel, out var participants, out var extra));
        Assert.Equal("Lobby", channel);
        Assert.Equal(["Alpha", "Bravo"], participants);
        Assert.Equal(3, extra);
    }

    [Fact]
    public void DiscordParserParsesEnglishVoiceSummary()
    {
        const string label = "Gaming (Voice Channel), Alice, Bob, and 4 others, Call Duration 8 minutes";

        Assert.True(DiscordVoiceParser.TryParseActiveVoiceElement(label, out var channel, out var participants, out var extra));
        Assert.Equal("Gaming", channel);
        Assert.Equal(["Alice", "Bob"], participants);
        Assert.Equal(4, extra);
    }

    [Fact]
    public void DiscordParserCleansWindowTitleAndParticipantTile()
    {
        var (channel, server) = DiscordVoiceParser.ParseWindowTitle("🎤｜VC1 | Example Server - Discord");

        Assert.Equal("VC1", channel);
        Assert.Equal("Example Server", server);
        Assert.Equal("Alice", DiscordVoiceParser.TryParseParticipant("通話タイル、Alice"));
        Assert.Equal("Bob", DiscordVoiceParser.TryParseParticipant("Call tile,Bob"));
    }

    [Fact]
    public void DownloadActivityGraceRejectsStalePartialFiles()
    {
        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(DownloadMonitorService.HasRecentActivity(now.AddSeconds(-5), now));
        Assert.True(DownloadMonitorService.HasRecentActivity(now.AddSeconds(-12), now));
        Assert.False(DownloadMonitorService.HasRecentActivity(now.AddSeconds(-13), now));
        Assert.False(DownloadMonitorService.HasRecentActivity(now.AddMinutes(-10), now));
    }

    [Theory]
    [InlineData("archive.zip.crdownload", "archive.zip")]
    [InlineData("video.mp4.part", "video.mp4")]
    [InlineData("setup.exe.tmp.crdownload", "setup.exe")]
    public void DownloadDisplayNameRemovesPartialExtension(string source, string expected)
    {
        Assert.Equal(expected, DownloadMonitorService.CleanDisplayName(source));
    }

}
