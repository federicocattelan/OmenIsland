using OmenIsland.App.Services.Media;
using Xunit;

namespace OmenIsland.Tests;

public sealed class ProcessLoopbackTests
{
    [Fact]
    public void ResolveActiveProcessIdReturnsZeroForEmptyList()
    {
        var resolved = ProcessLoopbackCapture.ResolveActiveProcessId([]);
        Assert.Equal(0, resolved);
    }

    [Fact]
    public void TestStaCaptureFromActiveProcess()
    {
        var procs = System.Diagnostics.Process.GetProcessesByName("spotify");
        if (procs.Length == 0)
        {
            return;
        }

        var pid = ProcessLoopbackCapture.ResolveActiveProcessId(procs.Select(p => p.Id).ToArray());
        Exception? threadEx = null;
        Exception? stopEx = null;
        int packetCount = 0;
        var thread = new Thread(() =>
        {
            try
            {
                using var capture = new ProcessLoopbackCapture(pid);
                capture.RecordingStopped += (_, e) => stopEx = e.Exception;
                capture.DataAvailable += (_, e) => Interlocked.Increment(ref packetCount);
                capture.StartRecording();
                Thread.Sleep(200);
                capture.StopRecording();
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(threadEx);
        Assert.Null(stopEx);
    }

    [Fact]
    public void TestMtaCaptureFromActiveProcess()
    {
        var procs = System.Diagnostics.Process.GetProcessesByName("spotify");
        if (procs.Length == 0)
        {
            return;
        }

        var pid = ProcessLoopbackCapture.ResolveActiveProcessId(procs.Select(p => p.Id).ToArray());
        Exception? threadEx = null;
        Exception? stopEx = null;
        int packetCount = 0;
        var thread = new Thread(() =>
        {
            try
            {
                using var capture = new ProcessLoopbackCapture(pid);
                capture.RecordingStopped += (_, e) => stopEx = e.Exception;
                capture.DataAvailable += (_, e) => Interlocked.Increment(ref packetCount);
                capture.StartRecording();
                Thread.Sleep(200);
                capture.StopRecording();
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        Assert.Null(threadEx);
        Assert.Null(stopEx);
    }

    [Fact]
    public void ResolveActiveProcessIdReturnsSinglePidDirectly()
    {
        var resolved = ProcessLoopbackCapture.ResolveActiveProcessId([12345]);
        Assert.Equal(12345, resolved);
    }

    [Fact]
    public void CanInstantiateAndStartStopOnCurrentProcess()
    {
        using var capture = new ProcessLoopbackCapture(Environment.ProcessId);
        Assert.Equal(Environment.ProcessId, capture.TargetProcessId);
        Assert.NotNull(capture.WaveFormat);

        capture.StartRecording();
        // Give it a brief moment
        Thread.Sleep(50);
        capture.StopRecording();
    }

    [Fact]
    public void ZeroPidStaysSilentAndDoesNotThrow()
    {
        using var capture = new ProcessLoopbackCapture(0);
        Assert.Equal(0, capture.TargetProcessId);

        // Starting with PID 0 should be a no-op (no capture thread, no error)
        capture.StartRecording();
        capture.StopRecording();
    }

    [Fact]
    public async Task CanCaptureFromActiveMediaAppIfRunning()
    {
        var manager = await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var session = manager.GetCurrentSession();
        if (session == null)
        {
            var sessions = manager.GetSessions();
            session = sessions.FirstOrDefault(s => s.SourceAppUserModelId.Contains("spotify", StringComparison.OrdinalIgnoreCase));
        }

        if (session != null)
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("spotify");
            if (procs.Length > 0)
            {
                var pid = ProcessLoopbackCapture.ResolveActiveProcessId(procs.Select(p => p.Id).ToArray());
                using var capture = new ProcessLoopbackCapture(pid);
                capture.StartRecording();
                await Task.Delay(100);
                capture.StopRecording();
            }
        }
    }
}
