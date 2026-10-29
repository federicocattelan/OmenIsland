using System.Management;
using System.Security.Principal;

namespace OmenIsland.Hardware.HpWmi;

/// <summary>
/// Minimal clean-room transport for HP's hpqBIntM ACPI/WMI interface.
/// It does not load or invoke OMEN Gaming Hub or OmenMon components.
/// </summary>
public sealed class HpWmiBiosClient : IDisposable
{
    private static readonly byte[] Signature = [0x53, 0x45, 0x43, 0x55]; // "SECU"

    private readonly object _sync = new();
    private ManagementScope? _scope;
    private ManagementObject? _biosMethods;
    private bool _disposed;

    public bool IsAvailable { get; private set; }

    public bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity)
                .IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public void Initialize()
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            if (IsAvailable)
            {
                return;
            }

            _scope = new ManagementScope(@"\\.\root\wmi");
            _scope.Connect();

            using var searcher = new ManagementObjectSearcher(
                _scope,
                new ObjectQuery("SELECT * FROM hpqBIntM"));
            using var results = searcher.Get();

            _biosMethods = results.Cast<ManagementObject>().FirstOrDefault();
            IsAvailable = _biosMethods is not null;

            if (!IsAvailable)
            {
                throw new HpWmiException(
                    "Interfaccia HP WMI hpqBIntM non trovata.");
            }
        }
    }

    public HpWmiResult Execute(
        byte commandType,
        ReadOnlySpan<byte> input,
        int outputSize) =>
        Execute(HpWmiProtocol.GamingCommand, commandType, input, outputSize);

    public HpWmiResult Execute(
        uint command,
        byte commandType,
        ReadOnlySpan<byte> input,
        int outputSize)
    {
        ThrowIfDisposed();

        if (outputSize is not (0 or 4 or 128))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputSize),
                "HP WMI supporta output da 0, 4 o 128 byte.");
        }

        lock (_sync)
        {
            if (!IsAvailable || _scope is null || _biosMethods is null)
            {
                throw new HpWmiException("Il client HP WMI non è inizializzato.");
            }

            using var inputClass = new ManagementClass(
                _scope,
                new ManagementPath("hpqBDataIn"),
                null);
            using var inputData = inputClass.CreateInstance()
                ?? throw new HpWmiException("Impossibile creare hpqBDataIn.");

            inputData["Sign"] = Signature;
            inputData["Command"] = command;
            inputData["CommandType"] = (uint)commandType;
            inputData["Size"] = (uint)input.Length;
            inputData["hpqBData"] = input.ToArray();

            using var parameters = _biosMethods.GetMethodParameters(
                $"hpqBIOSInt{outputSize}");
            parameters["InData"] = inputData;

            using var response = _biosMethods.InvokeMethod(
                $"hpqBIOSInt{outputSize}",
                parameters,
                null);

            if (response?["OutData"] is not ManagementBaseObject output)
            {
                throw new HpWmiException("Risposta HP WMI non valida.");
            }

            using (output)
            {
                var returnCode = Convert.ToInt32(output["rwReturnCode"]);
                var data = outputSize == 0
                    ? Array.Empty<byte>()
                    : (output["Data"] as byte[] ?? new byte[outputSize]);

                return new HpWmiResult(returnCode, data);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            _disposed = true;
            IsAvailable = false;
            _biosMethods?.Dispose();
            _biosMethods = null;
            _scope = null;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

public sealed class HpWmiException : Exception
{
    public HpWmiException(string message)
        : base(message)
    {
    }

    public HpWmiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
