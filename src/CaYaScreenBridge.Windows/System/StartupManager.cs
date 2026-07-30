using System.Diagnostics;
using CaYaScreenBridge.Core.Diagnostics;
using Microsoft.Win32;

namespace CaYaScreenBridge.Windows.System;

public enum StartupMethod
{
    None = 0,

    /// <summary>Elevated scheduled task triggered at logon. No UAC prompt, works over games.</summary>
    ScheduledTask = 1,

    /// <summary>HKCU Run key. Standard user rights only.</summary>
    RunKey = 2,
}

public sealed record StartupStatus(StartupMethod Method, bool Registered, string? TargetPath, string? Detail);

/// <summary>
/// Registers the application to start with Windows, and repairs the registration when it drifts.
///
/// The elevated scheduled task is the preferred route, and not for its own sake: a low level mouse
/// hook installed by a standard user process is ignored while an elevated window has focus, so
/// without it the cursor correction would silently stop working over Task Manager, an installer, or
/// any game launched with administrator rights. A logon triggered task with
/// <c>HighestAvailable</c> gets those rights without showing a UAC prompt at every sign in.
///
/// Three layers of fallback, because "it did not start this time" is the failure that makes a tool
/// like this untrustworthy: the Task Scheduler COM API, then the <c>schtasks</c> command line, then
/// the plain Run key.
/// </summary>
public sealed class StartupManager
{
    public const string TaskName = "CaYaScreenBridge";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "CaYaScreenBridge";
    private const string BackgroundArgument = "--background";

    // Task Scheduler COM constants.
    private const int TaskTriggerLogon = 9;
    private const int TaskActionExec = 0;
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelHighest = 1;
    private const int TaskInstancesIgnoreNew = 2;

    private readonly ILogSink _log;

    public StartupManager(ILogSink log) => _log = log;

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CaYaScreenBridge.exe");

    public StartupStatus GetStatus()
    {
        (bool taskExists, string? taskTarget) = TryReadScheduledTask();
        if (taskExists)
        {
            return new StartupStatus(StartupMethod.ScheduledTask, true, taskTarget, null);
        }

        string? runValue = TryReadRunKey();
        if (runValue is not null)
        {
            return new StartupStatus(StartupMethod.RunKey, true, runValue, null);
        }

        return new StartupStatus(StartupMethod.None, false, null, null);
    }

    /// <summary>
    /// Brings the registration in line with the requested settings, and corrects a registration that
    /// points at an old location after the application was moved or updated.
    /// </summary>
    public StartupStatus Apply(bool enabled, bool elevated)
    {
        if (!enabled)
        {
            RemoveScheduledTask();
            RemoveRunKey();
            _log.Info("Startup", "Start with Windows disabled.");
            return new StartupStatus(StartupMethod.None, false, null, null);
        }

        string exePath = ExecutablePath;

        if (elevated)
        {
            RemoveRunKey();

            if (TryRegisterScheduledTaskViaCom(exePath, out string? comError))
            {
                _log.Info("Startup", "Registered the elevated logon task.");
                return new StartupStatus(StartupMethod.ScheduledTask, true, exePath, null);
            }

            _log.Warn("Startup", $"Task Scheduler COM registration failed: {comError}");

            if (TryRegisterScheduledTaskViaCommandLine(exePath, out string? cliError))
            {
                _log.Info("Startup", "Registered the elevated logon task through schtasks.");
                return new StartupStatus(StartupMethod.ScheduledTask, true, exePath, "schtasks fallback");
            }

            _log.Warn(
                "Startup",
                $"schtasks registration failed as well ({cliError}); falling back to the Run key. " +
                "Correction will pause while an elevated window has focus.");
        }
        else
        {
            RemoveScheduledTask();
        }

        if (TryRegisterRunKey(exePath, out string? runError))
        {
            return new StartupStatus(
                StartupMethod.RunKey,
                true,
                exePath,
                elevated ? "Elevated registration was not possible." : null);
        }

        _log.Error("Startup", $"Could not register any startup entry: {runError}");
        return new StartupStatus(StartupMethod.None, false, null, runError);
    }

    /// <summary>
    /// Called on every start. If the stored target no longer matches this executable (the folder was
    /// renamed, the application was updated into a versioned directory), the entry is rewritten
    /// rather than left pointing at something that no longer exists.
    /// </summary>
    public void RepairIfNeeded(bool enabled, bool elevated)
    {
        try
        {
            StartupStatus status = GetStatus();

            if (!enabled)
            {
                if (status.Registered)
                {
                    Apply(false, elevated);
                }

                return;
            }

            bool methodMatches = status.Method == (elevated ? StartupMethod.ScheduledTask : StartupMethod.RunKey);
            bool pathMatches = status.TargetPath is not null &&
                               status.TargetPath.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase);

            if (status.Registered && methodMatches && pathMatches)
            {
                return;
            }

            _log.Info("Startup", "The startup entry was missing or stale; rewriting it.");
            Apply(true, elevated);
        }
        catch (Exception ex)
        {
            _log.Warn("Startup", $"Startup repair failed: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------------------------
    // Scheduled task
    // -------------------------------------------------------------------------------------------

    private (bool Exists, string? Target) TryReadScheduledTask()
    {
        try
        {
            dynamic? service = CreateTaskService();
            if (service is null)
            {
                return (false, null);
            }

            dynamic folder = service.GetFolder("\\");
            dynamic task = folder.GetTask(TaskName);
            dynamic actions = task.Definition.Actions;

            string? path = actions.Count >= 1 ? (string)actions[1].Path : null;
            return (true, path);
        }
        catch
        {
            // GetTask throws when the task does not exist, which is the common case.
            return (false, null);
        }
    }

    private bool TryRegisterScheduledTaskViaCom(string exePath, out string? error)
    {
        error = null;

        try
        {
            dynamic? service = CreateTaskService();
            if (service is null)
            {
                error = "The Task Scheduler service is unavailable.";
                return false;
            }

            string userId = $"{Environment.UserDomainName}\\{Environment.UserName}";

            dynamic folder = service.GetFolder("\\");
            dynamic definition = service.NewTask(0);

            definition.RegistrationInfo.Description =
                "Keeps the mouse cursor aligned across displays with different DPI.";
            definition.RegistrationInfo.Author = "CaYaDev";

            definition.Principal.UserId = userId;
            definition.Principal.LogonType = TaskLogonInteractiveToken;
            definition.Principal.RunLevel = TaskRunLevelHighest;

            dynamic settings = definition.Settings;
            settings.Enabled = true;
            settings.Hidden = false;
            settings.StartWhenAvailable = true;
            settings.AllowDemandStart = true;
            settings.AllowHardTerminate = true;
            settings.MultipleInstances = TaskInstancesIgnoreNew;

            // The defaults here are tuned for maintenance jobs and would stop the application on a
            // laptop, or kill it after three days.
            settings.DisallowStartIfOnBatteries = false;
            settings.StopIfGoingOnBatteries = false;
            settings.RunOnlyIfIdle = false;
            settings.ExecutionTimeLimit = "PT0S";
            settings.IdleSettings.StopOnIdleEnd = false;
            settings.RestartCount = 3;
            settings.RestartInterval = "PT1M";

            dynamic trigger = definition.Triggers.Create(TaskTriggerLogon);
            trigger.UserId = userId;

            // A short delay lets the shell and the display drivers settle before the layout is read.
            trigger.Delay = "PT8S";
            trigger.Enabled = true;

            dynamic action = definition.Actions.Create(TaskActionExec);
            action.Path = exePath;
            action.Arguments = BackgroundArgument;
            action.WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;

            folder.RegisterTaskDefinition(
                TaskName,
                definition,
                TaskCreateOrUpdate,
                null,
                null,
                TaskLogonInteractiveToken,
                null);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static dynamic? CreateTaskService()
    {
        Type? type = Type.GetTypeFromProgID("Schedule.Service");
        if (type is null)
        {
            return null;
        }

        dynamic? service = Activator.CreateInstance(type);
        service?.Connect();
        return service;
    }

    private bool TryRegisterScheduledTaskViaCommandLine(string exePath, out string? error)
    {
        // schtasks cannot express every setting the COM path uses, but it does produce a working
        // elevated logon task, which is the part that matters.
        string arguments =
            $"/Create /F /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST " +
            $"/TR \"\\\"{exePath}\\\" {BackgroundArgument}\"";

        return RunSchTasks(arguments, out error);
    }

    private void RemoveScheduledTask()
    {
        try
        {
            dynamic? service = CreateTaskService();
            if (service is not null)
            {
                dynamic folder = service.GetFolder("\\");
                folder.DeleteTask(TaskName, 0);
                return;
            }
        }
        catch
        {
            // Falls through to the command line removal below.
        }

        RunSchTasks($"/Delete /F /TN \"{TaskName}\"", out _);
    }

    private bool RunSchTasks(string arguments, out string? error)
    {
        error = null;

        try
        {
            var info = new ProcessStartInfo("schtasks.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using Process? process = Process.Start(info);
            if (process is null)
            {
                error = "schtasks.exe could not be started.";
                return false;
            }

            string stdErr = process.StandardError.ReadToEnd();
            process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(15_000))
            {
                error = "schtasks.exe did not finish in time.";
                return false;
            }

            if (process.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(stdErr) ? $"exit code {process.ExitCode}" : stdErr.Trim();
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // -------------------------------------------------------------------------------------------
    // Run key
    // -------------------------------------------------------------------------------------------

    private static string? TryReadRunKey()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) as string;
        }
        catch
        {
            return null;
        }
    }

    private bool TryRegisterRunKey(string exePath, out string? error)
    {
        error = null;

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key.SetValue(RunValueName, $"\"{exePath}\" {BackgroundArgument}", RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void RemoveRunKey()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            _log.Debug("Startup", $"Could not remove the Run key value: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------------------------
    // Hook timeout
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Raises <c>LowLevelHooksTimeout</c>.
    ///
    /// Windows drops a low level hook whose callback overruns the timeout (300 ms by default) too
    /// many times, and it does so without any notification. The callback here is nowhere near that
    /// budget in normal operation, but a machine that stalls under heavy load can still trip it, and
    /// the user experiences that as the application randomly stopping. Raising the ceiling removes
    /// the class of failure. Takes effect at the next sign in.
    /// </summary>
    public bool ApplyHookTimeout(bool enabled, int milliseconds = 5000)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop", writable: true);

            if (!enabled)
            {
                key.DeleteValue("LowLevelHooksTimeout", throwOnMissingValue: false);
                return true;
            }

            object? current = key.GetValue("LowLevelHooksTimeout");
            if (current is int existing && existing >= milliseconds)
            {
                return true;
            }

            key.SetValue("LowLevelHooksTimeout", milliseconds, RegistryValueKind.DWord);
            _log.Info("Startup", $"LowLevelHooksTimeout set to {milliseconds} ms (applies after sign out).");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("Startup", $"Could not adjust LowLevelHooksTimeout: {ex.Message}");
            return false;
        }
    }
}
