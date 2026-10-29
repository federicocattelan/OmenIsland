using System.Management;
using LibreHardwareMonitor.Hardware;

namespace OmenIsland.Hardware.Telemetry;

public sealed class HardwareTelemetryReader : IDisposable
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true
    };

    private bool _isOpen;

    public void Open()
    {
        if (_isOpen)
        {
            return;
        }

        _computer.Open();
        _isOpen = true;
    }

    public TemperatureReadings Read()
    {
        if (!_isOpen)
        {
            Open();
        }

        _computer.Accept(new UpdateVisitor());

        var cpu = GetTemperature(
            hardware => hardware.HardwareType == HardwareType.Cpu,
            ["CPU Package", "Core (Tctl/Tdie)", "Tctl/Tdie", "CPU Core", "Tdie"]);
        if (cpu is null)
        {
            cpu = ReadAcpiCelsius();
        }

        var gpu = GetTemperature(
            hardware => hardware.HardwareType.ToString().StartsWith(
                "Gpu",
                StringComparison.OrdinalIgnoreCase),
            ["GPU Core", "GPU Temperature"]);

        return new TemperatureReadings(cpu, gpu, DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        if (!_isOpen)
        {
            return;
        }

        _computer.Close();
        _isOpen = false;
    }

    private float? GetTemperature(
        Func<IHardware, bool> hardwarePredicate,
        IReadOnlyList<string> preferredSensorNames)
    {
        var sensors = EnumerateHardware(_computer.Hardware)
            .Where(hardwarePredicate)
            .SelectMany(hardware => hardware.Sensors)
            .Where(sensor =>
                sensor.SensorType == SensorType.Temperature &&
                sensor.Value is not null &&
                sensor.Value is > 0 and < 125)
            .ToList();

        foreach (var name in preferredSensorNames)
        {
            var preferred = sensors.FirstOrDefault(sensor =>
                sensor.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (preferred?.Value is { } value)
            {
                return value;
            }
        }

        return sensors
            .Select(sensor => sensor.Value)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .DefaultIfEmpty()
            .Max() is var maximum && maximum > 0
                ? maximum
                : null;
    }

    private static float? ReadAcpiCelsius()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            using var results = searcher.Get();
            float? hottest = null;
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    var raw = Convert.ToDouble(item["CurrentTemperature"]);
                    var celsius = (float)((raw / 10d) - 273.15d);
                    if (celsius is > 1 and < 125)
                    {
                        hottest = hottest is null ? celsius : Math.Max(hottest.Value, celsius);
                    }
                }
            }

            return hottest;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<IHardware> EnumerateHardware(
        IEnumerable<IHardware> hardware)
    {
        foreach (var item in hardware)
        {
            yield return item;

            foreach (var child in EnumerateHardware(item.SubHardware))
            {
                yield return child;
            }
        }
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) =>
            computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();

            foreach (var child in hardware.SubHardware)
            {
                child.Accept(this);
            }
        }

        public void VisitParameter(IParameter parameter)
        {
        }

        public void VisitSensor(ISensor sensor)
        {
        }
    }
}

public sealed record TemperatureReadings(
    float? CpuCelsius,
    float? GpuCelsius,
    DateTimeOffset Timestamp);
