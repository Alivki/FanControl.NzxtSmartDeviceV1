# fancontrol-nzxt-smart-device

A [FanControl](https://github.com/Rem0o/FanControl.Releases) plugin for the **NZXT Smart Device (V1)** (USB `1E71:1714`), so its three fan channels can be controlled with FanControl curves instead of NZXT CAM.

Each channel shows up in FanControl as a control card ("Smart Device Fan 1–3") paired with its RPM sensor. When FanControl releases a channel, it is set to 50 % so fans never stop unattended.

## Install

1. Close NZXT CAM (it must not control the device at the same time).
2. Build: `dotnet build src -c Release`
3. Copy `src/bin/Release/net8.0/FanControl.SmartDeviceV1.dll` into `FanControl\Plugins` and restart FanControl.

## Test without FanControl

```
dotnet run --project probe -- status
dotnet run --project probe -- set 1 40     # fan 1 to 40 %
dotnet run --project probe -- set all 60
```

## Notes

- Only the Smart Device **V1** is supported and tested.
- The device protocol is documented by [liquidctl](https://github.com/liquidctl/liquidctl). This plugin is an independent C# implementation.

## Safety

- FanControl runs as administrator, so this plugin does too. It only opens the Smart Device's HID interface; it has no network access and reads or writes no files.
- The only commands sent are the three documented ones: detect, start reporting and set fixed duty (clamped to 0–100 %).
- When FanControl closes, all three channels are set to 50 %. If FanControl crashes, fans keep their last speed until the next restart or power cycle.
- Build from source rather than running a DLL from someone you don't know.

## License

MIT
