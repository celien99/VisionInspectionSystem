using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Modules.SOP;
using Modules.SOP.Models;

namespace Modules.SOP.Services;

/// <summary>
/// SOP 运行数据记录器。
/// 按日保存周期结论、已完成步骤、未完成的末步及违规明细。JSONL 每周期一条，CSV 每步一行。
/// 文件位于 <程序基目录>/sop_records/。写入失败仅吞异常，不影响检测主流程。
/// </summary>
public class SopRunRecorder
{
    private readonly string _recordsDir;
    private readonly object _writeLock = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };
    private readonly Dictionary<string, int> _nextSequenceByDate = new(StringComparer.Ordinal);

    public SopRunRecorder(string? baseDir = null)
    {
        _recordsDir = Path.Combine(baseDir ?? AppDomain.CurrentDomain.BaseDirectory, "sop_records");
        try { Directory.CreateDirectory(_recordsDir); }
        catch (Exception ex) { Debug.WriteLine($"[SOP记录] 创建目录失败: {ex.Message}"); }
    }

    public void Subscribe(SOPModule module)
    {
        module.WorkflowCompleted += OnWorkflowCompleted;
    }

    public void Unsubscribe(SOPModule module)
    {
        module.WorkflowCompleted -= OnWorkflowCompleted;
    }

    private void OnWorkflowCompleted(object? sender, SOPCompletedEventArgs e)
    {
        try
        {
            var startTime = e.StartTime;
            var endTime = e.EndTime;

            var record = new SopCycleRecord
            {
                StartTime = startTime,
                EndTime = endTime,
                WorkflowName = e.WorkflowName,
                WorkflowId = e.WorkflowId,
                Operator = e.Operator,
                Result = e.Outcome.ToString(),
                CycleId = e.CycleId,
                EndReason = e.Reason,
                FinalStepId = e.FinalStepId,
                TotalDurationSec = Math.Round((endTime - startTime).TotalSeconds, 2),
                StepCount = e.StepHistory.Count,
                InterruptedStep = e.InterruptedStep == null ? null : ToDuration(e.InterruptedStep),
                Steps = e.StepHistory.Select(ToDuration).ToList(),
                Violations = e.Violations.Select(v => new SopViolationSummary
                {
                    Type = v.Type.ToString(),
                    StepId = v.StepId,
                    Description = v.Description
                }).ToList()
            };

            AppendRecord(record);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SOP记录] 周期写入失败: {ex.Message}");
        }
    }

    private static SopStepDuration ToDuration(StepExecutionRecord step) => new()
    {
        StepId = step.StepId, StepName = step.StepName,
        DurationSec = Math.Round(step.Duration.TotalSeconds, 2), StandardDurationSec = Math.Round(step.StandardDurationSec, 2)
    };

    private void AppendRecord(SopCycleRecord record)
    {
        var dateStr = record.EndTime.ToString("yyyy-MM-dd");
        var jsonlPath = Path.Combine(_recordsDir, $"sop_run_{dateStr}.jsonl");
        var csvPath = Path.Combine(_recordsDir, $"sop_run_{dateStr}.csv");

        lock (_writeLock)
        {
            // 每天只在本进程首次写入时读取一次历史行数，避免每个周期 O(n) 扫描文件。
            if (!_nextSequenceByDate.TryGetValue(dateStr, out var sequence))
            {
                sequence = 1;
                if (File.Exists(jsonlPath))
                {
                    try { sequence = File.ReadLines(jsonlPath).Count() + 1; }
                    catch (Exception ex) { Debug.WriteLine($"[SOP记录] 读取周期序号失败: {ex.Message}"); }
                }
            }
            record.CycleIndex = sequence;
            _nextSequenceByDate[dateStr] = sequence + 1;

            // 1) JSONL：一行一个周期
            var jsonLine = JsonSerializer.Serialize(record, _jsonOptions);
            File.AppendAllText(jsonlPath, jsonLine + Environment.NewLine);

            // 2) CSV：长表，每步一行（首行不存在或为空时写表头）
            bool csvNew = !File.Exists(csvPath) || new FileInfo(csvPath).Length == 0;
            // Continue the old schema when appending to an existing day's file.
            bool extended = csvNew || File.ReadLines(csvPath).First().EndsWith(",EndReason", StringComparison.Ordinal);
            var sb = new StringBuilder();
            if (csvNew) sb.AppendLine(CsvHeader);
            AppendCsvRows(sb, record, extended);
            File.AppendAllText(csvPath, sb.ToString());
        }
    }

    public const string CsvHeader = "CycleIndex,CycleTime,Operator,WorkflowName,Result,TotalDurationSec,StepIndex,StepId,StepName,StepDurationSec,StandardDurationSec,ViolationCount,ViolationTypes,CycleId,StepStatus,EndReason";

    public static void AppendCsvRows(StringBuilder csv, SopCycleRecord record, bool extended = true)
    {
        var steps = record.Steps.Cast<SopStepDuration?>().ToList();
        if (record.InterruptedStep != null || steps.Count == 0) steps.Add(record.InterruptedStep);
        var violations = string.Join("|", record.Violations.Select(v => v.Type));
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var fields = new List<string> {
                record.CycleIndex.ToString(), record.EndTime.ToString("yyyy-MM-dd HH:mm:ss"), record.Operator,
                record.WorkflowName, record.Result, Number(record.TotalDurationSec), step == null ? "" : (i + 1).ToString(),
                step?.StepId.ToString() ?? "", step?.StepName ?? "", Number(step?.DurationSec), Number(step?.StandardDurationSec),
                record.Violations.Count.ToString(), violations
            };
            if (extended) fields.AddRange(new[] { record.CycleId, i < record.Steps.Count ? "Completed" : "Unfinished", record.EndReason });
            csv.AppendLine(string.Join(",", fields.Select(CsvField)));
        }
        static string Number(double? value) => value?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    private static string CsvField(string v) => "\"" + v.Replace("\"", "\"\"") + "\"";
}

/// <summary>一个完整周期（一次 SOP 跑完）的运行记录。</summary>
public class SopCycleRecord
{
    /// <summary>当天第几次运行（从 1 开始，跨重启连续）。</summary>
    public int CycleIndex { get; set; }
    public string CycleId { get; set; } = "";
    public int FinalStepId { get; set; }
    public string EndReason { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    /// <summary>所属 SOP 流程名称。</summary>
    public string WorkflowName { get; set; } = "";
    public string WorkflowId { get; set; } = "";
    /// <summary>操作人（登录用户名）；改动前产生的历史数据该字段为空（归为"未知用户"）。</summary>
    public string Operator { get; set; } = "";
    /// <summary>OK、NG、Review、Fault 或 Aborted。</summary>
    public string Result { get; set; } = "";
    /// <summary>周期总耗时（秒）。</summary>
    public double TotalDurationSec { get; set; }
    public SopStepDuration? InterruptedStep { get; set; }
    public int StepCount { get; set; }
    public List<SopStepDuration> Steps { get; set; } = new();
    public List<SopViolationSummary> Violations { get; set; } = new();
}

    /// <summary>周期内单步耗时记录。</summary>
    public class SopStepDuration
    {
        public int StepId { get; set; }
        public string StepName { get; set; } = "";
        /// <summary>该步骤耗时（秒）。</summary>
        public double DurationSec { get; set; }
        /// <summary>该步骤标准耗时（秒），来自配方 standard_duration_ms（缺省回退 duration_ms）。</summary>
        public double StandardDurationSec { get; set; }
    }

/// <summary>导致 NG 的违规摘要。</summary>
public class SopViolationSummary
{
    public string Type { get; set; } = "";
    public int StepId { get; set; }
    public string Description { get; set; } = "";
}
