using System.IO;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace X20Ctl.Product;

/// <summary>
/// Native Windows Runtime (WinRT) Bluetooth Low Energy client for KeyLinker controllers.
/// Requires NO Python, NO bleak, and NO external dependencies.
/// Pure C# WinRT calling Windows.Devices.Bluetooth directly.
/// </summary>
public static class NativeBleClient
{
    private static readonly Guid ServiceUuid = Guid.Parse("d7f010e0-660d-46e9-96c3-19c4148bdab5");
    private static readonly Guid WriteUuid   = Guid.Parse("d7f010e1-660d-46e9-96c3-19c4148bdab5");
    private static readonly Guid NotifyUuid  = Guid.Parse("d7f010e2-660d-46e9-96c3-19c4148bdab5");

    private static string CachePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "x20ctl", "native", "bt_cache.txt");

    public static async Task<PadBridge.Result> QueryAsync(int timeoutSeconds = 6)
    {
        try
        {
            var (address, error) = await FindDeviceAddressAsync(timeoutSeconds);
            if (address == null)
            {
                return new(true, false, error ?? "Controller not found over Bluetooth. Make sure Bluetooth is enabled and the controller is awake.");
            }

            string addressStr = FormatMac(address.Value);
            return await QueryGattAsync(address.Value, addressStr);
        }
        catch (Exception ex)
        {
            return new(true, false, $"Bluetooth query failed: {ex.Message}");
        }
    }

    private static async Task<(ulong? Address, string? Error)> FindDeviceAddressAsync(int timeoutSeconds)
    {
        ulong? cachedAddress = null;
        if (File.Exists(CachePath))
        {
            try
            {
                string text = File.ReadAllText(CachePath).Trim();
                if (ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out ulong cached) && cached != 0)
                {
                    cachedAddress = cached;
                }
            }
            catch { }
        }

        var tcs = new TaskCompletionSource<ulong>();
        string? scanError = null;

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += (w, args) =>
        {
            ulong addr = args.BluetoothAddress;
            byte[] mac = BitConverter.GetBytes(addr);
            bool isVendorMac = mac.Length >= 6 && mac[5] == 0x98 && mac[4] == 0xB6 &&
                               (mac[3] == 0xE9 || mac[3] == 0xEC || mac[3] == 0xED);

            string name = args.Advertisement.LocalName ?? "";
            bool serviceMatch = args.Advertisement.ServiceUuids.Contains(ServiceUuid);
            bool nameMatch = name.Contains("EasySMX", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("Xpert2", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("X20", StringComparison.OrdinalIgnoreCase);

            if (serviceMatch || nameMatch || isVendorMac)
            {
                try { w.Stop(); } catch { }
                tcs.TrySetResult(addr);
            }
        };

        watcher.Stopped += (w, args) =>
        {
            if (args.Error == BluetoothError.RadioNotAvailable)
            {
                scanError = "Bluetooth is turned off in Windows Settings or no Bluetooth adapter is present.";
            }
            else if (args.Error != BluetoothError.Success)
            {
                scanError = $"Bluetooth scan failed ({args.Error}).";
            }
        };

        try
        {
            watcher.Start();
        }
        catch (Exception ex)
        {
            return (null, $"Could not start Bluetooth scan: {ex.Message}");
        }

        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
        var completed = await Task.WhenAny(tcs.Task, timeoutTask);
        try { watcher.Stop(); } catch { }

        if (completed == tcs.Task)
        {
            return (await tcs.Task, null);
        }

        if (scanError != null)
        {
            return (null, scanError);
        }

        // If scanning timed out but we have a valid cached address, try connecting to it as fallback
        if (cachedAddress.HasValue)
        {
            return (cachedAddress.Value, null);
        }

        return (null, "Controller not found over Bluetooth. Make sure Bluetooth is enabled and the controller is awake.");
    }

    private static async Task<PadBridge.Result> QueryGattAsync(ulong bluetoothAddress, string addressStr)
    {
        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress);
        if (device == null)
        {
            ClearCache();
            return new(true, false, "Could not open Bluetooth LE controller device.");
        }

        var gattServices = await device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached);
        if (gattServices.Status != GattCommunicationStatus.Success || gattServices.Services.Count == 0)
        {
            gattServices = await device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Cached);
        }
        if (gattServices.Status != GattCommunicationStatus.Success || gattServices.Services.Count == 0)
        {
            ClearCache();
            return new(true, false, $"The controller at {addressStr} does not expose the KeyLinker configuration service.");
        }

        using var service = gattServices.Services[0];
        var writeRes = await service.GetCharacteristicsForUuidAsync(WriteUuid, BluetoothCacheMode.Uncached);
        var notifyRes = await service.GetCharacteristicsForUuidAsync(NotifyUuid, BluetoothCacheMode.Uncached);

        if (writeRes.Status != GattCommunicationStatus.Success || writeRes.Characteristics.Count == 0 ||
            notifyRes.Status != GattCommunicationStatus.Success || notifyRes.Characteristics.Count == 0)
        {
            return new(true, false, "Configuration characteristics could not be accessed.");
        }

        var writeChar = writeRes.Characteristics[0];
        var notifyChar = notifyRes.Characteristics[0];

        var notifyStatus = await notifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (notifyStatus != GattCommunicationStatus.Success)
            return new(true, false, "Failed to subscribe to controller notifications.");

        byte expectedSerial = 0;
        TaskCompletionSource<byte[]>? pendingReply = null;
        Windows.Foundation.TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler = (s, e) =>
        {
            var reader = DataReader.FromBuffer(e.CharacteristicValue);
            byte[] bytes = new byte[e.CharacteristicValue.Length];
            reader.ReadBytes(bytes);

            var (op, len, ser, non, pay, crc) = X20Codec.Decode(bytes);
            if (crc && ser == expectedSerial)
            {
                pendingReply?.TrySetResult(pay);
            }
        };

        notifyChar.ValueChanged += handler;

        async Task<byte[]?> SendQueryAsync(byte opcode, byte length, byte serial, byte[] payload, int timeoutMs = 2500)
        {
            expectedSerial = serial;
            pendingReply = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            byte[] packet = X20Codec.Frame(opcode, length, serial, 0, payload);

            var writer = new DataWriter();
            writer.WriteBytes(packet);
            var writeResult = await writeChar.WriteValueWithResultAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse);
            if (writeResult.Status != GattCommunicationStatus.Success)
                return null;

            var timeoutTask = Task.Delay(timeoutMs);
            var finished = await Task.WhenAny(pendingReply.Task, timeoutTask);
            return finished == pendingReply.Task ? await pendingReply.Task : null;
        }

        string? firmware = null;
        string? controller = null;
        string? battery = null;
        bool charging = false;

        try
        {
            // 1. Query Device Info (Op 0x91: READ_VID_PID_VERSION)
            var payInfo = await SendQueryAsync(0x91, 5, 1, []);
            if (payInfo != null && payInfo.Length >= 7)
            {
                string vid = $"{payInfo[1]:x2}{payInfo[2]:x2}";
                string pid = $"{payInfo[3]:x2}{payInfo[4]:x2}";
                firmware = $"{payInfo[5]:x}.{payInfo[6]:x2}";
                controller = (vid == "1020" && pid == "2006") ? "EasySMX X20 Pro" :
                             (vid == "1013" && pid == "2009") ? "EasySMX X20" : "EasySMX Controller";
            }

            // 2. Query Battery (Op 0x1D: SET_MODE with [0x02, 0xDF, 0xAB])
            var payBat = await SendQueryAsync(0x1D, 8, 2, [0x02, 0xDF, 0xAB]);
            if (payBat != null && payBat.Length >= 5)
            {
                byte status = payBat[4];
                charging = (status >> 4 & 1) != 0;
                int level = (status >> 7 & 1) != 0 ? 4 :
                            (status >> 6 & 1) != 0 ? 3 :
                            (status >> 5 & 1) != 0 ? 2 : 1;
                battery = $"{level}/4" + (charging ? " (Charging)" : "");
            }

            // 3. Return to normal mode (Op 0x1E: NORMAL_MODE)
            byte[] normalPkt = X20Codec.Frame(0x1E, 8, 3, 0, [0x02, 0xDF, 0xAB]);
            var normalWriter = new DataWriter();
            normalWriter.WriteBytes(normalPkt);
            _ = writeChar.WriteValueAsync(normalWriter.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
        }
        finally
        {
            notifyChar.ValueChanged -= handler;
            try
            {
                await notifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch { }
        }

        // Cache the verified working address
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, bluetoothAddress.ToString("X"));
        }
        catch { }

        var sb = new StringBuilder();
        sb.AppendLine("Controller");
        sb.AppendLine("──────────────────────────────────────────────────────────────");
        sb.AppendLine($"  controller      {controller ?? "EasySMX X20 Pro"}");
        sb.AppendLine($"  firmware        {firmware ?? "Not reported"}");
        sb.AppendLine($"  address         {addressStr}");
        sb.AppendLine($"  battery         {battery ?? "Not reported"}{(charging ? " · charging" : " · on battery")}");
        sb.AppendLine("  playing over    2.4 GHz receiver · EasySMX X20 Pro receiver");

        var telemetry = new TelemetryData(controller ?? "EasySMX X20 Pro", firmware, battery, charging, addressStr, sb.ToString());
        return new PadBridge.Result(true, true, sb.ToString(), telemetry);
    }

    private static void ClearCache()
    {
        try
        {
            if (File.Exists(CachePath)) File.Delete(CachePath);
        }
        catch { }
    }

    private static string FormatMac(ulong address)
    {
        byte[] bytes = BitConverter.GetBytes(address);
        return $"{bytes[5]:X2}:{bytes[4]:X2}:{bytes[3]:X2}:{bytes[2]:X2}:{bytes[1]:X2}:{bytes[0]:X2}";
    }
}
