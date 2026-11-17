using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LiveCoreStudio.Core.Diagnostics;
using LiveCoreStudio.Core.Engine;
using LiveCoreStudio.EngineClient;
using LiveCoreStudio.UI.ViewModels;

namespace LiveCoreStudio.App;

/// <summary>
/// <c>LiveCoreStudio.exe --smoke-test &lt;folder&gt;</c>: drives the real application end to end
/// (engine start, sources, switching) and saves a screenshot of every page. Used by CI to
/// verify the running UI, not just that it compiles. Exit code 0 = success.
/// </summary>
internal sealed class SmokeTest(Window window, ShellViewModel shell, EngineSupervisor engine,
    EngineProductionService production, IAppLog log, string outputFolder)
{
    public async Task<int> RunAsync()
    {
        try
        {
            Directory.CreateDirectory(outputFolder);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (engine.Health.State != EngineLifecycleState.Running)
            {
                if (DateTime.UtcNow > deadline) return Fail($"engine did not reach Running (state {engine.Health.State}, {engine.Health.LastError})");
                await Task.Delay(200);
            }

            await ShowAsync("dashboard", 1500);

            var bars = await production.AddTestPatternAsync("Camera 1", "bars", "#000000");
            var orange = await production.AddTestPatternAsync("Graphics", "solid", "#FF7A1A");
            await production.AddTestPatternAsync("Camera 2", "solid", "#2D6CDF");
            await production.SetProgramAsync(bars.Id);
            await production.SetPreviewAsync(orange.Id);
            await ShowAsync("production", 2500);

            if (FindScrollViewer(window) is { } scroller) { scroller.ScrollToBottom(); await Task.Delay(400); await CaptureAsync("production-sources"); scroller.ScrollToTop(); }

            await production.AutoTransitionAsync(2000);
            await Task.Delay(900);
            await CaptureAsync("production-mid-transition");

            await ShowAsync("diagnostics", 1000);
            await ShowAsync("settings", 800);
            log.Info("smoke", "Smoke test passed.");
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex.ToString());
        }
    }

    private static System.Windows.Controls.ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.ScrollViewer { Content: System.Windows.Controls.StackPanel } sv) return sv;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    private int Fail(string reason)
    {
        log.Error("smoke", "Smoke test failed: " + reason);
        File.WriteAllText(Path.Combine(outputFolder, "FAILED.txt"), reason);
        return 1;
    }

    private async Task ShowAsync(string pageKey, int settleMs)
    {
        shell.CurrentPage = shell.Pages.First(p => p.Key == pageKey);
        await Task.Delay(settleMs);
        await CaptureAsync(pageKey);
    }

    private Task CaptureAsync(string name)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var width = (int)Math.Ceiling(content.ActualWidth);
        var height = (int)Math.Ceiling(content.ActualHeight);

        // Paint the window background, then the content, so the PNG matches what the operator sees.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(outputFolder, name + ".png"));
        encoder.Save(file);
        log.Info("smoke", $"Captured {name}.png ({width}x{height}).");
        return Task.CompletedTask;
    }
}
