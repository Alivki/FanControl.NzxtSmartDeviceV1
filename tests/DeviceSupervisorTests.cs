using System;
using System.Collections.Generic;
using FanControl.SmartDeviceV1;
using Xunit;

namespace FanControl.SmartDeviceV1.Tests;

public class DeviceSupervisorTests
{
    private sealed class FakeDevice : IFanDevice
    {
        public int ReportsReceived { get; set; }
        public int InitializeCount { get; private set; }
        public bool Disposed { get; private set; }
        public bool FailWrites { get; set; }
        public Dictionary<int, int> Duties { get; } = new();

        public void Initialize() => InitializeCount++;

        public void SetDuty(int channel, int duty)
        {
            if (FailWrites)
                throw new InvalidOperationException("device gone");
            Duties[channel] = duty;
        }

        public int GetRpm(int channel) => 1000 + channel;
        public int GetMode(int channel) => 2;
        public void Dispose() => Disposed = true;
    }

    /// <summary>Supervisor with a manual clock and a queue of devices to hand out on each open.</summary>
    private sealed class Harness
    {
        public DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        public readonly Queue<Func<IFanDevice?>> Opens = new();
        public readonly List<string> Log = new();
        public int OpenCalls;
        public Func<IFanDevice?>? Fallback; // used once the queue is empty; null means "not found"
        public DeviceSupervisor Supervisor = null!;

        public Harness(params FakeDevice?[] devices)
        {
            foreach (var d in devices)
                Opens.Enqueue(() => d);
        }

        public Harness Start()
        {
            Supervisor = new DeviceSupervisor(Open, Log.Add, channelCount: 3, clock: () => Now, startTimer: false);
            return this;
        }

        private IFanDevice? Open()
        {
            OpenCalls++;
            if (Opens.Count > 0)
                return Opens.Dequeue()();
            return Fallback?.Invoke();
        }

        public void Advance(double seconds)
        {
            Now = Now.AddSeconds(seconds);
            Supervisor.Tick();
        }
    }

    [Fact]
    public void Connects_and_initializes_on_start()
    {
        var device = new FakeDevice();
        var h = new Harness(device).Start();

        Assert.True(h.Supervisor.IsConnected);
        Assert.Equal(1, device.InitializeCount);
        Assert.Equal(1001, h.Supervisor.GetRpm(1));
    }

    [Fact]
    public void Stays_connected_while_reports_keep_arriving()
    {
        var device = new FakeDevice();
        var h = new Harness(device).Start();

        for (var i = 0; i < 30; i++)
        {
            device.ReportsReceived++;
            h.Advance(1);
        }

        Assert.True(h.Supervisor.IsConnected);
        Assert.Equal(1, h.OpenCalls);
        Assert.False(device.Disposed);
    }

    [Fact]
    public void Does_not_drop_before_reports_have_been_missing_for_the_full_window()
    {
        var device = new FakeDevice();
        var h = new Harness(device, new FakeDevice()).Start();

        h.Advance(DeviceSupervisor.StaleAfter.TotalSeconds - 1);

        Assert.False(device.Disposed);
        Assert.Equal(1, h.OpenCalls);
    }

    [Fact]
    public void Reconnects_and_restores_duties_when_reports_stop()
    {
        var first = new FakeDevice();
        var second = new FakeDevice();
        var h = new Harness(first, second).Start();
        h.Supervisor.SetDuty(1, 35);
        h.Supervisor.SetDuty(2, 60);

        // e.g. PC went to sleep: the device stops reporting
        h.Advance(DeviceSupervisor.StaleAfter.TotalSeconds);

        Assert.True(first.Disposed);
        Assert.True(h.Supervisor.IsConnected);
        Assert.Equal(1, second.InitializeCount);
        Assert.Equal(35, second.Duties[1]);
        Assert.Equal(60, second.Duties[2]);
        Assert.False(second.Duties.ContainsKey(0));
        Assert.Single(h.Log, l => l.Contains("connection lost"));
    }

    [Fact]
    public void Recovery_is_logged_once_the_connection_has_been_stable()
    {
        var second = new FakeDevice();
        var h = new Harness(new FakeDevice(), second).Start();
        h.Advance(DeviceSupervisor.StaleAfter.TotalSeconds); // drop + reconnect

        for (var s = 1; s < DeviceSupervisor.StableAfter.TotalSeconds; s++)
        {
            second.ReportsReceived++;
            h.Advance(1);
        }
        Assert.DoesNotContain(h.Log, l => l.Contains("restored"));

        second.ReportsReceived++;
        h.Advance(1);
        Assert.Single(h.Log, l => l.Contains("restored"));
        Assert.Equal(2, h.Log.Count);
    }

    [Fact]
    public void Flapping_device_logs_one_problem_and_one_recovery()
    {
        var h = new Harness();
        for (var i = 0; i < 50; i++)
            h.Opens.Enqueue(() => new FakeDevice()); // each one goes silent right away
        var stable = new FakeDevice();
        h.Opens.Enqueue(() => stable);
        h.Start();

        // 50 devices that each stop reporting: drop, reconnect, drop, ...
        for (var i = 0; i < 50; i++)
            h.Advance(DeviceSupervisor.StaleAfter.TotalSeconds);
        for (var s = 0; s <= DeviceSupervisor.StableAfter.TotalSeconds; s++)
        {
            stable.ReportsReceived++;
            h.Advance(1);
        }

        Assert.Equal(2, h.Log.Count);
        Assert.Contains("connection lost", h.Log[0]);
        Assert.Contains("49 further errors not logged", h.Log[1]);
    }

    [Fact]
    public void Persistent_failure_logs_a_single_line()
    {
        var h = new Harness { Fallback = () => throw new UnauthorizedAccessException("held by another app") };
        h.Opens.Enqueue(() => new FakeDevice());
        h.Start();

        // device lost, then every reconnect attempt fails for an hour
        for (var s = 0; s < 3600; s++)
            h.Advance(1);

        Assert.Single(h.Log);
        Assert.Contains("connection lost", h.Log[0]);
    }

    [Fact]
    public void Failure_at_startup_is_left_to_the_caller_to_report()
    {
        var h = new Harness().Start();
        Assert.False(h.Supervisor.IsConnected);
        Assert.Empty(h.Log);
    }

    [Fact]
    public void Write_failure_drops_and_next_tick_reconnects_with_that_duty()
    {
        var first = new FakeDevice { FailWrites = true };
        var second = new FakeDevice();
        var h = new Harness(first, second).Start();

        h.Supervisor.SetDuty(2, 45);
        Assert.False(h.Supervisor.IsConnected);
        Assert.True(first.Disposed);

        h.Advance(1);

        Assert.True(h.Supervisor.IsConnected);
        Assert.Equal(45, second.Duties[2]);
    }

    [Fact]
    public void Keeps_retrying_while_device_is_missing_and_restores_duties_set_meanwhile()
    {
        var device = new FakeDevice();
        var h = new Harness(null, null, device).Start();
        Assert.False(h.Supervisor.IsConnected);

        // duty requested while disconnected is remembered, not lost
        h.Supervisor.SetDuty(0, 40);

        h.Advance(1); // before the retry interval: no new attempt
        Assert.Equal(1, h.OpenCalls);

        h.Advance(DeviceSupervisor.RetryEvery.TotalSeconds); // retry, still missing
        Assert.False(h.Supervisor.IsConnected);

        h.Advance(DeviceSupervisor.RetryEvery.TotalSeconds); // retry, device is back
        Assert.True(h.Supervisor.IsConnected);
        Assert.Equal(40, device.Duties[0]);
        Assert.Equal(3, h.OpenCalls);
        Assert.Single(h.Log, l => l.Contains("not found"));
    }

    [Fact]
    public void Open_throwing_is_retried_later()
    {
        var device = new FakeDevice();
        var h = new Harness();
        h.Opens.Enqueue(() => throw new UnauthorizedAccessException("in use"));
        h.Opens.Enqueue(() => device);
        h.Start();

        Assert.False(h.Supervisor.IsConnected);
        h.Advance(DeviceSupervisor.RetryEvery.TotalSeconds);
        Assert.True(h.Supervisor.IsConnected);
    }

    [Fact]
    public void Initialize_failure_disposes_the_half_open_device()
    {
        var broken = new ThrowingInitDevice();
        var good = new FakeDevice();
        var h = new Harness();
        h.Opens.Enqueue(() => broken);
        h.Opens.Enqueue(() => good);
        h.Start();

        Assert.True(broken.Disposed);
        h.Advance(DeviceSupervisor.RetryEvery.TotalSeconds);
        Assert.True(h.Supervisor.IsConnected);
    }

    [Fact]
    public void Disconnected_reads_return_zero()
    {
        var h = new Harness().Start();
        Assert.Equal(0, h.Supervisor.GetRpm(0));
        Assert.Equal(0, h.Supervisor.GetMode(0));
        Assert.Equal(0, h.Supervisor.ReportsReceived);
    }

    [Fact]
    public void Dispose_closes_device_and_stops_reconnecting()
    {
        var device = new FakeDevice();
        var h = new Harness(device, new FakeDevice()).Start();

        h.Supervisor.Dispose();
        h.Advance(60);

        Assert.True(device.Disposed);
        Assert.False(h.Supervisor.IsConnected);
        Assert.Equal(1, h.OpenCalls);
    }

    private sealed class ThrowingInitDevice : IFanDevice
    {
        public bool Disposed { get; private set; }
        public int ReportsReceived => 0;
        public void Initialize() => throw new InvalidOperationException("init failed");
        public void SetDuty(int channel, int duty) { }
        public int GetRpm(int channel) => 0;
        public int GetMode(int channel) => 0;
        public void Dispose() => Disposed = true;
    }
}
