using SkiaSharp;
using System.Diagnostics;
using System.Threading.Channels;
using VisionInspection.Core.Models;
using VisionInspection.Core.Services;

namespace VisionInspection.UI.Services;

/// <summary>管理预览和推理工作线程；每路只保留一张待推理帧，替换时立即释放。</summary>
public sealed class CameraFramePipeline : IDisposable
{
    private readonly CameraManager _cameraManager;
    private readonly Action<CameraFrameEventArgs> _renderPreview;
    private readonly Func<Dictionary<string, CaptureFrame>, Task> _processFrames;
    private readonly Channel<CameraFrameEventArgs> _previewQueue = Channel.CreateBounded<CameraFrameEventArgs>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Channel<bool> _inferenceSignal = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _previewCts = new();
    private readonly Task _previewTask;
    private readonly object _pendingLock = new();
    private readonly Dictionary<string, SKBitmap> _pendingFrames = new();
    private CancellationTokenSource? _inferenceCts;
    private Task? _inferenceTask;

    public CameraFramePipeline(CameraManager cameraManager, Action<CameraFrameEventArgs> renderPreview,
        Func<Dictionary<string, CaptureFrame>, Task> processFrames)
    {
        _cameraManager = cameraManager;
        _renderPreview = renderPreview;
        _processFrames = processFrames;
        _cameraManager.FrameGrabbed += OnFrameGrabbed;
        _previewTask = Task.Run(RenderPreviewsAsync);
    }

    private void OnFrameGrabbed(object? sender, CameraFrameEventArgs frame)
        => _previewQueue.Writer.TryWrite(frame);

    private async Task RenderPreviewsAsync()
    {
        var lastPreview = DateTime.MinValue;
        try
        {
            await foreach (var frame in _previewQueue.Reader.ReadAllAsync(_previewCts.Token))
            {
                var now = DateTime.Now;
                if ((now - lastPreview).TotalMilliseconds < 1000.0 / 25) continue;
                lastPreview = now;
                try { _renderPreview(frame); }
                catch (Exception ex) { Debug.WriteLine($"[Preview] 渲染异常: {ex}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void StartInference()
    {
        lock (_pendingLock)
        {
            if (_inferenceTask != null) return;
            _inferenceCts = new CancellationTokenSource();
            var token = _inferenceCts.Token;
            _inferenceTask = Task.Run(() => RunInferenceAsync(token));
        }
    }

    public void EnqueueInference(string cameraId, SKBitmap bitmap)
    {
        lock (_pendingLock)
        {
            if (_inferenceCts == null || _inferenceCts.IsCancellationRequested) return;
            if (_pendingFrames.Remove(cameraId, out var previous)) previous.Dispose();
            _pendingFrames[cameraId] = bitmap.Copy();
            _inferenceSignal.Writer.TryWrite(true);
        }
    }

    private async Task RunInferenceAsync(CancellationToken token)
    {
        var latestFrames = new Dictionary<string, SKBitmap>();
        var frameNumber = 0;
        try
        {
            while (true)
            {
                await _inferenceSignal.Reader.ReadAsync(token).ConfigureAwait(false);
                lock (_pendingLock)
                {
                    foreach (var (cameraId, bitmap) in _pendingFrames)
                    {
                        if (latestFrames.Remove(cameraId, out var previous)) previous.Dispose();
                        latestFrames[cameraId] = bitmap;
                    }
                    _pendingFrames.Clear();
                }
                var frames = latestFrames.ToDictionary(pair => pair.Key, pair => new CaptureFrame
                {
                    CameraId = pair.Key, Image = pair.Value,
                    Timestamp = DateTime.Now, FrameNumber = frameNumber++
                });
                if (frames.Count > 0) await _processFrames(frames).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Debug.WriteLine($"[Inference] 工作线程异常: {ex}"); }
        finally
        {
            foreach (var frame in latestFrames.Values) frame.Dispose();
            lock (_pendingLock)
            {
                foreach (var frame in _pendingFrames.Values) frame.Dispose();
                _pendingFrames.Clear();
            }
        }
    }

    public async Task StopInferenceAsync()
    {
        Task? worker;
        lock (_pendingLock)
        {
            _inferenceCts?.Cancel();
            worker = _inferenceTask;
        }
        if (worker != null) await worker.ConfigureAwait(false);
        lock (_pendingLock)
        {
            _inferenceCts?.Dispose();
            _inferenceCts = null;
            _inferenceTask = null;
        }
    }

    public void Dispose()
    {
        _cameraManager.FrameGrabbed -= OnFrameGrabbed;
        _previewCts.Cancel();
        _previewQueue.Writer.TryComplete();
        _previewTask.GetAwaiter().GetResult();
        StopInferenceAsync().GetAwaiter().GetResult();
        _previewCts.Dispose();
    }
}
