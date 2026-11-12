using System.Threading.Channels;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace OmenIsland.App.Services.Headphones;

public enum HeadphoneLink
{
    NotConfigured,
    Unavailable,
    Disconnected,
    Connecting,
    Connected,
    Failed
}

public sealed record HeadphoneSnapshot(
    HeadphoneLink Link,
    string DeviceName,
    string Detail,
    BatteryReading? Battery,
    NoiseMode? Noise,
    EqPreset? Eq,
    CustomEq? CustomEq,
    bool? DualConnection,
    SpatialMode? Spatial,
    AudioCodec? Codec,
    bool? LowLatency)
{
    public static HeadphoneSnapshot Empty { get; } =
        new(HeadphoneLink.NotConfigured, "", "", null, null, null, null, null, null, null, null);

    public bool IsConnected => Link == HeadphoneLink.Connected;
}

/// <summary>
/// Talks to CMF Headphone Pro over the Nothing RFCOMM channel. Follows the Bluetooth
/// connection of the selected device and opens the control socket when it connects.
/// </summary>
public sealed class CmfHeadphonesService : IAsyncDisposable
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan BatteryInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15)
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private BluetoothDevice? _device;
    private Session? _session;
    private CancellationTokenSource? _retry;
    private HeadphoneSnapshot _current = HeadphoneSnapshot.Empty;
    private bool _autoConnect = true;
    private bool _userDisconnected;
    private bool _disposed;

    public event EventHandler? Changed;

    public HeadphoneSnapshot Current
    {
        get
        {
            lock (_stateLock)
            {
                return _current;
            }
        }
    }

    public async Task ConfigureAsync(string? deviceId, bool autoConnect)
    {
        await _gate.WaitAsync();
        try
        {
            _autoConnect = autoConnect;
            _userDisconnected = false;
            await CloseSessionAsync();
            DetachDevice();

            if (string.IsNullOrWhiteSpace(deviceId))
            {
                Publish(_ => HeadphoneSnapshot.Empty);
                return;
            }

            try
            {
                _device = await BluetoothDevice.FromIdAsync(deviceId);
            }
            catch (Exception exception)
            {
                Publish(_ => HeadphoneSnapshot.Empty with
                {
                    Link = HeadphoneLink.Unavailable,
                    Detail = exception.Message
                });
                return;
            }

            if (_device is null)
            {
                Publish(_ => HeadphoneSnapshot.Empty with
                {
                    Link = HeadphoneLink.Unavailable,
                    Detail = "Dispositivo non trovato. Controlla che sia ancora associato."
                });
                return;
            }

            _device.ConnectionStatusChanged += Device_ConnectionStatusChanged;
            var name = _device.Name;
            Publish(_ => HeadphoneSnapshot.Empty with
            {
                Link = HeadphoneLink.Disconnected,
                DeviceName = name
            });
        }
        finally
        {
            _gate.Release();
        }

        if (_autoConnect && _device?.ConnectionStatus == BluetoothConnectionStatus.Connected)
        {
            await ConnectAsync();
        }
    }

    public async Task ConnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed || _device is null || _session is not null)
            {
                return;
            }

            _userDisconnected = false;
            CancelRetry();
            await OpenSessionAsync(scheduleRetry: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _userDisconnected = true;
            CancelRetry();
            await CloseSessionAsync();
            Publish(current => DisconnectedFrom(current, ""));
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SetNoiseModeAsync(NoiseMode mode) =>
        SendAsync(NothingProtocol.NoiseSet, NothingProtocol.NoiseSetPayload(mode),
            current => current with { Noise = mode });

    public async Task SetEqAsync(EqPreset preset)
    {
        await SendAsync(NothingProtocol.EqSet, NothingProtocol.EqSetPayload(preset),
            current => current with { Eq = preset });
        if (preset == EqPreset.Custom && _session is { } session)
        {
            // The Nothing X app may have changed the bands since we connected.
            await session.EnqueueAsync(NothingProtocol.CustomEqGet, []);
        }
    }

    public Task SetCustomEqAsync(CustomEq eq) =>
        SendAsync(NothingProtocol.CustomEqSet, NothingProtocol.CustomEqSetPayload(eq),
            current => current with { CustomEq = eq });

    public Task SetDualConnectionAsync(bool enabled) =>
        SendAsync(NothingProtocol.DualConnectionSet, [enabled ? (byte)0x01 : (byte)0x00],
            current => current with { DualConnection = enabled });

    public Task SetSpatialAsync(SpatialMode mode) =>
        SendAsync(NothingProtocol.SpatialSet, NothingProtocol.SpatialSetPayload(mode),
            current => current with { Spatial = mode });

    public Task SetCodecAsync(AudioCodec codec) =>
        SendAsync(NothingProtocol.CodecSet, NothingProtocol.CodecSetPayload(codec),
            current => current with { Codec = codec });

    public async Task SetLowLatencyAsync(bool enabled)
    {
        await SendAsync(NothingProtocol.LowLatencySet, NothingProtocol.LowLatencySetPayload(enabled),
            current => current with { LowLatency = enabled });
        if (_session is { } session)
        {
            // The firmware may switch codec along with the latency mode.
            await session.EnqueueAsync(NothingProtocol.CodecGet, []);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _disposed = true;
            CancelRetry();
            await CloseSessionAsync();
            DetachDevice();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SendAsync(
        ushort command,
        byte[] payload,
        Func<HeadphoneSnapshot, HeadphoneSnapshot> optimistic)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        Publish(optimistic);
        await session.EnqueueAsync(command, payload);
    }

    private async void Device_ConnectionStatusChanged(BluetoothDevice sender, object args)
    {
        try
        {
            if (sender.ConnectionStatus == BluetoothConnectionStatus.Connected)
            {
                if (!_autoConnect)
                {
                    return;
                }

                // The audio profiles come up first; give the control channel a moment.
                await Task.Delay(1500);
                await _gate.WaitAsync();
                try
                {
                    if (_disposed || _session is not null || _device != sender)
                    {
                        return;
                    }

                    _userDisconnected = false;
                    CancelRetry();
                    await OpenSessionAsync(scheduleRetry: true);
                }
                finally
                {
                    _gate.Release();
                }

                return;
            }

            await _gate.WaitAsync();
            try
            {
                if (_device != sender)
                {
                    return;
                }

                CancelRetry();
                await CloseSessionAsync();
                Publish(current => DisconnectedFrom(current, ""));
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // Caller holds _gate.
    private async Task OpenSessionAsync(bool scheduleRetry, int attempt = 0)
    {
        var device = _device;
        if (device is null)
        {
            return;
        }

        Publish(current => current with
        {
            Link = HeadphoneLink.Connecting,
            DeviceName = device.Name,
            Detail = ""
        });

        try
        {
            var service = await FindControlServiceAsync(device);
            if (service is null)
            {
                throw new InvalidOperationException(
                    device.ConnectionStatus == BluetoothConnectionStatus.Connected
                        ? "Canale di controllo non disponibile su queste cuffie."
                        : "Cuffie spente o non connesse al PC.");
            }

            var socket = new StreamSocket();
            try
            {
                await socket.ConnectAsync(
                    service.ConnectionHostName,
                    service.ConnectionServiceName,
                    SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            var session = new Session(socket, OnFrame, OnSessionEnded);
            _session = session;
            Publish(current => current with
            {
                Link = HeadphoneLink.Connected,
                DeviceName = device.Name,
                Detail = ""
            });
            session.Start();
            await session.EnqueueAsync(NothingProtocol.ProtocolGet, []);
            await session.EnqueueAsync(NothingProtocol.ProtocolSet, []);
            await RequestAllAsync(session);
        }
        catch (Exception exception)
        {
            await CloseSessionAsync();
            var detail = DescribeFailure(exception);
            Publish(current => current with
            {
                Link = HeadphoneLink.Failed,
                Detail = detail
            });

            if (scheduleRetry &&
                attempt < RetryDelays.Length &&
                device.ConnectionStatus == BluetoothConnectionStatus.Connected)
            {
                ScheduleRetry(attempt);
            }
        }
    }

    private static async Task<RfcommDeviceService?> FindControlServiceAsync(BluetoothDevice device)
    {
        var serviceId = RfcommServiceId.FromUuid(NothingProtocol.ServiceUuid);
        foreach (var mode in new[] { BluetoothCacheMode.Uncached, BluetoothCacheMode.Cached })
        {
            var result = await device.GetRfcommServicesForIdAsync(serviceId, mode);
            if (result.Error == BluetoothError.Success && result.Services.Count > 0)
            {
                return result.Services[0];
            }
        }

        return null;
    }

    private static async Task RequestAllAsync(Session session)
    {
        await session.EnqueueAsync(NothingProtocol.BatteryGet, []);
        await session.EnqueueAsync(NothingProtocol.NoiseGet, []);
        await session.EnqueueAsync(NothingProtocol.EqGet, []);
        await session.EnqueueAsync(NothingProtocol.CustomEqGet, []);
        await session.EnqueueAsync(NothingProtocol.DualConnectionGet, []);
        await session.EnqueueAsync(NothingProtocol.SpatialGet, []);
        await session.EnqueueAsync(NothingProtocol.CodecGet, []);
        await session.EnqueueAsync(NothingProtocol.LowLatencyGet, []);
    }

    private void ScheduleRetry(int attempt)
    {
        CancelRetry();
        var retry = new CancellationTokenSource();
        _retry = retry;
        _ = RetryAsync(attempt, retry.Token);
    }

    private async Task RetryAsync(int attempt, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(RetryDelays[attempt], cancellationToken);
            await _gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (!cancellationToken.IsCancellationRequested &&
                !_disposed &&
                !_userDisconnected &&
                _session is null)
            {
                await OpenSessionAsync(scheduleRetry: true, attempt + 1);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void CancelRetry()
    {
        _retry?.Cancel();
        _retry?.Dispose();
        _retry = null;
    }

    private void OnSessionEnded(Session session)
    {
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                if (_session != session)
                {
                    return;
                }

                await CloseSessionAsync();
                Publish(current => DisconnectedFrom(current, ""));
                if (!_disposed &&
                    !_userDisconnected &&
                    _autoConnect &&
                    _device?.ConnectionStatus == BluetoothConnectionStatus.Connected)
                {
                    ScheduleRetry(0);
                }
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    private void OnFrame(NothingFrame frame)
    {
        var payload = frame.Payload;
        switch (frame.Command)
        {
            case NothingProtocol.BatteryNotify or NothingProtocol.BatteryReturn:
                if (NothingProtocol.ParseBattery(payload) is { } battery)
                {
                    Publish(current => current with { Battery = battery });
                }

                break;
            case NothingProtocol.NoiseNotify or NothingProtocol.NoiseNotifyAlt or NothingProtocol.NoiseReturn:
                if (NothingProtocol.ParseNoise(payload) is { } noise)
                {
                    Publish(current => current with { Noise = noise });
                }

                break;
            case NothingProtocol.EqReturn:
                if (NothingProtocol.ParseEq(payload) is { } eq)
                {
                    Publish(current => current with { Eq = eq });
                }

                break;
            case NothingProtocol.CustomEqReturn:
                if (NothingProtocol.ParseCustomEq(payload) is { } customEq)
                {
                    Publish(current => current with { CustomEq = customEq });
                }

                break;
            case NothingProtocol.DualConnectionReturn:
                if (NothingProtocol.ParseFlag(payload) is { } dual)
                {
                    Publish(current => current with { DualConnection = dual });
                }

                break;
            case NothingProtocol.SpatialReturn:
                if (NothingProtocol.ParseSpatial(payload) is { } spatial)
                {
                    Publish(current => current with { Spatial = spatial });
                }

                break;
            case NothingProtocol.CodecReturn:
                if (NothingProtocol.ParseCodec(payload) is { } codec)
                {
                    Publish(current => current with { Codec = codec });
                }

                break;
            case NothingProtocol.LowLatencyReturn:
                if (NothingProtocol.ParseLowLatency(payload) is { } lowLatency)
                {
                    Publish(current => current with { LowLatency = lowLatency });
                }

                break;
        }
    }

    // Caller holds _gate.
    private async Task CloseSessionAsync()
    {
        var session = _session;
        _session = null;
        if (session is not null)
        {
            await session.DisposeAsync();
        }
    }

    private void DetachDevice()
    {
        if (_device is null)
        {
            return;
        }

        _device.ConnectionStatusChanged -= Device_ConnectionStatusChanged;
        _device.Dispose();
        _device = null;
    }

    private static HeadphoneSnapshot DisconnectedFrom(HeadphoneSnapshot current, string detail) =>
        HeadphoneSnapshot.Empty with
        {
            Link = string.IsNullOrEmpty(current.DeviceName)
                ? HeadphoneLink.NotConfigured
                : HeadphoneLink.Disconnected,
            DeviceName = current.DeviceName,
            Detail = detail
        };

    private static string DescribeFailure(Exception exception)
    {
        // 0x80072740 / WSAEADDRINUSE-style errors: another app holds the channel.
        var code = (uint)exception.HResult;
        return code switch
        {
            0x80072740 or 0x8007274D => "Canale occupato: chiudi Nothing X o altre app collegate alle cuffie.",
            0x8007274C or 0x80070079 => "Le cuffie non rispondono. Riprova tra qualche secondo.",
            0x80070005 => "Windows ha negato l'accesso al Bluetooth.",
            _ => exception.Message
        };
    }

    private void Publish(Func<HeadphoneSnapshot, HeadphoneSnapshot> update)
    {
        lock (_stateLock)
        {
            var next = update(_current);
            if (next == _current)
            {
                return;
            }

            _current = next;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly StreamSocket _socket;
        private readonly Action<NothingFrame> _onFrame;
        private readonly Action<Session> _onEnded;
        private readonly Channel<(ushort Command, byte[] Payload)> _outgoing =
            Channel.CreateUnbounded<(ushort, byte[])>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource _stop = new();
        private readonly NothingFrameReader _reader = new();
        private TaskCompletionSource<bool>? _pending;
        private byte _pendingSequence;
        private byte _sequence;
        private Task? _readTask;
        private Task? _writeTask;
        private Task? _batteryTask;
        private int _ended;

        public Session(StreamSocket socket, Action<NothingFrame> onFrame, Action<Session> onEnded)
        {
            _socket = socket;
            _onFrame = onFrame;
            _onEnded = onEnded;
        }

        public void Start()
        {
            HeadphoneTrafficLog.Write("session start");
            _readTask = Task.Run(ReadLoopAsync);
            _writeTask = Task.Run(WriteLoopAsync);
            _batteryTask = Task.Run(BatteryLoopAsync);
        }

        public ValueTask EnqueueAsync(ushort command, byte[] payload) =>
            _outgoing.Writer.WriteAsync((command, payload));

        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _ended, 1);
            _outgoing.Writer.TryComplete();
            _stop.Cancel();
            _socket.Dispose();
            foreach (var task in new[] { _readTask, _writeTask, _batteryTask })
            {
                if (task is null)
                {
                    continue;
                }

                try
                {
                    await task;
                }
                catch
                {
                    // The socket is gone; loop errors no longer matter.
                }
            }

            _stop.Dispose();
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                using var reader = new DataReader(_socket.InputStream)
                {
                    InputStreamOptions = InputStreamOptions.Partial
                };
                while (!_stop.IsCancellationRequested)
                {
                    var loaded = await reader.LoadAsync(1024).AsTask(_stop.Token);
                    if (loaded == 0)
                    {
                        break;
                    }

                    var buffer = new byte[loaded];
                    reader.ReadBytes(buffer);
                    _reader.Append(buffer);
                    while (_reader.TryRead(out var frame))
                    {
                        HeadphoneTrafficLog.Frame("RX", frame.Command, frame.Sequence, frame.Payload);
                        if (_pending is { } pending && frame.Sequence == _pendingSequence)
                        {
                            pending.TrySetResult(true);
                        }

                        _onFrame(frame);
                    }
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Connection dropped; report below.
            }

            End();
        }

        private async Task WriteLoopAsync()
        {
            try
            {
                using var writer = new DataWriter(_socket.OutputStream);
                await foreach (var (command, payload) in _outgoing.Reader.ReadAllAsync(_stop.Token))
                {
                    _sequence = _sequence >= 250 ? (byte)1 : (byte)(_sequence + 1);
                    var pending = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _pendingSequence = _sequence;
                    _pending = pending;

                    HeadphoneTrafficLog.Frame("TX", command, _sequence, payload);
                    writer.WriteBytes(NothingProtocol.Encode(command, payload, _sequence));
                    await writer.StoreAsync().AsTask(_stop.Token);

                    // One request at a time: wait for the matching reply or move on.
                    await Task.WhenAny(pending.Task, Task.Delay(ResponseTimeout, _stop.Token));
                    if (!pending.Task.IsCompleted)
                    {
                        HeadphoneTrafficLog.Write($"no reply to cmd=0x{command:X4} seq={_sequence}");
                    }

                    _pending = null;
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                End();
            }
        }

        private async Task BatteryLoopAsync()
        {
            using var timer = new PeriodicTimer(BatteryInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(_stop.Token))
                {
                    await EnqueueAsync(NothingProtocol.BatteryGet, []);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ChannelClosedException)
            {
            }
        }

        private void End()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                _onEnded(this);
            }
        }
    }
}
