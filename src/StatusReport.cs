using System;

namespace FanControl.SmartDeviceV1;

/// <summary>One fan channel's state from a Smart Device V1 status report.</summary>
/// <param name="Channel">0-based channel, 0-2.</param>
/// <param name="Rpm">Measured fan speed.</param>
/// <param name="Mode">0 = not detected, 1 = DC, 2 = PWM.</param>
public readonly record struct FanStatus(int Channel, int Rpm, int Mode);

/// <summary>Decodes the 21-byte status reports the device sends, one channel per report.</summary>
public static class StatusReport
{
    /// <summary>Bytes needed to reach the channel/mode byte at offset 15.</summary>
    public const int MinLength = 16;

    /// <summary>Returns false if the report is too short or names a channel the device doesn't have.</summary>
    public static bool TryParse(ReadOnlySpan<byte> report, out FanStatus status)
    {
        status = default;
        if (report.Length < MinLength)
            return false;

        var channel = report[15] >> 4;
        if (channel >= SmartDevice.FanCount)
            return false;

        status = new FanStatus(channel, report[3] << 8 | report[4], report[15] & 0x3);
        return true;
    }
}
