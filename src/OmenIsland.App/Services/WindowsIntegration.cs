using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace OmenIsland.App.Services;

public static class WindowsIntegration
{
    public const string BackgroundArgument = "--background";
    public const string StartElevatedArgument = "--start-elevated";
    public const string AppUserModelId = "OmenIsland.App";
    private const string StartupTaskName = "Omen Island";
    private const string RunValueName = "Omen Island";

    public static void EnsureStartMenuShortcut()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return;
        }

        var shortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "Omen Island.lnk");
        CreateShortcut(shortcutPath, executable, string.Empty);
    }

    public static void SetRunAtStartup(bool enabled)
    {
        if (!enabled)
        {
            RemoveRunKey();
            TryDeleteStartupTask();
            return;
        }

        if (IsProcessAdministrator() && TryRegisterElevatedStartupTask())
        {
            SetRunKey(StartElevatedArgument);
            return;
        }

        SetRunKey(StartupTaskExists() ? StartElevatedArgument : BackgroundArgument);
    }

    public static bool TryRunStartupTask()
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (RunStartupTaskOnce(out var alreadyRunning) || alreadyRunning)
            {
                return true;
            }

            Thread.Sleep(400);
        }

        return false;
    }

    public static void RestartElevated()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas"
        });
    }

    private static void SetRunKey(string arguments)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            writable: true);
        key?.SetValue(RunValueName, $"\"{executable}\" {arguments}");

        using var approval = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
        approval?.SetValue(
            RunValueName,
            new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            RegistryValueKind.Binary);
    }

    private static bool StartupTaskExists()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(
                "schtasks.exe",
                "/Query /TN \"Omen Island\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null || !process.WaitForExit(3000))
            {
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool RunStartupTaskOnce(out bool alreadyRunning)
    {
        alreadyRunning = false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(
                "schtasks.exe",
                "/Run /TN \"Omen Island\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // The launcher can already have exited.
                }

                return false;
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (process.ExitCode == 0)
            {
                return true;
            }

            alreadyRunning = output.Contains("running", StringComparison.OrdinalIgnoreCase)
                || output.Contains("esecuzione", StringComparison.OrdinalIgnoreCase);
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void RemoveRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            writable: true);
        key?.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    private static bool TryRegisterElevatedStartupTask()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        try
        {
            var schedulerType = Type.GetTypeFromProgID("Schedule.Service");
            if (schedulerType is null)
            {
                return false;
            }

            dynamic scheduler = Activator.CreateInstance(schedulerType)
                ?? throw new InvalidOperationException("Utilità di pianificazione non disponibile.");
            scheduler.Connect();
            dynamic folder = scheduler.GetFolder("\\");
            dynamic task = scheduler.NewTask(0);
            task.RegistrationInfo.Description =
                "Avvia Omen Island con accesso a profili, ventole e temperature.";
            task.Principal.RunLevel = 1;
            task.Principal.LogonType = 3;
            task.Settings.Enabled = true;
            task.Settings.StartWhenAvailable = true;
            task.Settings.DisallowStartIfOnBatteries = false;
            task.Settings.StopIfGoingOnBatteries = false;
            task.Settings.AllowDemandStart = true;
            task.Settings.ExecutionTimeLimit = "PT0S";
            task.Settings.MultipleInstances = 2;
            task.Settings.Priority = 4;

            dynamic trigger = task.Triggers.Create(9);
            trigger.UserId = WindowsIdentity.GetCurrent().Name;

            dynamic action = task.Actions.Create(0);
            action.Path = executable;
            action.Arguments = BackgroundArgument;

            folder.RegisterTaskDefinition(
                StartupTaskName,
                task,
                6,
                null,
                null,
                3);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteStartupTask()
    {
        try
        {
            var schedulerType = Type.GetTypeFromProgID("Schedule.Service");
            if (schedulerType is null)
            {
                return;
            }

            dynamic scheduler = Activator.CreateInstance(schedulerType)
                ?? throw new InvalidOperationException("Utilità di pianificazione non disponibile.");
            scheduler.Connect();
            dynamic folder = scheduler.GetFolder("\\");
            folder.DeleteTask(StartupTaskName, 0);
        }
        catch
        {
            // The task is absent, or removing it needs the same permission used to create it.
        }
    }

    public static bool IsProcessAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity)
            .IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void CreateShortcut(
        string shortcutPath,
        string targetPath,
        string arguments)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException(
                "Impossibile creare il collegamento nel menu Start.");
        dynamic shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException(
                "Impossibile creare il collegamento nel menu Start.");

        try
        {
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.Arguments = arguments;
            shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
            shortcut.IconLocation = targetPath + ",0";
            shortcut.Description = "Omen Island";
            shortcut.Save();
        }
        finally
        {
            if (shell is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
