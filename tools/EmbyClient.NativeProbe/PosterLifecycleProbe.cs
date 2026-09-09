using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using EmbyClient.Api;
using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace EmbyClient.NativeProbe;

public sealed partial class App
{
    private bool _posterLifecycle;

    private void LaunchPosterLifecycle()
    {
        var image = new Image { Width = 176, Height = 264, Stretch = Stretch.UniformToFill };
        _window = new Window
        {
            Title = "SYNTHETIC poster lifecycle baseline",
            Content = new Grid { Children = { image } }
        };
        _window.AppWindow.Resize(new SizeInt32(340, 380));
        _window.AppWindow.Show(activateWindow: false);
        _ = RunPosterLifecycleAsync(image);
    }

    private async Task RunPosterLifecycleAsync(Image image)
    {
        var report = new PosterLifecycleReport();
        var clock = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = timeout.Token;
        void SavePoster()
        {
            report.UpdatedUtc = DateTimeOffset.UtcNow;
            var temporary = _resultPath! + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(report,
                PosterLifecycleJsonContext.Default.PosterLifecycleReport));
            File.Move(temporary, _resultPath!, overwrite: true);
        }
        try
        {
            SavePoster();
            PosterRequire(report.NativeAot, "NativeAotRequired");
            var bytes = await LoadPosterInputAsync(report, Path.GetDirectoryName(_resultPath!)!, token);
            report.HttpDisposedBeforeBaseline = true;
            report.Stage = "BaselineIdle";
            SavePoster();
            await WaitForPosterFramesAsync(token);
            report.RasterizationScale = image.XamlRoot?.RasterizationScale ?? 1;
            report.DecodePixelWidth = (int)Math.Clamp(176 * report.RasterizationScale, 176, 704);
            report.DecodePixelHeight = report.DecodePixelWidth * 3 / 2;
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            report.Baseline = CapturePosterResources("Baseline", clock);
            report.Status = "Running";
            report.Stage = "DecodeBindRenderClear";
            SavePoster();
            for (var index = 1; index <= report.RequiredCycles; index++)
            {
                var cycle = await RunPosterCycleAsync(image, bytes, report.DecodePixelWidth,
                    report.DecodePixelHeight, index, token);
                await Task.Delay(100, token);
                cycle.Resources = CapturePosterResources("Cycle" + index, clock);
                report.Cycles.Add(cycle);
                report.CompletedCycles = index;
                SavePoster();
            }
            report.Stage = "FinalIdle";
            var idleClock = Stopwatch.StartNew();
            foreach (var seconds in new[] { 2, 10, 30 })
            {
                var remaining = TimeSpan.FromSeconds(seconds) - idleClock.Elapsed;
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
                report.FinalIdle.Add(CapturePosterResources("Idle" + seconds, clock));
                SavePoster();
            }
            var early = report.Cycles.Skip(10).Take(10).Select(x => x.Resources!).ToArray();
            var late = report.Cycles.TakeLast(10).Select(x => x.Resources!).ToArray();
            report.LateMedianHandleDelta = PosterMedian(late.Select(x => (double)x.Handles))
                - PosterMedian(early.Select(x => (double)x.Handles));
            report.LateMedianPrivateBytesDelta = PosterMedian(late.Select(x => (double)x.PrivateBytes))
                - PosterMedian(early.Select(x => (double)x.PrivateBytes));
            report.LastFortyHandleSlopePerCycle = PosterSlope(report.Cycles.TakeLast(40).Select(x => (double)x.Resources!.Handles));
            report.LastFortyPrivateBytesSlopePerCycle = PosterSlope(report.Cycles.TakeLast(40).Select(x => (double)x.Resources!.PrivateBytes));
            report.Status = "BaselineCompleted";
        }
        catch (Exception error)
        {
            report.Status = "Failed";
            report.ErrorCode = error is PosterProbeException known ? known.Code
                : error is OperationCanceledException ? "BoundedTimeout" : error.GetType().Name;
            report.ErrorHResult = error.HResult;
        }
        finally
        {
            try { image.Source = null; report.FinalSourceCleared = image.Source is null; }
            catch (Exception error) { report.CleanupErrors.Add(error.GetType().Name + ":" + error.HResult); }
            if (report.CleanupErrors.Count > 0) report.Status = "Failed";
            report.FinishedUtc = DateTimeOffset.UtcNow;
            SavePoster();
            Environment.ExitCode = report.Status == "BaselineCompleted" ? 0 : 1;
            _window!.Close();
            Exit();
        }
    }

    private static async Task<byte[]> LoadPosterInputAsync(PosterLifecycleReport report,
        string outputDirectory, CancellationToken token)
    {
        using var http = EmbyApiClient.CreateHttpClient();
        var api = new EmbyApiClient(http, new Uri("http://127.0.0.1:18962/emby/"),
            new ClientIdentity("SYNTHETIC Poster Probe", "Windows poster probe", "poster-probe-" + report.RunId, "0.1.0"));
        var info = await api.GetPublicSystemInfoAsync(token);
        PosterRequire(info.Id == report.ServerId && info.ServerName == "SYNTHETIC Large Library (5000)"
            && info.Version == "synthetic-1.0", "FixedSyntheticLargeLibraryRequired");
        report.SyntheticIdentityVerified = true;
        report.ServerVersion = info.Version!;
        var authentication = await api.AuthenticateByNameAsync("demo", "demo", token);
        PosterRequire(!string.IsNullOrEmpty(authentication.AccessToken) && !string.IsNullOrEmpty(authentication.User?.Id),
            "SyntheticAuthenticationRequired");
        var authenticated = api.WithAuthentication(authentication.AccessToken!, authentication.User!.Id!);
        try
        {
            var bytes = await authenticated.GetItemImageAsync(report.ItemId, options: new ImageOptions
            {
                MaxWidth = 480, MaxHeight = 720, Format = "png"
            }, cancellationToken: token);
            report.ImageDownloadCount++;
            PosterRequire(bytes.Length is >= 24 and <= 262144
                && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                && bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8), "BoundedSyntheticPngRequired");
            report.InputPixelWidth = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
            report.InputPixelHeight = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
            PosterRequire(report.InputPixelWidth == 480 && report.InputPixelHeight == 720, "ExpectedSyntheticPosterDimensions");
            report.InputBytes = bytes.Length;
            report.InputSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            await File.WriteAllBytesAsync(Path.Combine(outputDirectory, report.InputFileName), bytes, token);
            return bytes;
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await authenticated.LogoutAsync(cleanupTimeout.Token);
            report.LogoutCompleted = true;
        }
    }

    private static async Task<PosterCycle> RunPosterCycleAsync(Image image, byte[] bytes,
        int width, int height, int index, CancellationToken token)
    {
        var cycle = new PosterCycle { Cycle = index };
        var decodeClock = Stopwatch.StartNew();
        await NativePosterDecoder.DecodeAndApplyAsync(bytes, width, height, token, bitmap =>
        {
            image.Source = bitmap;
            cycle.BoundInDecoderCallback = ReferenceEquals(image.Source, bitmap);
            cycle.BitmapPixelWidth = bitmap.PixelWidth;
            cycle.BitmapPixelHeight = bitmap.PixelHeight;
        });
        cycle.DecodeMilliseconds = decodeClock.Elapsed.TotalMilliseconds;
        PosterRequire(cycle.BoundInDecoderCallback && cycle.BitmapPixelWidth > 0 && cycle.BitmapPixelHeight > 0,
            "DecodedSourceBindingMissing");
        cycle.BoundRenderCallbacks = await WaitForPosterFramesAsync(token);
        image.Source = null;
        cycle.SourceCleared = image.Source is null;
        cycle.ClearedRenderCallbacks = await WaitForPosterFramesAsync(token);
        PosterRequire(cycle.SourceCleared, "ImageSourceNotCleared");
        return cycle;
    }

    private static async Task<int> WaitForPosterFramesAsync(CancellationToken token)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        EventHandler<object> handler = (_, _) =>
        {
            if (++count >= 2) completion.TrySetResult(count);
        };
        CompositionTarget.Rendering += handler;
        try
        {
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(8), token);
        }
        catch (TimeoutException) { throw new PosterProbeException("RenderBoundaryTimeout"); }
        finally { CompositionTarget.Rendering -= handler; }
    }

    private static PosterResourceSample CapturePosterResources(string label, Stopwatch clock)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new PosterResourceSample
        {
            Label = label, ElapsedSeconds = clock.Elapsed.TotalSeconds,
            PrivateBytes = process.PrivateMemorySize64, WorkingSetBytes = process.WorkingSet64,
            Handles = process.HandleCount, Threads = process.Threads.Count,
            ManagedBytes = GC.GetTotalMemory(forceFullCollection: false),
            ManagedTotalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false),
            Gen0Collections = GC.CollectionCount(0), Gen1Collections = GC.CollectionCount(1), Gen2Collections = GC.CollectionCount(2)
        };
    }

    private static double PosterMedian(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
    }

    private static double PosterSlope(IEnumerable<double> values)
    {
        var data = values.ToArray();
        var midpoint = (data.Length - 1) / 2.0;
        var mean = data.Average();
        return data.Select((value, index) => (index - midpoint) * (value - mean)).Sum()
            / data.Select((_, index) => (index - midpoint) * (index - midpoint)).Sum();
    }

    private static void PosterRequire(bool condition, string code)
    {
        if (!condition) throw new PosterProbeException(code);
    }

    private sealed class PosterProbeException(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }
}
