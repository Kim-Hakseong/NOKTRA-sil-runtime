using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Sil.App.ViewModels;

namespace Sil.App;

internal static class Program
{
    /// <summary>
    /// Entry point. <c>--smoke</c> exercises the composition root headlessly — it builds the
    /// shell view model, runs the scenario for a fixed span of virtual time and prints the
    /// result — so the app can be verified without a display server.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--smoke", StringComparer.Ordinal))
        {
            return RunSmokeTest();
        }

        int screenshotIndex = Array.IndexOf(args, "--screenshot");
        if (screenshotIndex >= 0)
        {
            string directory = screenshotIndex + 1 < args.Length
                ? args[screenshotIndex + 1]
                : "docs/screenshots";

            // Set the framework up without entering a message loop: the renderer drives layout
            // and the dispatcher itself, and no window is ever shown.
            BuildAvaloniaApp().SetupWithoutStarting();
            return ScreenshotRenderer.Run(directory);
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Used by the Avalonia tooling and by <see cref="Main"/>.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static int RunSmokeTest()
    {
        using var viewModel = new MainWindowViewModel
        {
            SyncToWallClock = false,   // run on virtual time so the smoke test is instant
        };

        Console.WriteLine($"scenario   : {viewModel.ScenarioTitle}");
        Console.WriteLine($"channels   : {viewModel.Channels.Count}");
        Console.WriteLine($"tree nodes : {viewModel.ProjectTree.Count}");
        Console.WriteLine($"traces     : {viewModel.Traces.Count}");

        for (int i = 0; i < 400; i++)
        {
            viewModel.StepOnce();
        }

        viewModel.Refresh();

        Console.WriteLine($"time       : {viewModel.SimulationTimeText}");
        Console.WriteLine($"steps      : {viewModel.StepCountText}");
        Console.WriteLine($"verdict    : {viewModel.VerdictText}");

        foreach (ChannelRowViewModel channel in viewModel.Channels)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {channel.Header,-16} {channel.ValueText,12}   {channel.LimitText}"));
        }

        bool healthy = viewModel.Channels.Count > 0
            && viewModel.Traces.Count > 0
            && viewModel.Traces.All(t => t.Count > 0)
            && viewModel.StepCountText == "400";

        Console.WriteLine(healthy ? "smoke: OK" : "smoke: FAILED");
        return healthy ? 0 : 1;
    }
}
