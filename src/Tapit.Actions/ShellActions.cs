using System.Diagnostics;
using System.Runtime.Versioning;

namespace Tapit.Actions;

/// <summary>
/// Actions that reach outside Tapit: opening things and running things.
/// </summary>
/// <remarks>
/// Every one of these runs a target the user configured themselves, and only after a tap has
/// cleared both the detector's validation gates and the zone model's rejection stack. They
/// are deliberately thin wrappers over <see cref="Process"/> - Tapit adds no privilege and
/// performs no elevation.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class OpenUrlAction : ActionBase
{
    public override string Id => "open.url";

    public override string DisplayName => "Open a URL";

    public override string Category => "Open";

    public override bool RequiresArgument => true;

    public override string? ArgumentHint => "https://example.com";

    public override Task ExecuteAsync(ActionContext context)
    {
        string url = RequireArgument(context, "a URL");

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"'{url}' is not an http or https URL.");
        }

        Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        return Task.CompletedTask;
    }
}

[SupportedOSPlatform("windows")]
public sealed class OpenFileAction : ActionBase
{
    public override string Id => "open.file";

    public override string DisplayName => "Open a file";

    public override string Category => "Open";

    public override bool RequiresArgument => true;

    public override string? ArgumentHint => "Full path to a file";

    public override Task ExecuteAsync(ActionContext context)
    {
        string path = RequireArgument(context, "a file path");

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"File not found: {path}", path);
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        return Task.CompletedTask;
    }
}

[SupportedOSPlatform("windows")]
public sealed class OpenFolderAction : ActionBase
{
    public override string Id => "open.folder";

    public override string DisplayName => "Open a folder";

    public override string Category => "Open";

    public override bool RequiresArgument => true;

    public override string? ArgumentHint => "Full path to a folder";

    public override Task ExecuteAsync(ActionContext context)
    {
        string path = RequireArgument(context, "a folder path");

        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Folder not found: {path}");
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>Launches an application, or focuses it when it is already running.</summary>
[SupportedOSPlatform("windows")]
public sealed class LaunchApplicationAction : ActionBase
{
    public override string Id => "launch.app";

    public override string DisplayName => "Launch an application";

    public override string Category => "Applications";

    public override bool RequiresArgument => true;

    public override string? ArgumentHint => "Path to an .exe, or an app name on PATH";

    public override Task ExecuteAsync(ActionContext context)
    {
        string target = RequireArgument(context, "an application");

        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>Brings an already-running application to the foreground.</summary>
[SupportedOSPlatform("windows")]
public sealed class FocusApplicationAction : ActionBase
{
    public override string Id => "focus.app";

    public override string DisplayName => "Focus an application";

    public override string Category => "Applications";

    public override bool RequiresArgument => true;

    public override string? ArgumentHint => "Process name, e.g. notepad or chrome";

    public override Task ExecuteAsync(ActionContext context)
    {
        string name = Path.GetFileNameWithoutExtension(RequireArgument(context, "a process name"));

        Process? target = Process.GetProcessesByName(name)
            .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);

        if (target is null)
        {
            throw new InvalidOperationException($"No running window found for '{name}'.");
        }

        NativeInput.ShowWindow(target.MainWindowHandle, NativeInput.ShowRestore);
        NativeInput.SetForegroundWindow(target.MainWindowHandle);

        return Task.CompletedTask;
    }
}

/// <summary>Runs an executable with optional arguments.</summary>
[SupportedOSPlatform("windows")]
public sealed class RunExecutableAction : ActionBase
{
    public override string Id => "run.executable";

    public override string DisplayName => "Run an executable";

    public override string Category => "Run";

    public override bool RequiresArgument => true;

    public override string? ArgumentHint => "Path to an .exe, optionally followed by arguments";

    public override Task ExecuteAsync(ActionContext context)
    {
        string command = RequireArgument(context, "an executable");
        (string executable, string arguments) = SplitCommand(command);

        var startInfo = new ProcessStartInfo(executable)
        {
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process.Start(startInfo)?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Splits a command line, honouring a quoted executable path.</summary>
    internal static (string Executable, string Arguments) SplitCommand(string command)
    {
        command = command.Trim();

        if (command.StartsWith('"'))
        {
            int closing = command.IndexOf('"', 1);
            if (closing > 0)
            {
                return (command[1..closing], command[(closing + 1)..].Trim());
            }
        }

        int space = command.IndexOf(' ');
        return space < 0
            ? (command, string.Empty)
            : (command[..space], command[(space + 1)..].Trim());
    }
}

/// <summary>
/// Runs a PowerShell command.
/// </summary>
/// <remarks>
/// Runs with <c>-NoProfile</c> so behaviour does not depend on the user's profile script,
/// and with <c>-NonInteractive</c> because nothing can answer a prompt from here. The
/// execution policy is left alone: Tapit will not weaken a machine's script policy to make
/// one of its own actions work.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RunPowerShellAction : ActionBase
{
    public override string Id => "run.powershell";

    public override string DisplayName => "Run a PowerShell command";

    public override string Category => "Run";

    public override bool RequiresArgument => true;

    public override string? ArgumentHint => "PowerShell command";

    public override Task ExecuteAsync(ActionContext context)
    {
        string command = RequireArgument(context, "a command");

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        Process.Start(startInfo)?.Dispose();
        return Task.CompletedTask;
    }
}
