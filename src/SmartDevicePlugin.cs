using System;
using System.Linq;
using System.Threading;
using FanControl.Plugins;

namespace FanControl.SmartDeviceV1;

/// <summary>FanControl plugin exposing the three NZXT Smart Device (V1) fan channels.</summary>
public sealed class SmartDevicePlugin : IPlugin2
{
    private readonly IPluginLogger _logger;
    private DeviceSupervisor? _device;

    public SmartDevicePlugin(IPluginLogger logger) => _logger = logger;

    public string Name => "NZXT Smart Device V1";

    public void Initialize()
    {
        try
        {
            var device = new DeviceSupervisor(() => SmartDevice.Open(Log), Log, SmartDevice.FanCount);
            if (!device.IsConnected)
            {
                // no cards for hardware that isn't there at startup
                Log("device not found - is it connected, and is NZXT CAM closed?");
                device.Dispose();
                return;
            }
            _device = device;

            // status reports arrive one channel at a time; wait briefly so the first rpm values are in
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && _device.ReportsReceived < SmartDevice.FanCount * 2)
                Thread.Sleep(100);
            var rpms = string.Join(", ", Enumerable.Range(0, SmartDevice.FanCount).Select(i => $"fan {i + 1} {_device.GetRpm(i)} rpm"));
            Log($"connected ({rpms})");
        }
        catch (Exception e)
        {
            Log($"initialize failed: {e}");
            _device?.Dispose();
            _device = null;
        }
    }

    public void Load(IPluginSensorsContainer container)
    {
        if (_device == null)
            return;
        for (var i = 0; i < SmartDevice.FanCount; i++)
        {
            var fan = new FanSensor(_device, i);
            container.FanSensors.Add(fan);
            container.ControlSensors.Add(new ControlSensor(_device, i, fan.Id));
        }
    }

    public void Update()
    {
        // status is pushed by the device and collected on a background reader
    }

    public void Close()
    {
        if (_device == null)
            return;
        // leave fans at a safe speed in case FanControl closes without resetting its controls
        for (var i = 0; i < SmartDevice.FanCount; i++)
            _device.SetDuty(i, ControlSensor.ResetDuty);
        _device.Dispose();
        _device = null;
    }

    private void Log(string message) => _logger.Log($"[SmartDeviceV1] {message}");
}

internal sealed class FanSensor : IPluginSensor
{
    private readonly DeviceSupervisor _device;
    private readonly int _channel;

    public FanSensor(DeviceSupervisor device, int channel)
    {
        _device = device;
        _channel = channel;
    }

    public string Id => $"NZXT.SmartDeviceV1/fan{_channel + 1}/rpm";
    public string Name => $"Smart Device Fan {_channel + 1}";
    public float? Value { get; private set; }

    public void Update() => Value = _device.GetRpm(_channel);
}

internal sealed class ControlSensor : IPluginControlSensor2
{
    // duty applied when FanControl releases the channel, so fans never stop unattended
    public const int ResetDuty = 50;
    private static readonly TimeSpan Reassert = TimeSpan.FromSeconds(10);

    private readonly DeviceSupervisor _device;
    private readonly int _channel;
    private int _lastWritten = -1;
    private DateTime _lastWrite;

    public ControlSensor(DeviceSupervisor device, int channel, string pairedFanSensorId)
    {
        _device = device;
        _channel = channel;
        PairedFanSensorId = pairedFanSensorId;
    }

    public string Id => $"NZXT.SmartDeviceV1/fan{_channel + 1}/control";
    public string Name => $"Smart Device Fan {_channel + 1}";
    public string PairedFanSensorId { get; }
    public float? Value { get; private set; }

    public void Set(float val)
    {
        var duty = (int)Math.Round(Math.Clamp(val, 0f, 100f));
        Value = duty;
        // FanControl calls Set every tick; only talk to the device on change, plus a periodic re-assert
        if (duty == _lastWritten && DateTime.UtcNow - _lastWrite < Reassert)
            return;
        Write(duty);
    }

    public void Reset()
    {
        Write(ResetDuty);
        Value = null;
        _lastWritten = -1;
    }

    public void Update()
    {
    }

    private void Write(int duty)
    {
        // the supervisor handles failures by reconnecting and re-applying the duty
        _device.SetDuty(_channel, duty);
        _lastWritten = duty;
        _lastWrite = DateTime.UtcNow;
    }
}
