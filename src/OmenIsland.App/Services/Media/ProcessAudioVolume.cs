using NAudio.CoreAudioApi;

namespace OmenIsland.App.Services.Media;

internal static class ProcessAudioVolume
{
    public static float? TryGet(IReadOnlyCollection<int> processIds)
    {
        float? level = null;
        Visit(processIds, volume =>
        {
            level ??= volume.Volume;
        });
        return level;
    }

    public static void TrySet(IReadOnlyCollection<int> processIds, float level)
    {
        var clamped = Math.Clamp(level, 0f, 1f);
        Visit(processIds, volume => volume.Volume = clamped);
    }

    private static void Visit(IReadOnlyCollection<int> processIds, Action<SimpleAudioVolume> action)
    {
        if (processIds.Count == 0)
        {
            return;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            var ids = processIds as HashSet<int> ?? processIds.ToHashSet();
            for (var index = 0; index < sessions.Count; index++)
            {
                var session = sessions[index];
                try
                {
                    if (!ids.Contains((int)session.GetProcessID))
                    {
                        continue;
                    }

                    action(session.SimpleAudioVolume);
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                }
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
