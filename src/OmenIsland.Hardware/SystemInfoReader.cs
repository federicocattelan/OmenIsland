using System.Management;

namespace OmenIsland.Hardware;

internal static class SystemInfoReader
{
    public static SystemIdentity Read()
    {
        var productName = ReadProperty(
            "Win32_ComputerSystem",
            "Model") ?? "HP PC";
        var boardId = ReadProperty(
            "Win32_BaseBoard",
            "Product") ?? "—";
        var biosVersion = ReadProperty(
            "Win32_BIOS",
            "SMBIOSBIOSVersion") ?? "—";

        return new SystemIdentity(productName, boardId, biosVersion);
    }

    private static string? ReadProperty(string className, string propertyName)
    {
        using var searcher = new ManagementObjectSearcher(
            $@"root\cimv2",
            $"SELECT {propertyName} FROM {className}");
        using var results = searcher.Get();

        return results
            .Cast<ManagementObject>()
            .Select(item => item[propertyName]?.ToString()?.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}

internal sealed record SystemIdentity(
    string ProductName,
    string BoardId,
    string BiosVersion);
