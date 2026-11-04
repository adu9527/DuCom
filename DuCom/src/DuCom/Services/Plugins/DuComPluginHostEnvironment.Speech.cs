using System.IO;
using System.Windows;
using System.Windows.Media;

namespace DuCom.Services.Plugins;

public sealed partial class DuComPluginHostEnvironment
{
    private readonly Dictionary<string, (MediaPlayer Player, string Path)> _speechPlayers = new(StringComparer.Ordinal);
    public async Task PlaySpeechPreviewAsync(string pluginId, byte[]? mp3, CancellationToken cancellationToken)
    {
        Application? app = Application.Current;
        if (app is null || app.Dispatcher.HasShutdownStarted) return;
        await app.Dispatcher.InvokeAsync(() =>
        {
            if (_speechPlayers.Remove(pluginId, out var old))
            {
                old.Player.Close();
                try { File.Delete(old.Path); } catch (IOException) { }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (mp3 is null) return;
            string root = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "DuCom", "Plugins", "Data", pluginId, "Preview");
            Directory.CreateDirectory(root);
            foreach (string stale in Directory.EnumerateFiles(root, "*.mp3"))
                try { if (File.GetLastWriteTimeUtc(stale) < DateTime.UtcNow.AddDays(-1)) File.Delete(stale); } catch (IOException) { }
            string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".mp3");
            File.WriteAllBytes(path, mp3);
            MediaPlayer player = new();
            _speechPlayers[pluginId] = (player, path);
            void Release()
            {
                if (_speechPlayers.TryGetValue(pluginId, out var current) && ReferenceEquals(current.Player, player)) _speechPlayers.Remove(pluginId);
                player.Close();
                try { File.Delete(path); } catch (IOException) { }
            }
            player.MediaEnded += (_, _) => Release();
            player.MediaFailed += (_, e) => { Program.DiagnosticLog?.Warning("语音试听播放失败：" + e.ErrorException.Message); Release(); };
            player.Open(new Uri(path)); player.Play();
        }).Task;
    }
}
