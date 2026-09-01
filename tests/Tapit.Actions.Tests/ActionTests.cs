using Tapit.Actions;
using Tapit.Core.Classification;

namespace Tapit.Actions.Tests;

/// <summary>
/// Action-layer tests.
/// </summary>
/// <remarks>
/// These deliberately avoid running actions with real side effects - locking the machine,
/// writing screenshots, overwriting the user's clipboard, launching processes. What is
/// tested is the contract around them: registration, validation, dispatch, ordering, and
/// that a failure is contained rather than taking the dispatcher down.
/// </remarks>
public class ActionRegistryTests
{
    private readonly ActionRegistry _registry = new();

    [Fact]
    public void EveryActionHasAUniqueIdAndDisplayName()
    {
        IReadOnlyCollection<IAction> all = _registry.All;

        Assert.NotEmpty(all);
        Assert.Equal(all.Count, all.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        Assert.All(all, action =>
        {
            Assert.False(string.IsNullOrWhiteSpace(action.Id));
            Assert.False(string.IsNullOrWhiteSpace(action.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(action.Category));
        });
    }

    [Theory]
    [InlineData("none")]
    [InlineData("media.playpause")]
    [InlineData("media.next")]
    [InlineData("media.previous")]
    [InlineData("volume.up")]
    [InlineData("volume.down")]
    [InlineData("volume.mute")]
    [InlineData("system.screenshot")]
    [InlineData("system.screenshot.clipboard")]
    [InlineData("system.lock")]
    [InlineData("clipboard.text")]
    [InlineData("open.url")]
    [InlineData("open.file")]
    [InlineData("open.folder")]
    [InlineData("launch.app")]
    [InlineData("focus.app")]
    [InlineData("run.executable")]
    [InlineData("run.powershell")]
    [InlineData("feedback.visual")]
    [InlineData("feedback.sound")]
    public void EverySpecifiedActionIsRegistered(string id) => Assert.NotNull(_registry.Find(id));

    [Fact]
    public void UnknownIdResolvesToDoNothingRatherThanThrowing()
    {
        // A profile naming an action that no longer exists must not break tap handling.
        Assert.Null(_registry.Find("does.not.exist"));
        Assert.Equal("none", _registry.Resolve("does.not.exist").Id);
        Assert.Equal("none", _registry.Resolve(null).Id);
    }

    [Fact]
    public void ActionsRequiringAnArgumentAdvertiseAHint()
    {
        Assert.All(_registry.All.Where(a => a.RequiresArgument),
            action => Assert.False(string.IsNullOrWhiteSpace(action.ArgumentHint)));
    }

    [Fact]
    public void ValidationCatchesAMissingArgument()
    {
        ActionValidation result = _registry.Validate("open.url", null);

        Assert.False(result.IsValid);
        Assert.Contains("URL", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("file:///C:/windows")]
    [InlineData("not a url")]
    [InlineData("javascript:alert(1)")]
    public void OnlyHttpAndHttpsUrlsAreAccepted(string url) =>
        Assert.False(_registry.Validate("open.url", url).IsValid);

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://localhost:8080/path")]
    public void HttpUrlsValidate(string url) => Assert.True(_registry.Validate("open.url", url).IsValid);

    [Fact]
    public void MissingPathsAreReportedBeforeATapEverFires()
    {
        Assert.False(_registry.Validate("open.file", @"C:\definitely\not\here.txt").IsValid);
        Assert.False(_registry.Validate("open.folder", @"C:\definitely\not\here").IsValid);
    }

    [Fact]
    public void ExistingPathsValidate()
    {
        string file = Path.GetTempFileName();
        try
        {
            Assert.True(_registry.Validate("open.file", file).IsValid);
            Assert.True(_registry.Validate("open.folder", Path.GetTempPath()).IsValid);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void UnknownActionFailsValidation() =>
        Assert.False(_registry.Validate("nope", null).IsValid);

    [Fact]
    public void ActionsAreGroupedByCategory() =>
        Assert.Contains(_registry.ByCategory, group => group.Key == "Media" && group.Count() >= 3);
}

public class SafeActionExecutionTests
{
    [Fact]
    public async Task NoActionDoesNothingAndSucceeds() =>
        await new NoAction().ExecuteAsync(ActionContext.ForTest());

    [Fact]
    public async Task VisualFeedbackReachesTheFeedbackSink()
    {
        var feedback = new RecordingFeedback();
        var context = new ActionContext(Zone.RightRear, 0.9, DateTimeOffset.Now, null, feedback);

        await new VisualFeedbackAction().ExecuteAsync(context);

        Assert.Single(feedback.Flashes);
        Assert.Equal(Zone.RightRear, feedback.Flashes[0].Zone);
    }

    [Fact]
    public async Task ActionsNeedingAnArgumentThrowWhenItIsMissing()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CopyTextAction().ExecuteAsync(ActionContext.ForTest()));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OpenUrlAction().ExecuteAsync(ActionContext.ForTest()));
    }

    [Fact]
    public async Task OpenUrlRefusesANonWebScheme() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OpenUrlAction().ExecuteAsync(ActionContext.ForTest(argument: "ftp://example.com")));

    [Fact]
    public async Task OpenFileReportsAMissingFile() =>
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new OpenFileAction().ExecuteAsync(ActionContext.ForTest(argument: @"C:\nope\missing.txt")));

    [Fact]
    public async Task FocusApplicationReportsWhenNothingIsRunning() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FocusApplicationAction().ExecuteAsync(
                ActionContext.ForTest(argument: "tapit-no-such-process-xyz")));

    [Theory]
    [InlineData("notepad.exe", "notepad.exe", "")]
    [InlineData("notepad.exe /a /b", "notepad.exe", "/a /b")]
    [InlineData("\"C:\\Program Files\\App\\a.exe\" --flag", "C:\\Program Files\\App\\a.exe", "--flag")]
    [InlineData("\"C:\\Program Files\\a.exe\"", "C:\\Program Files\\a.exe", "")]
    public void CommandSplittingHonoursQuotedPaths(string input, string executable, string arguments)
    {
        (string actualExecutable, string actualArguments) = RunExecutableAction.SplitCommand(input);

        Assert.Equal(executable, actualExecutable);
        Assert.Equal(arguments, actualArguments);
    }
}

public class ActionDispatcherTests
{
    [Fact]
    public async Task TestAsyncRunsSynchronouslyAndReportsSuccess()
    {
        using var dispatcher = new ActionDispatcher(new ActionRegistry());

        ActionOutcome outcome = await dispatcher.TestAsync(Zone.LeftFront, "none", null);

        Assert.True(outcome.Succeeded);
        Assert.Equal("none", outcome.ActionId);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public async Task TestAsyncCapturesFailureRatherThanThrowing()
    {
        using var dispatcher = new ActionDispatcher(new ActionRegistry());

        ActionOutcome outcome = await dispatcher.TestAsync(Zone.LeftFront, "open.url", null);

        Assert.False(outcome.Succeeded);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public void DispatchedActionsRun()
    {
        var registry = new ActionRegistry([new CountingAction()]);
        using var dispatcher = new ActionDispatcher(registry);

        var completed = new CountdownEvent(3);
        dispatcher.ActionCompleted += (_, _) => completed.Signal();

        for (int i = 0; i < 3; i++)
        {
            Assert.True(dispatcher.Dispatch(Zone.LeftRear, "test.counter", null, 1.0));
        }

        Assert.True(completed.Wait(TimeSpan.FromSeconds(5)), "actions did not run");
        Assert.Equal(3, dispatcher.ExecutedCount);
        Assert.Equal(0, dispatcher.FailedCount);
    }

    [Fact]
    public void AFailingActionIsContainedAndCounted()
    {
        var registry = new ActionRegistry([new ThrowingAction()]);
        using var dispatcher = new ActionDispatcher(registry);

        var completed = new ManualResetEventSlim(false);
        ActionOutcome? captured = null;
        dispatcher.ActionCompleted += (_, outcome) =>
        {
            captured = outcome;
            completed.Set();
        };

        dispatcher.Dispatch(Zone.LeftRear, "test.throws", null, 1.0);

        Assert.True(completed.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(captured!.Succeeded);
        Assert.Equal(1, dispatcher.FailedCount);

        // The worker must still be alive for the next tap.
        Assert.True(dispatcher.Dispatch(Zone.LeftRear, "test.throws", null, 1.0));
    }

    [Fact]
    public void AFullQueueDropsTheOldestSoTheNewestTapStillRuns()
    {
        var blocking = new BlockingAction();
        var registry = new ActionRegistry([blocking]);
        using var dispatcher = new ActionDispatcher(registry, capacity: 2);

        // First one occupies the worker; the rest queue up behind it.
        for (int i = 0; i < 8; i++)
        {
            dispatcher.Dispatch(Zone.LeftRear, "test.blocking", i.ToString(), 1.0);
        }

        Assert.True(dispatcher.DroppedCount > 0, "a bounded queue must drop when it overflows");

        blocking.Release();
    }

    [Fact]
    public void DispatchAfterDisposeIsRefusedNotThrown()
    {
        var dispatcher = new ActionDispatcher(new ActionRegistry());
        dispatcher.Dispose();

        Assert.False(dispatcher.Dispatch(Zone.LeftRear, "none", null, 1.0));
    }

    [Fact]
    public void RecentOutcomesAreBounded()
    {
        var registry = new ActionRegistry([new CountingAction()]);
        using var dispatcher = new ActionDispatcher(registry, capacity: 64);

        var completed = new CountdownEvent(30);
        dispatcher.ActionCompleted += (_, _) =>
        {
            if (!completed.IsSet)
            {
                completed.Signal();
            }
        };

        for (int i = 0; i < 30; i++)
        {
            dispatcher.Dispatch(Zone.LeftRear, "test.counter", null, 1.0);
        }

        completed.Wait(TimeSpan.FromSeconds(10));

        Assert.True(dispatcher.Recent.Count <= 20, "the outcome log must not grow without limit");
    }

    private sealed class CountingAction : ActionBase
    {
        public override string Id => "test.counter";

        public override string DisplayName => "Counter";

        public override Task ExecuteAsync(ActionContext context) => Task.CompletedTask;
    }

    private sealed class ThrowingAction : ActionBase
    {
        public override string Id => "test.throws";

        public override string DisplayName => "Throws";

        public override Task ExecuteAsync(ActionContext context) =>
            throw new InvalidOperationException("deliberate failure");
    }

    private sealed class BlockingAction : ActionBase
    {
        private readonly ManualResetEventSlim _gate = new(false);

        public override string Id => "test.blocking";

        public override string DisplayName => "Blocking";

        public override Task ExecuteAsync(ActionContext context)
        {
            _gate.Wait(TimeSpan.FromSeconds(10));
            return Task.CompletedTask;
        }

        public void Release() => _gate.Set();
    }
}

internal sealed class RecordingFeedback : IActionFeedback
{
    public List<(Zone Zone, string Message)> Flashes { get; } = [];

    public List<string> Notifications { get; } = [];

    public int Beeps { get; private set; }

    public void Flash(Zone zone, string message) => Flashes.Add((zone, message));

    public void Beep() => Beeps++;

    public void Notify(string message) => Notifications.Add(message);
}
