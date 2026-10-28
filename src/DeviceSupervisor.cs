using System;
using System.Threading;

namespace FanControl.SmartDeviceV1;

/// <summary>
/// Keeps a working connection to the device. The device streams status reports continuously
/// once initialized, so if they stop (sleep/wake, USB reset, unplug) or a write fails, the
/// connection is dropped, reopened, re-initialized and the last requested duties are restored.
/// </summary>
public sealed class DeviceSupervisor : IDisposable
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(2);

    private readonly Func<IFanDevice?> _open;
    private readonly Action<string> _log;
    private readonly Func<DateTime> _clock;
    private readonly object _lock = new();
    private readonly int?[] _duty;
    private readonly Timer? _timer;
    private IFanDevice? _device;
    private int _lastReports;
    private DateTime _lastProgress;
    private DateTime _nextAttempt;
    private bool _missingLogged;
    private bool _disposed;

    /// <param name="open">Opens a new connection, or returns null if the device isn't present.</param>
    /// <param name="clock">UTC clock; injectable for tests.</param>
    /// <param name="startTimer">false in tests, which call <see cref="Tick"/> directly.</param>
    public DeviceSupervisor(Func<IFanDevice?> open, Action<string> log, int channelCount,
        Func<DateTime>? clock = null, bool startTimer = true)
    {
        _open = open;
        _log = log;
        _clock = clock ?? (() => DateTime.UtcNow);
        _duty = new int?[channelCount];
        lock (_lock) Connect(isReconnect: false);
        if (startTimer)
            _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public bool IsConnected
    {
        get { lock (_lock) return _device != null; }
    }

    public int ReportsReceived
    {
        get { lock (_lock) return _device?.ReportsReceived ?? 0; }
    }

    public int GetRpm(int channel)
    {
        lock (_lock) return _device?.GetRpm(channel) ?? 0;
    }

    public int GetMode(int channel)
    {
        lock (_lock) return _device?.GetMode(channel) ?? 0;
    }

    /// <summary>Remembers the duty and sends it now if connected; it is re-sent after a reconnect.</summary>
    public void SetDuty(int channel, int duty)
    {
        lock (_lock)
        {
            _duty[channel] = duty;
            if (_device == null)
                return;
            try
            {
                _device.SetDuty(channel, duty);
            }
            catch (Exception e)
            {
                Drop($"write failed: {e.Message}");
            }
        }
    }

    /// <summary>Health check, run once a second by the timer.</summary>
    public void Tick()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            var now = _clock();
            if (_device != null)
            {
                var reports = _device.ReportsReceived;
                if (reports != _lastReports)
                {
                    _lastReports = reports;
                    _lastProgress = now;
                }
                else if (now - _lastProgress >= StaleAfter)
                {
                    Drop($"no status reports for {StaleAfter.TotalSeconds:0} s");
                }
            }
            if (_device == null && now >= _nextAttempt)
                Connect(isReconnect: true);
        }
    }

    private void Connect(bool isReconnect)
    {
        var now = _clock();
        IFanDevice? device = null;
        try
        {
            device = _open();
            if (device == null)
            {
                if (!_missingLogged)
                    _log($"device not found, retrying every {RetryEvery.TotalSeconds:0} s");
                _missingLogged = true;
                _nextAttempt = now + RetryEvery;
                return;
            }
            device.Initialize();
            for (var ch = 0; ch < _duty.Length; ch++)
            {
                if (_duty[ch] is int duty)
                    device.SetDuty(ch, duty);
            }
            _device = device;
            _lastReports = device.ReportsReceived;
            _lastProgress = now;
            _missingLogged = false;
            if (isReconnect)
                _log("reconnected and restored fan speeds");
        }
        catch (Exception e)
        {
            _log($"connect failed: {e.Message}");
            try { device?.Dispose(); } catch { /* already broken */ }
            _nextAttempt = now + RetryEvery;
        }
    }

    private void Drop(string reason)
    {
        _log($"connection lost ({reason}), reconnecting");
        try { _device?.Dispose(); } catch { /* already broken */ }
        _device = null;
        _nextAttempt = _clock();
    }

    public void Dispose()
    {
        _timer?.Dispose();
        lock (_lock)
        {
            _disposed = true;
            try { _device?.Dispose(); } catch { /* closing anyway */ }
            _device = null;
        }
    }
}
