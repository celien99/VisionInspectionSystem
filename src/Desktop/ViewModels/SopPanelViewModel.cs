using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using Modules.SOP.Models;

namespace Desktop.ViewModels;

/// <summary>SOP 面板显示状态；窗口交互仍由视图处理，检测生命周期由 MainViewModel 管理。</summary>
public partial class SopPanelViewModel : ObservableObject
{
    public ObservableCollection<SOPStepItem> Steps { get; } = new();
    private readonly DispatcherTimer _timer;
    private DateTime _timerStart;

    [ObservableProperty] private string _status = "未初始化";
    [ObservableProperty] private Brush _statusForeground = Brushes.Gray;
    [ObservableProperty] private string _result = "--";
    [ObservableProperty] private Brush _resultForeground = Brushes.Gray;
    [ObservableProperty] private string _stepProgress = "0/0";
    [ObservableProperty] private string _elapsedText = "00:00";
    [ObservableProperty] private Brush _elapsedForeground = Brushes.Gray;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatisticsTooltip))] private int _cycleCount;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatisticsTooltip))] private int _ngCycleCount;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatisticsTooltip))] private int _violationCount;

    public string StatisticsTooltip
    {
        get
        {
            var ok = CycleCount - NgCycleCount;
            var yield = CycleCount > 0 ? ok * 100.0 / CycleCount : 0;
            return $"完成周期 {CycleCount} 次（OK {ok} / NG {NgCycleCount}，良率 {yield:F1}%）\n" +
                   $"违规事件 {ViolationCount} 次\n双击可清零统计（复位不清零，换配方自动清零）";
        }
    }

    public SopPanelViewModel()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) =>
        {
            var elapsed = DateTime.Now - _timerStart;
            ElapsedText = elapsed.TotalHours >= 1
                ? $"{elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
                : $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        };
    }

    public void StartTimer()
    {
        _timerStart = DateTime.Now;
        ElapsedText = "00:00";
        ElapsedForeground = new SolidColorBrush(Color.FromRgb(250, 140, 22));
        _timer.Start();
    }

    public void StopTimer() => _timer.Stop();

    public void ResetTimer()
    {
        _timer.Stop();
        ElapsedText = "00:00";
        ElapsedForeground = Brushes.Gray;
    }

    public void SetStatus(string status, Brush? foreground = null)
    {
        Status = status;
        if (foreground != null) StatusForeground = foreground;
    }

    public void SetResult(string result, Brush? foreground = null)
    {
        Result = result;
        if (foreground != null) ResultForeground = foreground;
    }

    public void SetCurrentStep(int stepId)
    {
        var index = Steps.ToList().FindIndex(step => step.Id == stepId.ToString());
        for (var i = 0; i < Steps.Count; i++)
            Steps[i].Status = i < index ? StepStatus.Completed : i == index ? StepStatus.Running : StepStatus.Pending;
        StepProgress = $"{index + 1}/{Steps.Count}";
    }

    public void SetStepResult(int stepIndex, bool success)
    {
        if (stepIndex >= 0 && stepIndex < Steps.Count)
            Steps[stepIndex].Status = success ? StepStatus.Completed : StepStatus.Failed;
    }

    public void SyncDurations(IEnumerable<StepExecutionRecord> history)
    {
        foreach (var record in history)
        {
            var step = Steps.FirstOrDefault(item => item.Id == record.StepId.ToString());
            if (step != null) step.Duration = record.Duration;
        }
    }
}

public partial class SOPStepItem : ObservableObject
{
    public string Id { get; set; } = "";
    public string StepNumber { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    [ObservableProperty] private StepStatus _status;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DurationText))] private TimeSpan _duration;
    public string DurationText => Duration <= TimeSpan.Zero ? "" : Duration.TotalSeconds < 1
        ? $"{Duration.TotalMilliseconds:F0}ms" : $"{Duration.TotalSeconds:F1}s";
}

public enum StepStatus { Pending, Running, Completed, Failed }
