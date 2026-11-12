using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace OmenIsland.App.Services.Headphones;

public sealed record PairedDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

public static class BluetoothDeviceCatalog
{
    public const string PreferredName = "CMF Headphone Pro";

    public static async Task<IReadOnlyList<PairedDevice>> ListPairedAsync()
    {
        var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        var devices = await DeviceInformation.FindAllAsync(selector);
        return devices
            .Where(device => !string.IsNullOrWhiteSpace(device.Name))
            .Select(device => new PairedDevice(device.Id, device.Name))
            .OrderByDescending(device => IsPreferred(device.Name))
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static bool IsPreferred(string name) =>
        name.Contains(PreferredName, StringComparison.OrdinalIgnoreCase);
}
