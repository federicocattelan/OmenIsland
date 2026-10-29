using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace OmenIsland.App.Services.Media;

public sealed class ProcessLoopbackCapture : IWaveIn
{
    private const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
    private static readonly Guid IidIAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    private readonly object _lock = new();
    private readonly ManualResetEvent _stopEvent = new(false);
    private EventWaitHandle? _sampleReadyEvent;
    private AudioClient? _audioClient;
    private AudioCaptureClient? _audioCaptureClient;
    private Thread? _captureThread;
    private byte[] _recordBuffer = [];
    private bool _isDisposed;

    public int TargetProcessId { get; }

    public WaveFormat WaveFormat { get; set; }

    public event EventHandler<WaveInEventArgs>? DataAvailable;

    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public ProcessLoopbackCapture(int targetProcessId, WaveFormat? waveFormat = null)
    {
        TargetProcessId = targetProcessId;
        WaveFormat = waveFormat ?? WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    }

    public static int ResolveActiveProcessId(IReadOnlyCollection<int> processIds)
    {
        if (processIds.Count == 0)
        {
            return 0;
        }

        if (processIds.Count == 1)
        {
            return processIds.First();
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            var set = processIds as HashSet<int> ?? processIds.ToHashSet();
            int candidatePid = 0;
            for (var index = 0; index < sessions.Count; index++)
            {
                var session = sessions[index];
                try
                {
                    var pid = (int)session.GetProcessID;
                    if (set.Contains(pid))
                    {
                        if (session.State == AudioSessionState.AudioSessionStateActive)
                        {
                            return pid;
                        }

                        if (candidatePid == 0)
                        {
                            candidatePid = pid;
                        }
                    }
                }
                catch
                {
                }
            }

            if (candidatePid > 0)
            {
                return candidatePid;
            }
        }
        catch
        {
        }

        return processIds.First();
    }

    public void StartRecording()
    {
        lock (_lock)
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(ProcessLoopbackCapture));
            }

            if (_captureThread != null)
            {
                return;
            }

            _stopEvent.Reset();

            if (TargetProcessId <= 0)
            {
                // No target media process active: stay silent, do not record system audio.
                return;
            }

            _captureThread = new Thread(CaptureThreadProc)
            {
                IsBackground = true,
                Name = $"ProcessLoopbackCapture-{TargetProcessId}"
            };
            _captureThread.SetApartmentState(ApartmentState.MTA);
            _captureThread.Start();
        }
    }

    public void StopRecording()
    {
        Thread? threadToJoin = null;
        lock (_lock)
        {
            if (_captureThread == null)
            {
                return;
            }

            _stopEvent.Set();
            threadToJoin = _captureThread;
            _captureThread = null;
        }

        if (threadToJoin != null && threadToJoin.IsAlive && threadToJoin != Thread.CurrentThread)
        {
            threadToJoin.Join(600);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }

        StopRecording();
        _stopEvent.Dispose();
    }

    private void CaptureThreadProc()
    {
        Exception? error = null;
        try
        {
            var rawClient = ActivateClient(TargetProcessId, TimeSpan.FromMilliseconds(2000));
            if (rawClient == null)
            {
                return;
            }

            _audioClient = new AudioClient(rawClient);
            var flags = AudioClientStreamFlags.Loopback |
                        AudioClientStreamFlags.EventCallback |
                        AudioClientStreamFlags.AutoConvertPcm;

            _audioClient.Initialize(AudioClientShareMode.Shared, flags, 0, 0, WaveFormat, Guid.Empty);
            _audioCaptureClient = _audioClient.AudioCaptureClient;
            _sampleReadyEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
            _audioClient.SetEventHandle(_sampleReadyEvent.SafeWaitHandle.DangerousGetHandle());
            _audioClient.Start();

            var sampleEvent = _sampleReadyEvent;
            if (sampleEvent == null)
            {
                return;
            }

            var waitHandles = new WaitHandle[] { _stopEvent, sampleEvent };
            var bytesPerFrame = WaveFormat.BlockAlign;

            while (!_stopEvent.WaitOne(0))
            {
                var waitIndex = WaitHandle.WaitAny(waitHandles, 20);
                if (waitIndex == 0) // StopEvent
                {
                    break;
                }

                var captureClient = _audioCaptureClient;
                if (captureClient == null)
                {
                    break;
                }

                try
                {
                    var nextPacketSize = captureClient.GetNextPacketSize();
                    while (nextPacketSize > 0)
                    {
                        var dataPtr = captureClient.GetBuffer(out int numFramesToRead, out AudioClientBufferFlags bufferFlags);
                        if (numFramesToRead > 0)
                        {
                            var bytesToRead = numFramesToRead * bytesPerFrame;
                            if (_recordBuffer.Length < bytesToRead)
                            {
                                _recordBuffer = new byte[bytesToRead];
                            }

                            if ((bufferFlags & AudioClientBufferFlags.Silent) != 0)
                            {
                                Array.Clear(_recordBuffer, 0, bytesToRead);
                            }
                            else
                            {
                                Marshal.Copy(dataPtr, _recordBuffer, 0, bytesToRead);
                            }

                            DataAvailable?.Invoke(this, new WaveInEventArgs(_recordBuffer, bytesToRead));
                        }

                        captureClient.ReleaseBuffer(numFramesToRead);
                        nextPacketSize = captureClient.GetNextPacketSize();
                    }
                }
                catch (COMException comEx) when ((uint)comEx.ErrorCode == 0x88890004) // AUDCLNT_E_DEVICE_INVALIDATED
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            try
            {
                _audioClient?.Stop();
            }
            catch
            {
            }

            CleanupClient();
            RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
        }
    }

    private void CleanupClient()
    {
        try
        {
            _audioCaptureClient?.Dispose();
        }
        catch
        {
        }
        _audioCaptureClient = null;

        try
        {
            _audioClient?.Dispose();
        }
        catch
        {
        }
        _audioClient = null;

        try
        {
            _sampleReadyEvent?.Dispose();
        }
        catch
        {
        }
        _sampleReadyEvent = null;
    }

    private static IAudioClient ActivateClient(int processId, TimeSpan timeout)
    {
        var clientParams = new AudioClientActivationParams
        {
            ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            ProcessLoopbackParams = new AudioClientProcessLoopbackParams
            {
                TargetProcessId = (uint)processId,
                ProcessLoopbackMode = 0 // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
            }
        };

        var clientParamsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        var propVariantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariant>());
        try
        {
            Marshal.StructureToPtr(clientParams, clientParamsPtr, false);
            var propVariant = new PropVariant
            {
                vt = 65, // VT_BLOB
                blob = new Blob
                {
                    cbSize = Marshal.SizeOf<AudioClientActivationParams>(),
                    pBlobData = clientParamsPtr
                }
            };
            Marshal.StructureToPtr(propVariant, propVariantPtr, false);

            var handler = new CompletionHandler();
            var riid = IidIAudioClient;
            var hr = ActivateAudioInterfaceAsync(
                VirtualAudioDeviceProcessLoopback,
                ref riid,
                propVariantPtr,
                handler,
                out _);

            if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            if (!handler.Task.Wait(timeout))
            {
                throw new TimeoutException($"Activation for PID {processId} timed out after {timeout.TotalMilliseconds}ms");
            }

            return handler.Task.Result;
        }
        finally
        {
            Marshal.FreeHGlobal(propVariantPtr);
            Marshal.FreeHGlobal(clientParamsPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientProcessLoopbackParams
    {
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public AudioClientProcessLoopbackParams ProcessLoopbackParams;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int cbSize;
        public IntPtr pBlobData;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public Blob blob;
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig]
        int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig]
        int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
    }

    [ComImport]
    [Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject
    {
    }

    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IAudioClient> Task => _tcs.Task;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                var hr = activateOperation.GetActivateResult(out var resultHr, out var activatedInterface);
                if (hr != 0)
                {
                    _tcs.TrySetException(new COMException("GetActivateResult failed", hr));
                }
                else if (resultHr != 0)
                {
                    _tcs.TrySetException(new COMException("Activation failed", resultHr));
                }
                else if (activatedInterface is IAudioClient audioClient)
                {
                    _tcs.TrySetResult(audioClient);
                }
                else
                {
                    _tcs.TrySetException(new InvalidCastException("Activated interface is not IAudioClient"));
                }
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }

            return 0;
        }
    }

    [DllImport("mmdevapi.dll", ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [In] ref Guid riid,
        [In] IntPtr activationParams,
        [In] IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation? activationOperation);
}
