using Tapit.Core.Classification;

namespace Tapit.Actions;

/// <summary>Visual and audible feedback, supplied by the host so actions stay UI-free.</summary>
public interface IActionFeedback
{
    /// <summary>Briefly indicates which zone fired.</summary>
    void Flash(Zone zone, string message);

    /// <summary>Short confirmation tone.</summary>
    void Beep();

    /// <summary>Reports a failure the user should know about.</summary>
    void Notify(string message);
}

/// <summary>Feedback sink that does nothing. Used in tests and headless runs.</summary>
public sealed class NullActionFeedback : IActionFeedback
{
    public static NullActionFeedback Instance { get; } = new();

    public void Flash(Zone zone, string message)
    {
    }

    public void Beep()
    {
    }

    public void Notify(string message)
    {
    }
}

/// <summary>Everything an action is allowed to know about the tap that triggered it.</summary>
public sealed record ActionContext(
    Zone Zone,
    double Confidence,
    DateTimeOffset TriggeredAt,
    string? Argument,
    IActionFeedback Feedback)
{
    /// <summary>Convenience for tests and for actions that need no argument.</summary>
    public static ActionContext ForTest(Zone zone = Zone.LeftFront, string? argument = null) =>
        new(zone, 1.0, DateTimeOffset.Now, argument, NullActionFeedback.Instance);
}

/// <summary>
/// Something Tapit can do in response to an accepted tap.
/// </summary>
/// <remarks>
/// <para>
/// Actions run on a dedicated worker, never on the audio or DSP threads, and are allowed to
/// block - launching a process or taking a screenshot takes as long as it takes.
/// </para>
/// <para>
/// An action is only ever reached through <see cref="ActionDispatcher"/>, and the dispatcher
/// is only ever handed events that passed both the detector's validation gates and the zone
/// model's rejection stack. There is no code path from a rejected event to an action.
/// </para>
/// </remarks>
public interface IAction
{
    /// <summary>Stable identifier used in profiles. Never localise this.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Category used to group actions in the UI.</summary>
    string Category { get; }

    /// <summary>True when the action needs configuration (a URL, a path, a command).</summary>
    bool RequiresArgument { get; }

    /// <summary>Hint shown next to the argument field.</summary>
    string? ArgumentHint { get; }

    Task ExecuteAsync(ActionContext context);
}

/// <summary>Base class supplying the boilerplate every action shares.</summary>
public abstract class ActionBase : IAction
{
    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    public virtual string Category => "General";

    public virtual bool RequiresArgument => false;

    public virtual string? ArgumentHint => null;

    public abstract Task ExecuteAsync(ActionContext context);

    /// <summary>Validates that a required argument is present.</summary>
    protected static string RequireArgument(ActionContext context, string what)
    {
        if (string.IsNullOrWhiteSpace(context.Argument))
        {
            throw new InvalidOperationException($"This action needs {what} to be configured.");
        }

        return context.Argument;
    }
}
