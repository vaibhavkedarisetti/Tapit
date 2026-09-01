using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Tapit.Core.Classification;

namespace Tapit.Actions;

[SupportedOSPlatform("windows")]
internal static class NativeInput
{
    // Virtual key codes for the multimedia keys. Sending these is exactly what a keyboard
    // with media buttons does, so any application that responds to those buttons responds
    // to Tapit without needing to know Tapit exists.
    public const byte VkVolumeMute = 0xAD;
    public const byte VkVolumeDown = 0xAE;
    public const byte VkVolumeUp = 0xAF;
    public const byte VkMediaNextTrack = 0xB0;
    public const byte VkMediaPrevTrack = 0xB1;
    public const byte VkMediaStop = 0xB2;
    public const byte VkMediaPlayPause = 0xB3;

    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr handle, int command);

    public const int ShowRestore = 9;

    public static void TapKey(byte virtualKey)
    {
        keybd_event(virtualKey, 0, KeyEventExtendedKey, UIntPtr.Zero);
        keybd_event(virtualKey, 0, KeyEventExtendedKey | KeyEventKeyUp, UIntPtr.Zero);
    }
}

/// <summary>Sends a single media or volume key.</summary>
[SupportedOSPlatform("windows")]
public sealed class MediaKeyAction(string id, string displayName, byte virtualKey, int repeat = 1) : ActionBase
{
    public override string Id => id;

    public override string DisplayName => displayName;

    public override string Category => "Media";

    public override Task ExecuteAsync(ActionContext context)
    {
        for (int i = 0; i < repeat; i++)
        {
            NativeInput.TapKey(virtualKey);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Locks the workstation.</summary>
[SupportedOSPlatform("windows")]
public sealed class LockWorkstationAction : ActionBase
{
    public override string Id => "system.lock";

    public override string DisplayName => "Lock Windows";

    public override string Category => "System";

    public override Task ExecuteAsync(ActionContext context)
    {
        if (!NativeInput.LockWorkStation())
        {
            throw new InvalidOperationException("Windows refused the lock request.");
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Captures every screen into a single image.
/// </summary>
/// <remarks>
/// Shared by the file and clipboard screenshot actions so both capture identically.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ScreenCapture
{
    public static Bitmap CaptureAllScreens()
    {
        Rectangle bounds = SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        }

        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);

        try
        {
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    public static string DefaultScreenshotDirectory
    {
        get
        {
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string directory = Path.Combine(
                string.IsNullOrEmpty(pictures) ? Path.GetTempPath() : pictures, "Tapit Screenshots");

            Directory.CreateDirectory(directory);
            return directory;
        }
    }
}

/// <summary>Saves a screenshot to a PNG file.</summary>
[SupportedOSPlatform("windows")]
public sealed class ScreenshotAction : ActionBase
{
    public override string Id => "system.screenshot";

    public override string DisplayName => "Screenshot to file";

    public override string Category => "System";

    public override bool RequiresArgument => false;

    public override string? ArgumentHint => "Folder (optional - defaults to Pictures\\Tapit Screenshots)";

    public override Task ExecuteAsync(ActionContext context)
    {
        string directory = string.IsNullOrWhiteSpace(context.Argument)
            ? ScreenCapture.DefaultScreenshotDirectory
            : context.Argument;

        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, $"tapit-{DateTime.Now:yyyyMMdd-HHmmss}.png");

        using Bitmap bitmap = ScreenCapture.CaptureAllScreens();
        bitmap.Save(path, ImageFormat.Png);

        context.Feedback.Notify($"Screenshot saved to {path}");
        return Task.CompletedTask;
    }
}

/// <summary>Copies a screenshot to the clipboard instead of writing a file.</summary>
[SupportedOSPlatform("windows")]
public sealed class ClipboardScreenshotAction : ActionBase
{
    public override string Id => "system.screenshot.clipboard";

    public override string DisplayName => "Screenshot to clipboard";

    public override string Category => "System";

    public override Task ExecuteAsync(ActionContext context)
    {
        using Bitmap bitmap = ScreenCapture.CaptureAllScreens();
        ClipboardHelper.SetImage(bitmap);
        return Task.CompletedTask;
    }
}

/// <summary>Copies configured text to the clipboard.</summary>
[SupportedOSPlatform("windows")]
public sealed class CopyTextAction : ActionBase
{
    public override string Id => "clipboard.text";

    public override string DisplayName => "Copy text";

    public override string Category => "Clipboard";

    public override bool RequiresArgument => true;

    public override string? ArgumentHint => "Text to copy";

    public override Task ExecuteAsync(ActionContext context)
    {
        ClipboardHelper.SetText(RequireArgument(context, "some text"));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Clipboard access, marshalled onto an STA thread.
/// </summary>
/// <remarks>
/// The Windows clipboard is an OLE service and requires a single-threaded apartment. The
/// action worker is a plain background thread, so every clipboard call gets its own
/// short-lived STA thread rather than silently failing.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ClipboardHelper
{
    public static void SetText(string text) => RunSta(() => Clipboard.SetText(text));

    public static void SetImage(Image image) => RunSta(() => Clipboard.SetImage(image));

    private static void RunSta(Action action)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("The clipboard did not respond.");
        }

        if (failure is not null)
        {
            throw new InvalidOperationException($"Clipboard operation failed: {failure.Message}", failure);
        }
    }
}

/// <summary>Flashes the zone indicator and nothing else. The safe default for testing.</summary>
public sealed class VisualFeedbackAction : ActionBase
{
    public override string Id => "feedback.visual";

    public override string DisplayName => "Show which zone was tapped";

    public override string Category => "Feedback";

    public override Task ExecuteAsync(ActionContext context)
    {
        context.Feedback.Flash(context.Zone, Zones.DisplayName(context.Zone));
        return Task.CompletedTask;
    }
}

/// <summary>Plays a short confirmation sound.</summary>
public sealed class PlaySoundAction : ActionBase
{
    public override string Id => "feedback.sound";

    public override string DisplayName => "Play a sound";

    public override string Category => "Feedback";

    public override Task ExecuteAsync(ActionContext context)
    {
        context.Feedback.Beep();
        return Task.CompletedTask;
    }
}

/// <summary>Does nothing. Lets a zone be deliberately left unbound.</summary>
public sealed class NoAction : ActionBase
{
    public override string Id => "none";

    public override string DisplayName => "Do nothing";

    public override string Category => "Feedback";

    public override Task ExecuteAsync(ActionContext context) => Task.CompletedTask;
}
