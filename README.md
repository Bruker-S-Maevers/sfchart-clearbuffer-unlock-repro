# SfChart `ClearBuffer` "image is unlocked" repro

Minimal WPF (.NET Framework 4.8) repro for an unhandled exception in Syncfusion `SfChart` when a
bitmap series (for example `FastLineBitmapSeries`) is used:

```
System.InvalidOperationException: Cannot call this method while the image is unlocked.
   at System.Windows.Media.Imaging.WriteableBitmap.Unlock()
   at Syncfusion.UI.Xaml.Charts.SfChart.ClearBuffer()
   at Syncfusion.UI.Xaml.Charts.ChartSeriesPanel.Update(Size finalSize)
   at Syncfusion.UI.Xaml.Charts.ChartSeriesBase.UpdateOnSeriesBoundChanged(Size size)
   at Syncfusion.UI.Xaml.Charts.AdornmentSeries.UpdateOnSeriesBoundChanged(Size size)
   at Syncfusion.UI.Xaml.Charts.CartesianSeries.UpdateOnSeriesBoundChanged(Size size)
   at Syncfusion.UI.Xaml.Charts.SfChart.RenderSeries()
   at Syncfusion.UI.Xaml.Charts.SfChart.UpdateArea(Boolean forceUpdate)
   at System.Windows.Threading.ExceptionWrapper.InternalRealCall(Delegate callback, Object args, Int32 numArgs)
   at System.Windows.Threading.ExceptionWrapper.TryCatchWhen(Object source, Delegate callback, Object args, Int32 numArgs, Delegate catchHandler)
```

Affected versions: reproduced with `Syncfusion.SfChart.WPF` **28.2.3** and **35.1.40** (latest at the time
of writing). The relevant code is unchanged between these versions.

## Steps to reproduce

Requirements: Windows, .NET SDK (any recent version), .NET Framework 4.8.

```powershell
# optional: without a key, Syncfusion shows a "Syncfusion License" trial dialog that must be closed
$env:SYNCFUSION_LICENSE_KEY = "<your key>"

dotnet build -c Release
.\bin\Release\net48\SfChartUnlockRepro.exe 30
```

Arguments: `[seconds=60] [--software]`. `--software` forces WPF software rendering, which makes the
render thread slower and the issue even more likely.

The app:

1. Shows an `SfChart` with a single `FastLineBitmapSeries` and streams data into it (every 10 ms).
2. Resizes the window from a **background thread** using Win32 `SetWindowPos`. The size messages
   are *sent* to the UI thread (synchronously, not posted).
3. Counts the `InvalidOperationException`s thrown from `SfChart.ClearBuffer` and prints the first one.

Expected: no exception. Actual: the exception above, typically dozens of times within 30 seconds.
The exit code is `1` when the issue was reproduced.

## Root cause

`SfChart.ClearBuffer()` reads the `fastRenderSurface` field three times:

```csharp
internal void ClearBuffer()
{
    if (clearPixels)
    {
        if (fastRenderSurface != null)
        {
            fastRenderSurface.Lock();
            fastRenderSurface.Clear();
            fastRenderSurface.Unlock();
        }
        clearPixels = false;
    }
}
```

`WriteableBitmap.Lock()` waits for the render thread to finish copying the previous frame
(`AcquireBackBuffer` -> `_copyCompletedEvent.WaitOne`). On an STA thread this is a pumping wait
(`DispatcherSynchronizationContext.Wait` -> `CoWaitForMultipleHandles`): **sent window messages and
COM calls are dispatched while `Lock()` waits**.

If one of those messages causes a layout pass that changes the series bounds (for example
`WM_SIZE`/`WM_WINDOWPOSCHANGED` from a resize initiated by another thread or process, a DPI or display
change, a Remote Desktop reconnect, or an inbound COM call that changes the UI),
`SfChart.OnSeriesBoundsChanged` -> `CreateFastRenderSurface()` runs **re-entrantly** and assigns a
new `WriteableBitmap` to `fastRenderSurface`.

When `Lock()` returns:

1. `Lock()` was called on the **old** bitmap.
2. `Clear()` clears the **new** bitmap.
3. `Unlock()` is called on the **new** bitmap, which was never locked, so it throws
   `InvalidOperationException: Cannot call this method while the image is unlocked.`

Secondary effect: the exception leaves `UpdateArea` before `isUpdateDispatched = false` is reset.
`ChartBase.ScheduleUpdate()` then never dispatches another update, so the chart **stops updating**
until its size changes again, even if the application handles the exception.

## How to fix (in Syncfusion)

Read the field once into a local variable and use it for the whole lock/unlock sequence. Syncfusion
already does this elsewhere, for example `ChartSeries.OnBitmapSelection`:

```csharp
WriteableBitmap fastRenderSurface = Area.fastRenderSurface;
fastRenderSurface.Lock();
int* pixels = fastRenderSurface.GetPixels();
...
fastRenderSurface.Unlock();
```

The same pattern is used in `XyDataSeries.GenerateColumnPixels`/`GenerateBarPixels` and
`StackingSeriesBase.GenerateStackingColumnPixels`/`GenerateStackingBarPixels`.

Suggested fix for `SfChart.ClearBuffer()`:

```csharp
internal void ClearBuffer()
{
    if (clearPixels)
    {
        var surface = fastRenderSurface;
        if (surface != null)
        {
            surface.Lock();
            try
            {
                surface.Clear();
            }
            finally
            {
                surface.Unlock();
            }
        }
        clearPixels = false;
    }
}
```

`SfChart.UpdateBitmapSeries(ChartSeries, bool)` has the same pattern (`fastRenderSurface.Lock()` ...
`fastRenderSurface.Unlock()` on the field, with a `ClearBuffer()` call in between) and should be fixed
the same way.

It would also be worth making `UpdateArea` robust (for example resetting `isUpdateDispatched` in a
`finally` block), so a single exception during rendering does not stop all further chart updates.

## Workaround (application side)

Until this is fixed, an application can handle exactly this exception in
`Dispatcher.UnhandledException`. Match it by stack frames: frame 0 is `WriteableBitmap.Unlock`,
frame 1 is `SfChart.ClearBuffer`. Then set `e.Handled = true` and force a chart update so the chart
does not stay frozen, for example by invoking the internal `SfChart.UpdateArea(true)` via reflection
at `DispatcherPriority.Background`. Alternatively, use non-bitmap series such as `FastLineSeries`,
which do not use a `WriteableBitmap`.
