using System;

namespace FanControl.SmartDeviceV1;

/// <summary>An open connection to a fan controller. Lets the supervisor be tested without hardware.</summary>
public interface IFanDevice : IDisposable
{
    /// <summary>Count of status reports received since the connection was opened.</summary>
    int ReportsReceived { get; }

    void Initialize();
    void SetDuty(int channel, int duty);
    int GetRpm(int channel);
    int GetMode(int channel);
}
