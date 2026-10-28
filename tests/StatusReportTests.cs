using System;
using FanControl.SmartDeviceV1;
using Xunit;

namespace FanControl.SmartDeviceV1.Tests;

public class StatusReportTests
{
    /// <summary>Builds a 21-byte report with the given fields; other bytes are filled with noise.</summary>
    private static byte[] Report(int channel, int rpm, int mode, int length = 21)
    {
        var report = new byte[length];
        new Random(42).NextBytes(report);
        if (length > 4)
        {
            report[3] = (byte)(rpm >> 8);
            report[4] = (byte)rpm;
        }
        if (length > 15)
            report[15] = (byte)(channel << 4 | mode);
        return report;
    }

    [Fact]
    public void Decodes_channel_rpm_and_mode()
    {
        Assert.True(StatusReport.TryParse(Report(channel: 1, rpm: 1492, mode: 2), out var status));
        Assert.Equal(new FanStatus(1, 1492, 2), status);
    }

    [Fact]
    public void Rpm_is_big_endian()
    {
        var report = Report(0, 0, 1);
        report[3] = 0x12;
        report[4] = 0x34;
        Assert.True(StatusReport.TryParse(report, out var status));
        Assert.Equal(0x1234, status.Rpm);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65535)]
    public void Rpm_covers_full_range(int rpm)
    {
        Assert.True(StatusReport.TryParse(Report(2, rpm, 1), out var status));
        Assert.Equal(rpm, status.Rpm);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Mode_is_low_two_bits(int mode)
    {
        var report = Report(0, 800, mode);
        report[15] |= 0b1100; // bits 2-3 are not part of the mode
        Assert.True(StatusReport.TryParse(report, out var status));
        Assert.Equal(mode, status.Mode);
        Assert.Equal(0, status.Channel);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Accepts_every_device_channel(int channel)
    {
        Assert.True(StatusReport.TryParse(Report(channel, 1000, 2), out var status));
        Assert.Equal(channel, status.Channel);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(15)]
    public void Rejects_channels_the_device_does_not_have(int channel)
    {
        Assert.False(StatusReport.TryParse(Report(channel, 1000, 2), out var status));
        Assert.Equal(default, status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(15)]
    public void Rejects_reports_too_short_to_hold_the_channel_byte(int length)
    {
        Assert.False(StatusReport.TryParse(Report(0, 1000, 2, length), out var status));
        Assert.Equal(default, status);
    }

    [Fact]
    public void Accepts_minimum_and_longer_reports()
    {
        Assert.True(StatusReport.TryParse(Report(1, 900, 1, StatusReport.MinLength), out _));
        Assert.True(StatusReport.TryParse(Report(1, 900, 1, 65), out var status));
        Assert.Equal(new FanStatus(1, 900, 1), status);
    }
}
