using System.Runtime.Versioning;

namespace Tapit.Actions;

/// <summary>
/// Every action Tapit can perform, addressed by stable id.
/// </summary>
/// <remarks>
/// Ids are written into profiles, so they are part of the file format and must not change.
/// Display names may.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ActionRegistry
{
    private readonly Dictionary<string, IAction> _actions;

    public ActionRegistry(IEnumerable<IAction>? actions = null)
    {
        _actions = (actions ?? BuildDefaults())
            .ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<IAction> BuildDefaults() =>
    [
        new NoAction(),
        new VisualFeedbackAction(),
        new PlaySoundAction(),

        new MediaKeyAction("media.playpause", "Play / Pause", NativeInput.VkMediaPlayPause),
        new MediaKeyAction("media.next", "Next track", NativeInput.VkMediaNextTrack),
        new MediaKeyAction("media.previous", "Previous track", NativeInput.VkMediaPrevTrack),
        new MediaKeyAction("media.stop", "Stop", NativeInput.VkMediaStop),

        // Volume steps are 2 keypresses so one tap is an audible change rather than a nudge.
        new MediaKeyAction("volume.up", "Volume up", NativeInput.VkVolumeUp, repeat: 2),
        new MediaKeyAction("volume.down", "Volume down", NativeInput.VkVolumeDown, repeat: 2),
        new MediaKeyAction("volume.mute", "Mute / unmute", NativeInput.VkVolumeMute),

        new ScreenshotAction(),
        new ClipboardScreenshotAction(),
        new CopyTextAction(),
        new LockWorkstationAction(),

        new OpenUrlAction(),
        new OpenFileAction(),
        new OpenFolderAction(),
        new LaunchApplicationAction(),
        new FocusApplicationAction(),
        new RunExecutableAction(),
        new RunPowerShellAction(),
    ];

    public IReadOnlyCollection<IAction> All => _actions.Values;

    public IEnumerable<IGrouping<string, IAction>> ByCategory =>
        _actions.Values.GroupBy(a => a.Category).OrderBy(g => g.Key, StringComparer.Ordinal);

    public IAction? Find(string? id) =>
        id is not null && _actions.TryGetValue(id, out IAction? action) ? action : null;

    /// <summary>Resolves an id, falling back to the do-nothing action rather than throwing.</summary>
    public IAction Resolve(string? id) => Find(id) ?? _actions["none"];

    /// <summary>
    /// Checks a binding without running it, so the UI can show a problem before a tap does.
    /// </summary>
    public ActionValidation Validate(string? id, string? argument)
    {
        IAction? action = Find(id);
        if (action is null)
        {
            return new ActionValidation(false, $"Unknown action '{id}'.");
        }

        if (action.RequiresArgument && string.IsNullOrWhiteSpace(argument))
        {
            return new ActionValidation(false, $"{action.DisplayName} needs {action.ArgumentHint ?? "a value"}.");
        }

        return action switch
        {
            OpenFileAction when !File.Exists(argument) =>
                new ActionValidation(false, $"File not found: {argument}"),
            OpenFolderAction when !Directory.Exists(argument) =>
                new ActionValidation(false, $"Folder not found: {argument}"),
            OpenUrlAction when !Uri.TryCreate(argument, UriKind.Absolute, out Uri? uri)
                               || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) =>
                new ActionValidation(false, "Enter a full http or https URL."),
            _ => new ActionValidation(true, action.DisplayName),
        };
    }
}

public sealed record ActionValidation(bool IsValid, string Message);
