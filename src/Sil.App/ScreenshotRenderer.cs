using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Threading;
using Sil.App.ViewModels;
using Sil.App.Views;

namespace Sil.App;

/// <summary>
/// Renders the shell to PNG files offscreen.
/// </summary>
/// <remarks>
/// Screenshots are produced by laying the window out and rendering it into a bitmap rather than by
/// grabbing the screen: that needs no display server or capture permission, always frames the
/// window exactly, and — because the simulation runs on virtual time to a fixed step count — gives
/// the same image every run. Documentation images are then reproducible the same way results are.
/// </remarks>
internal static class ScreenshotRenderer
{
    private const double Width = 1280;
    private const double Height = 820;

    /// <summary>
    /// Rendered 1:1. The bitmap's DPI already drives the render transform, so asking for a 2x
    /// pixel size as well double-scaled the content and clipped it.
    /// </summary>
    private const double Scale = 1.0;

    internal static int Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var shots = new List<(string File, int Steps, bool Fail)>
        {
            // Idle at t=0, then a settled run, then far enough in that the near-resonant
            // response has broken the displacement band and the verdict has flipped.
            ("01-idle.png", 0, false),
            ("02-running.png", 1200, false),
            ("03-limit-failure.png", 6400, true),
        };

        foreach ((string file, int steps, bool expectFail) in shots)
        {
            string path = Path.Combine(outputDirectory, file);
            bool failing = RenderOne(path, steps);

            if (failing != expectFail)
            {
                Console.Error.WriteLine(
                    $"{file}: expected failing={expectFail} but the run reported failing={failing}.");
                return 1;
            }

            Console.WriteLine($"wrote {path}  ({steps} cycles, verdict {(failing ? "FAIL" : "PASS")})");
        }

        return 0;
    }

    private static bool RenderOne(string path, int steps)
    {
        using var viewModel = new MainWindowViewModel
        {
            SyncToWallClock = false,   // virtual time: the image does not depend on machine speed
        };

        for (int i = 0; i < steps; i++)
        {
            viewModel.StepOnce();
        }

        viewModel.Refresh();

        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = Width,
            Height = Height,
        };

        // The window has to be shown for its content to be attached and laid out; a detached
        // visual renders empty. We then capture the content root rather than the window itself,
        // because a top-level owns its own compositor surface and does not render into a bitmap.
        window.Show();

        // Let layout settle before capturing. A single pump caught the panels mid-arrange and
        // produced a torn frame with elements drawn at two different positions.
        for (int pass = 0; pass < 8; pass++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(30);
            Dispatcher.UIThread.RunJobs();
        }

        // Showing the window already measured and arranged the content at the window size.
        var content = (Visual)window.Content!;

        var pixelSize = new PixelSize((int)(Width * Scale), (int)(Height * Scale));
        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * Scale, 96 * Scale));
        bitmap.Render(content);
        bitmap.Save(path);

        bool failing = viewModel.IsFailing;
        window.Close();
        return failing;
    }
}
