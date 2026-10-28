using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FanControl.SmartDeviceV1;

/// <summary>
/// NZXT Smart Device (V1), USB 1E71:1714. Three fan channels.
/// Protocol as documented by liquidctl (liquidctl/driver/smart_device.py, GPL-3.0):
///   write [0x01, 0x5c]                  detect connected fans/LEDs
///   write [0x01, 0x5d]                  start periodic status reports
///   write [0x02, 0x4d, ch, 0x00, duty]  set fixed duty 0-100 % on channel 0-2
///   status report (21 bytes): rpm = b[3]&lt;&lt;8 | b[4], channel = b[15]&gt;&gt;4, mode = b[15]&amp;3 (0 none, 1 DC, 2 PWM)
/// </summary>
public sealed class SmartDevice : IFanDevice
{
    public const ushort VendorId = 0x1E71;
    public const ushort ProductId = 0x1714;
    public const int FanCount = 3;

    private readonly FileStream _stream;
    private readonly int _outputLength;
    private readonly int _inputLength;
    private readonly object _writeLock = new();
    private readonly object _statusLock = new();
    private readonly int[] _rpm = new int[FanCount];
    private readonly int[] _mode = new int[FanCount];
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string> _log;
    private readonly Task _reader;
    private int _reportsReceived;
    private bool _readErrorLogged;

    private SmartDevice(FileStream stream, int inputLength, int outputLength, Action<string> log)
    {
        _stream = stream;
        _inputLength = inputLength;
        _outputLength = outputLength;
        _log = log;
        _reader = Task.Run(ReadLoopAsync);
    }

    public int ReportsReceived => Volatile.Read(ref _reportsReceived);

    /// <summary>
    /// Opens the first Smart Device V1 found. Returns null if none is present; throws if one is
    /// present but can't be opened, so the caller decides what to log.
    /// </summary>
    /// <param name="log">Receives read errors from the background reader (at most one per connection).</param>
    public static SmartDevice? Open(Action<string> log)
    {
        Exception? lastError = null;
        foreach (var path in Hid.FindPaths(VendorId, ProductId))
        {
            try
            {
                var handle = Hid.Open(path, out var inLen, out var outLen);
                if (inLen < StatusReport.MinLength || outLen < 5)
                {
                    handle.Dispose();
                    continue;
                }
                var stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
                return new SmartDevice(stream, inLen, outLen, log);
            }
            catch (Exception e)
            {
                lastError = e;
            }
        }
        if (lastError != null)
            throw lastError;
        return null;
    }

    /// <summary>Detects fans and starts status reporting. Call once after opening.</summary>
    public void Initialize()
    {
        Write(0x01, 0x5c);
        Write(0x01, 0x5d);
    }

    public void SetDuty(int channel, int duty)
    {
        if (channel is < 0 or >= FanCount)
            throw new ArgumentOutOfRangeException(nameof(channel));
        Write(0x02, 0x4d, (byte)channel, 0x00, (byte)Math.Clamp(duty, 0, 100));
    }

    public int GetRpm(int channel)
    {
        lock (_statusLock) return _rpm[channel];
    }

    /// <summary>0 = not detected, 1 = DC, 2 = PWM.</summary>
    public int GetMode(int channel)
    {
        lock (_statusLock) return _mode[channel];
    }

    private void Write(params byte[] data)
    {
        var report = new byte[_outputLength];
        Array.Copy(data, report, data.Length);
        lock (_writeLock)
        {
            _stream.Write(report, 0, report.Length);
            _stream.Flush();
        }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[_inputLength];
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var read = await _stream.ReadAsync(buffer.AsMemory(0, _inputLength), _cts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    // handle no longer delivers data (device gone); don't spin
                    await Task.Delay(1000, _cts.Token).ConfigureAwait(false);
                    continue;
                }
                if (!StatusReport.TryParse(buffer.AsSpan(0, read), out var status))
                    continue;
                lock (_statusLock)
                {
                    _rpm[status.Channel] = status.Rpm;
                    _mode[status.Channel] = status.Mode;
                }
                Interlocked.Increment(ref _reportsReceived);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                if (_cts.IsCancellationRequested)
                    break; // handle closed during Dispose, not a real error
                // the supervisor notices the missing reports and reconnects; one line is enough
                if (!_readErrorLogged)
                    _log($"read error: {e.Message}");
                _readErrorLogged = true;
                try { await Task.Delay(1000, _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _reader.Wait(2000); } catch { /* reader already logged */ }
        _stream.Dispose();
        _cts.Dispose();
    }
}
