using Syncfusion.UI.Xaml.Charts;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

// Repro: SfChart.ClearBuffer throws "Cannot call this method while the image is unlocked."
//
// SfChart.ClearBuffer() does: fastRenderSurface.Lock(); fastRenderSurface.Clear(); fastRenderSurface.Unlock();
// WriteableBitmap.Lock() may wait for the render thread using a pumping STA wait. Sent window messages
// (here: WM_WINDOWPOSCHANGED/WM_SIZE caused by SetWindowPos from another thread) are dispatched during
// that wait. They run layout, which calls SfChart.OnSeriesBoundsChanged -> CreateFastRenderSurface and
// replaces fastRenderSurface. Unlock() then runs on the new bitmap, which was never locked.
//
// Usage: SfChartUnlockRepro.exe [seconds=60] [--software]
internal static class Program
{
    public sealed class Point2(double x, double y)
    {
        public double X { get; } = x;
        public double Y { get; } = y;
    }

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private static int s_hits;

    [STAThread]
    private static int Main(string[] args)
    {
        var seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 60;
        if (Array.IndexOf(args, "--software") >= 0)
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        var licenseKey = Environment.GetEnvironmentVariable("SYNCFUSION_LICENSE_KEY");
        if (!string.IsNullOrEmpty(licenseKey))
            Syncfusion.Licensing.SyncfusionLicenseProvider.RegisterLicense(licenseKey);

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            if (e.Exception is InvalidOperationException && e.Exception.StackTrace?.Contains("SfChart.ClearBuffer") == true)
            {
                var hit = Interlocked.Increment(ref s_hits);
                if (hit == 1)
                    Console.WriteLine($"REPRODUCED:{Environment.NewLine}{e.Exception}");
                e.Handled = true;
            }
        };

        var data = new ObservableCollection<Point2>();
        var chart = new SfChart
        {
            PrimaryAxis = new NumericalAxis(),
            SecondaryAxis = new NumericalAxis(),
        };
        chart.Series.Add(new FastLineBitmapSeries
        {
            ItemsSource = data,
            XBindingPath = nameof(Point2.X),
            YBindingPath = nameof(Point2.Y),
            Interior = Brushes.Blue,
        });

        var window = new Window { Title = "SfChart unlock repro", Width = 1200, Height = 800, Content = chart };

        var x = 0.0;
        var random = new Random(1);
        var dataTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
        {
            for (var i = 0; i < 20; i++)
            {
                data.Add(new Point2(x, Math.Sin(x / 50) * 100 + random.NextDouble() * 10));
                x++;
            }
            while (data.Count > 5000)
                data.RemoveAt(0);
        }, window.Dispatcher);

        var stop = new ManualResetEventSlim();
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var resizer = new Thread(() =>
            {
                var big = true;
                while (!stop.IsSet)
                {
                    // Cross-thread SetWindowPos sends the size messages synchronously to the UI thread.
                    SetWindowPos(hwnd, IntPtr.Zero, 0, 0, big ? 1200 : 1180, big ? 800 : 780,
                        SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
                    big = !big;
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
            resizer.Start();
        };

        var sw = Stopwatch.StartNew();
        var endTimer = new DispatcherTimer(TimeSpan.FromSeconds(seconds), DispatcherPriority.Normal, (_, _) =>
        {
            stop.Set();
            window.Close();
        }, window.Dispatcher);

        window.Loaded += (_, _) => endTimer.Start();
        app.Run(window);

        Console.WriteLine($"Ran {sw.Elapsed.TotalSeconds:F0}s, ClearBuffer unlock exceptions: {s_hits}");
        return s_hits > 0 ? 1 : 0;
    }
}
