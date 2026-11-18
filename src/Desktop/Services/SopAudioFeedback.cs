using NAudio.Wave;
using System.IO;
using Core.Services;

namespace Desktop.Services;

/// <summary>复用播放器，并在应用关闭时释放音频设备与文件。</summary>
public sealed class SopAudioFeedback : IDisposable
{
    private readonly Dictionary<string, (WaveOutEvent Output, AudioFileReader Reader)> _players = new();

    public void PlayOk() => Play("ok.wav");
    public void PlayNg() => Play("ng.wav");

    private void Play(string fileName)
    {
        var path = AppPaths.FindFile(fileName);
        if (path == null) return;
        try
        {
            if (!_players.TryGetValue(path, out var player))
            {
                var reader = new AudioFileReader(path);
                var output = new WaveOutEvent();
                try { output.Init(reader); }
                catch { output.Dispose(); reader.Dispose(); throw; }
                player = (output, reader);
                _players.Add(path, player);
            }
            player.Output.Stop();
            player.Reader.Position = 0;
            player.Output.Play();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SOP-Audio] 播放失败 '{path}': {ex}");
        }
    }

    public void Dispose()
    {
        foreach (var (output, reader) in _players.Values)
        {
            output.Dispose();
            reader.Dispose();
        }
        _players.Clear();
    }
}
