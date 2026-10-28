using System;
using System.Threading;

namespace FanControl.SmartDeviceV1;

/// <summary>
/// Keeps a working connection to the device. The device streams status reports continuously
/// once initialized, so if they stop (USB reset, unplug, another app taking the device) or a
/// write fails, the connection is dropped, reopened, re-initialized and the last requested
/// duties are restored.
/// Logging is limited to one line when a problem starts and one when the connection has been
/// stable again for <see cref="StableAfter"/>, so a flapping or missing device can't flood the log.
/// </summary>
public sealed class DeviceSupervisor : IDisposable
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(30);

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
    private DateTime _connectedAt;
    private bool _inOutage;
    private int _suppressed;
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
        // the first attempt is reported by the caller, so it is not logged here
        lock (_lock) Connect(logFailure: false);
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
                Connect(logFailure: true);

            if (_inOutage && _device != null && now - _connectedAt >= StableAfter)
            {
                var note = _suppressed > 0 ? $" ({_suppressed} further errors not logged)" : "";
                _log($"connection restored and fan speeds re-applied{note}");
                _inOutage = false;
                _suppressed = 0;
            }
        }
    }

    private void Connect(bool logFailure)
    {
        var now = _clock();
        IFanDevice? device = null;
        try
        {
            device = _open();
            if (device == null)
            {
                if (logFailure)
                    Problem($"device not found, retrying every {RetryEvery.TotalSeconds:0} s");
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
            _connectedAt = now;
        }
        catch (Exception e)
        {
            if (logFailure)
                Problem($"reconnect failed: {e.Message}");
            try { device?.Dispose(); } catch { /* already broken */ }
            _nextAttempt = now + RetryEvery;
        }
    }

    private void Drop(string reason)
    {
        Problem($"connection lost ({reason}), reconnecting");
        try { _device?.Dispose(); } catch { /* already broken */ }
        _device = null;
        _nextAttempt = _clock();
    }

    /// <summary>Logs the first problem of an outage; later ones are only counted.</summary>
    private void Problem(string message)
    {
        if (_inOutage)
        {
            _suppressed++;
            return;
        }
        _log(message);
        _inOutage = true;
        _suppressed = 0;
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
