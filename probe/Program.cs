using System;
using System.Threading;
using FanControl.SmartDeviceV1;

// Test tool for the Smart Device V1, used without FanControl.
//   SmartDeviceProbe status            show detected fans and rpm
//   SmartDeviceProbe set <fan> <duty>  set fan 1-3 (or "all") to duty 0-100 %, then show rpm for 10 s

var device = SmartDevice.Open(m => Console.WriteLine($"  {m}"));
if (device == null)
{
    Console.WriteLine("Smart Device V1 not found. Is NZXT CAM closed?");
    return 1;
}

using (device)
{
    device.Initialize();
    WaitForReports(device, 3);
    PrintStatus(device);

    if (args.Length == 3 && args[0] == "set")
    {
        var duty = int.Parse(args[2]);
        var channels = args[1] == "all" ? new[] { 0, 1, 2 } : new[] { int.Parse(args[1]) - 1 };
        foreach (var ch in channels)
        {
            Console.WriteLine($"Setting fan {ch + 1} to {duty}%");
            device.SetDuty(ch, duty);
        }
        for (var s = 0; s < 10; s++)
        {
            Thread.Sleep(1000);
            PrintStatus(device);
        }
    }
}
return 0;

static void WaitForReports(SmartDevice device, int seconds)
{
    var deadline = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < deadline && device.ReportsReceived < SmartDevice.FanCount * 2)
        Thread.Sleep(100);
    Console.WriteLine($"  {device.ReportsReceived} status reports received");
}

static void PrintStatus(SmartDevice device)
{
    string[] modes = { "not detected", "DC", "PWM" };
    for (var i = 0; i < SmartDevice.FanCount; i++)
        Console.Write($"Fan {i + 1}: {device.GetRpm(i),5} rpm ({modes[Math.Min(device.GetMode(i), 2)]})   ");
    Console.WriteLine();
}
