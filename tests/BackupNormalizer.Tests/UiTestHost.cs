using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace BackupNormalizer.Tests;

/// <summary>Single persistent headless UI thread shared by all GUI tests.</summary>
internal static class UiTestHost
{
    private sealed record UiWork(Action Action, TaskCompletionSource<Exception?> Done);

    private static readonly BlockingCollection<UiWork> Queue = new();

    private static readonly Thread UiThread = new(() =>
    {
        AppBuilder.Configure<BackupNormalizer.Ui.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting();
        foreach (var work in Queue.GetConsumingEnumerable())
        {
            try
            {
                work.Action();
                Dispatcher.UIThread.RunJobs();
                work.Done.SetResult(null);
            }
            catch (Exception ex) { work.Done.SetResult(ex); }
        }
    })
    { IsBackground = true };

    public static void Run(Action action)
    {
        if (!UiThread.IsAlive) UiThread.Start();
        var done = new TaskCompletionSource<Exception?>();
        Queue.Add(new UiWork(action, done));
        var error = done.Task.GetAwaiter().GetResult();
        if (error != null) throw new Xunit.Sdk.XunitException("UI thread failed: " + error);
    }
}
