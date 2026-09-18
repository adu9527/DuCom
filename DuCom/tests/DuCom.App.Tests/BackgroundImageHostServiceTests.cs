using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DuCom.PluginHost;
using DuCom.Services.Plugins;
using Xunit;

namespace DuCom.App.Tests;

public sealed class BackgroundImageHostServiceTests
{
    [Fact]
    public void ReuseRevalidatesContentAndPreservesOwnershipAndGeneration()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), $"ducom-background-{Guid.NewGuid():N}");
            Application application = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "image.bmp");
                WriteImage(path, 20);
                BackgroundImageHostService service = new();
                int changes = 0;
                List<string?> properties = [];
                service.Changed += (_, _) => changes++;
                service.PropertyChanged += (_, args) => properties.Add(args.PropertyName);

                void Apply(double? opacity = null, string? imagePath = null, string owner = "owner")
                {
                    int previous = changes;
                    service.Apply(new BackgroundApply { PluginId = owner, ImageTokenPath = imagePath ?? path, Opacity = opacity });
                    PumpUntil(() => changes > previous);
                }

                Apply(0.2);
                BitmapSource original = Assert.IsAssignableFrom<BitmapSource>(service.ImageSource);
                Assert.True(original.IsFrozen);
                Assert.Equal(1920, original.PixelWidth);
                Assert.True(service.Enabled);
                properties.Clear();
                Apply(0.7);
                Assert.Same(original, service.ImageSource);
                Assert.Equal(0.7, service.Opacity);
                Assert.Equal(["ImageSource", "Opacity", "Enabled"], properties);
                Apply();
                Assert.Same(original, service.ImageSource);
                Assert.Equal(0.7, service.Opacity);

                DateTime timestamp = File.GetLastWriteTimeUtc(path);
                long length = new FileInfo(path).Length;
                WriteImage(path, 230);
                File.SetLastWriteTimeUtc(path, timestamp);
                Assert.Equal(length, new FileInfo(path).Length);
                Apply();
                BitmapSource updated = Assert.IsAssignableFrom<BitmapSource>(service.ImageSource);
                Assert.NotSame(original, updated);
                byte[] oldPixels = new byte[original.PixelWidth * 4];
                byte[] newPixels = new byte[updated.PixelWidth * 4];
                original.CopyPixels(new Int32Rect(0, 0, original.PixelWidth, 1), oldPixels, oldPixels.Length, 0);
                updated.CopyPixels(new Int32Rect(0, 0, updated.PixelWidth, 1), newPixels, newPixels.Length, 0);
                Assert.False(oldPixels.SequenceEqual(newPixels));

                Apply(2, owner: "other");
                Assert.Same(updated, service.ImageSource);
                Assert.Equal(1, service.Opacity);
                service.Clear("owner");
                Drain();
                Assert.Same(updated, service.ImageSource);

                // A queued clear must not overwrite a newer generation.
                service.Clear("other");
                Apply(-1);
                Assert.NotSame(updated, service.ImageSource);
                Assert.Equal(0, service.Opacity);
                Assert.True(service.Enabled);

                // Deterministically deliver an old decode after clear (including null owner).
                long staleGeneration = (long)typeof(BackgroundImageHostService)
                    .GetField("_generation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
                service.Clear();
                Drain();
                MethodInfo set = typeof(BackgroundImageHostService).GetMethod("Set", BindingFlags.Instance | BindingFlags.NonPublic)!;
                set.Invoke(service, ["owner", staleGeneration, original, 0.9, null]);
                set.Invoke(service, [null, staleGeneration, original, 0.9, null]);
                Drain();
                Assert.Null(service.ImageSource);
                Assert.False(service.Enabled);
                Assert.Equal(0, service.Opacity);

                Apply();
                ImageSource? beforeStaleClear = service.ImageSource;
                set.Invoke(service, [null, staleGeneration, null, null, null]);
                Drain();
                Assert.Same(beforeStaleClear, service.ImageSource);
                using (FileStream locked = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Apply();
                    Assert.Null(service.ImageSource);
                }
                Apply();
                File.Delete(path);
                Apply();
                Assert.Null(service.ImageSource);
                Assert.False(service.Enabled);

                WriteImage(path, 20);
                Apply();
                using (FileStream oversized = new(path, FileMode.Open, FileAccess.Write))
                    oversized.SetLength(20L * 1024 * 1024 + 1);
                Apply();
                Assert.Null(service.ImageSource);
                File.WriteAllText(path, "not an image");
                Apply();
                Assert.Null(service.ImageSource);

                WriteImage(path, 20);
                Apply(0.4);
                service.Apply(new BackgroundApply { PluginId = "owner", Opacity = 0.8 });
                Drain();
                Assert.Null(service.ImageSource);
                Assert.Equal(0.4, service.Opacity);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                application.Shutdown();
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "Background image test timed out.");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void WriteImage(string path, byte value)
    {
        BitmapSource source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgr32, null,
            new byte[] { value, 0, 0, 0, value, 0, 0, 0 }, 8);
        BmpBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void PumpUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Drain();
            Thread.Sleep(1);
        }
        Assert.True(condition(), "Background apply did not complete.");
    }

    private static void Drain()
    {
        DispatcherFrame frame = new();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
