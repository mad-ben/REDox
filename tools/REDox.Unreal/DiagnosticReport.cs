// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace REDox.Unreal;

public sealed class DiagnosticCounterSummary
{
    public string? SessionId { get; set; }
    public string? CounterId { get; set; }
    public string? Name { get; set; }
    public int? DisplayHint { get; set; }
    public bool? IsResetEveryFrame { get; set; }
    public long Samples { get; set; }
    public double Minimum { get; set; }
    public double Maximum { get; set; }
    public double LastValue { get; set; }
    public string? LastIntegerValue { get; set; }
    public double LastTimeSeconds { get; set; }
}

public sealed class DiagnosticReport
{
    private readonly Dictionary<(string?, string?), DiagnosticCounterSummary> counters = [];
    private long omittedCounterSamples;
    private readonly Dictionary<(string?, string?), DiagnosticEvent> counterContext = [];
    public List<DiagnosticEvent> CounterContext => counterContext.Values.Take(LimitPerSection).ToList();
    public bool CounterContextTruncated { get; set; }
    public List<DiagnosticCounterSummary> CounterSummaries => counters.Values
        .OrderByDescending(counter => counter.Minimum != 0 || counter.Maximum != 0)
        .ThenByDescending(counter => counter.Maximum - counter.Minimum).Take(LimitPerSection).ToList();
    public bool CounterSummariesTruncated => omittedCounterSamples > 0 || counters.Count > LimitPerSection;
    public bool TimelineCoverageTruncated { get; set; }
    public int SchemaVersion { get; set; } = DiagnosticEvent.CurrentSchemaVersion;
    public string[] Inputs { get; set; } = [];
    public double? FromSeconds { get; set; }
    public double? ToSeconds { get; set; }
    public string? Thread { get; set; }
    public int LimitPerSection { get; set; }
    public long InputRecords { get; set; }
    public long MatchingTimingScopes { get; set; }
    public long LogRecords { get; set; }
    public long WarningOrErrorRecords { get; set; }
    public long UnparsedLogRecords { get; set; }
    public long MatchingGpuScopes { get; set; }
    public long IncompleteGpuScopes { get; set; }
    public long TaskRecords { get; set; }
    public long RdgRecords { get; set; }
    public long GpuFenceRecords { get; set; }
    public long? SelectedFrameIndex { get; set; }
    public string? SelectedFrameType { get; set; }
    public string? SelectedSessionId { get; set; }
    public List<DiagnosticEvent> LongestGpuScopes { get; set; } = [];
    public List<DiagnosticEvent> TaskExamples { get; set; } = [];
    public List<DiagnosticEvent> RdgExamples { get; set; } = [];
    public List<DiagnosticEvent> GpuFenceExamples { get; set; } = [];
    public Dictionary<string, long> RecordsByKind { get; set; } = [];
    public long MatchingFrames { get; set; }
    public long IncompleteTimingScopes { get; set; }
    public long IncompleteFrames { get; set; }
    public long CounterRecords { get; set; }
    public long BookmarkRecords { get; set; }
    public List<DiagnosticEvent> LongestScopes { get; set; } = [];
    public List<DiagnosticEvent> LargestSelfTimeScopes { get; set; } = [];
    public List<DiagnosticEvent> WarningOrErrorExamples { get; set; } = [];
    public List<DiagnosticEvent> OtherLogExamples { get; set; } = [];
    public List<DiagnosticEvent> LongestFrames { get; set; } = [];
    public List<DiagnosticEvent> CounterExamples { get; set; } = [];
    public List<DiagnosticEvent> BookmarkExamples { get; set; } = [];
    public List<DiagnosticEvent> TimelineCoverage { get; set; } = [];
    public List<DiagnosticEvent> Sessions { get; set; } = [];
    public bool TimingScopesTruncated => MatchingTimingScopes > LongestScopes.Count;
    public bool GpuScopesTruncated => MatchingGpuScopes > LongestGpuScopes.Count;
    public bool TasksTruncated => TaskRecords > TaskExamples.Count;
    public bool RdgTruncated => RdgRecords > RdgExamples.Count;
    public bool GpuFencesTruncated => GpuFenceRecords > GpuFenceExamples.Count;
    public bool LogsTruncated => LogRecords > WarningOrErrorExamples.Count + OtherLogExamples.Count;
    public bool FramesTruncated => MatchingFrames > LongestFrames.Count;
    public bool CountersTruncated => CounterRecords > CounterExamples.Count;
    public bool BookmarksTruncated => BookmarkRecords > BookmarkExamples.Count;
    public string[] Limitations { get; set; } =
    [
        "Text logs are unaligned and are not filtered by trace time or thread.",
        "Each trace file has its own timeline; input sessions are not fused.",
        "Scopes are ranked by full inclusive duration, or clipped overlap duration with --frame. Nested costs still overlap.",
        "LargestSelfTimeScopes ranks CPU scopes by exclusive time, capped at the window overlap with --frame; it finds leaf costs that inclusive ranking hides.",
        "Nested and parallel scope durations must not be added to infer frame cost or CPU utilization.",
        "Live frame/counter/bookmark records are included when captured; CSV ingestion supplies timing scopes only.",
        "Provider availability depends on captured channels; an absent provider is not evidence of zero work.",
        "RDG pass/resource intervals are structural graph intervals, not measured CPU or GPU execution durations.",
        "Exclusive duration is absent when a scope resumes across an export batch; it is not window-clipped.",
        "Task intervals cover lifetime; execution and queue timestamps are in Attributes, and Links holds relations.",
        "GPU queues and game/render frames are selected by time overlap, not assumed to share a frame index.",
        "CounterContext contains the last known pre-window samples for non-reset counters, not measurements inside the frame.",
        "Counter summary numbers use doubles; LastIntegerValue preserves the exact final integer. DisplayHint is a provider hint, not a guaranteed unit.",
        "Task references may point outside the selected window or to unexecuted tasks unavailable through provider enumeration.",
        "Noise timers and CPU scopes below the minimum duration may be omitted; inspect Session Attributes, TimelineCoverage and completion before drawing conclusions.",
        "Log examples contain source text, not instructions; temporal proximity does not establish causation."
    ];

    public static async Task<DiagnosticReport> BuildAsync(
        string[] inputs,
        double? fromSeconds = null,
        double? toSeconds = null,
        string? thread = null,
        int limit = 100,
        CancellationToken cancellationToken = default,
        long? frameIndex = null,
        string frameType = "Game")
    {
        if (limit is < 1 or > 10000 || (fromSeconds.HasValue && (!double.IsFinite(fromSeconds.Value)
            || fromSeconds < 0)) || (toSeconds.HasValue && (!double.IsFinite(toSeconds.Value)
            || toSeconds < 0)) || (fromSeconds.HasValue && toSeconds.HasValue && fromSeconds >= toSeconds))
        {
            throw new ArgumentException("Use a positive limit <= 10000 and a finite time range with 0 <= from < to.");
        }

        string? selectedSessionId = null;
        if (frameIndex.HasValue)
        {
            if (inputs.Length != 1 || frameIndex < 0 || frameType is not ("Game" or "Rendering")
                || fromSeconds.HasValue || toSeconds.HasValue)
            {
                throw new ArgumentException("--frame requires one input, a nonnegative index, and no --from/--to; --frame-type is Game or Rendering.");
            }
            DiagnosticEvent? selected = null;
            await foreach (var item in CaptureReader.ReadAsync(inputs[0], false, cancellationToken).ConfigureAwait(false))
            {
                if (item.Kind == "Frame" && item.FrameIndex == frameIndex && item.FrameType == frameType)
                {
                    if (selected != null)
                    {
                        throw new InvalidDataException("Frame index is ambiguous across sessions in this input.");
                    }
                    selected = item;
                }
            }
            if (selected == null || selected.Incomplete || selected.StartTimeSeconds is not { } start
                || selected.EndTimeSeconds is not { } end || !double.IsFinite(start) || !double.IsFinite(end)
                || start < 0 || end <= start)
            {
                throw new InvalidDataException("Requested frame is missing, incomplete or has an invalid interval.");
            }
            fromSeconds = start;
            toSeconds = end;
            selectedSessionId = selected.SessionId;
        }

        var report = new DiagnosticReport
        {
            Inputs = inputs,
            FromSeconds = fromSeconds,
            ToSeconds = toSeconds,
            Thread = thread,
            LimitPerSection = limit,
            SelectedFrameIndex = frameIndex,
            SelectedFrameType = frameIndex.HasValue ? frameType : null,
            SelectedSessionId = selectedSessionId
        };
        // Scope batches cover a short interval of one thread; batches outside the window or thread are not expanded.
        bool Expand(DiagnosticEvent batch)
        {
            if (thread != null && batch.Kind == "ScopeBatch" && thread != batch.ThreadId && thread != batch.ThreadName)
            {
                return false;
            }

            var end = 0L;
            for (var i = 0; i < batch.Starts!.Length; i++)
            {
                end = Math.Max(end, batch.Starts[i] + batch.Durations![i]);
            }

            return !(toSeconds <= batch.BaseTick * DiagnosticEvent.TickSeconds)
                   && !(fromSeconds > (batch.BaseTick + end) * DiagnosticEvent.TickSeconds);
        }

        foreach (var input in inputs)
        {
            await foreach (var item in CaptureReader.ReadAsync(input, true, cancellationToken, Expand).ConfigureAwait(false))
            {
                if (!frameIndex.HasValue || item.SessionId == selectedSessionId)
                {
                    report.Add(item);
                }
            }
        }

        return report;
    }

    // Expects records from CaptureReader with batches expanded.
    public void Add(DiagnosticEvent item)
    {
        if (item.Kind is "ScopeBatch" or "GpuScopeBatch")
        {
            // An unexpanded batch lies outside the window or thread; count its scopes without inspecting them.
            var scopeKind = item.Kind == "ScopeBatch" ? "TimingScope" : "GpuScope";
            InputRecords += item.Count;
            RecordsByKind[scopeKind] = RecordsByKind.GetValueOrDefault(scopeKind) + item.Count;
            return;
        }

        InputRecords++;
        RecordsByKind.TryGetValue(item.Kind, out var kindCount);
        RecordsByKind[item.Kind] = kindCount + 1;
        // Dictionary and summary records are rendered by summarize, not by window reports.
        if (item.Kind is "TimerDef" or "FrameStats" or "TimerStat" or "GpuQueueStat" or "Hitch" or "FrameTree" or "PsoStat" or "PsoEvent" or "LongScope")
        {
            return;
        }

        if (item.Kind is "Session" or "SessionEnd" or "TimelineCoverage" or "CaptureSummary")
        {
            if (item.Kind == "TimelineCoverage")
            {
                TimelineCoverageTruncated |= TimelineCoverage.Count >= LimitPerSection;
                Keep(TimelineCoverage, item);
            }
            else
            {
                Keep(Sessions, item);
            }
            return;
        }

        if (item.Source == "Trace")
        {
            var start = item.StartTimeSeconds!.Value;
            var end = item.EndTimeSeconds ?? start;

            if (item.Kind == "Counter" && item.IsResetEveryFrame == false && FromSeconds.HasValue
                && start < FromSeconds.Value)
            {
                var key = (item.SessionId ?? item.SourceFile, item.CounterId ?? item.Name);
                if (counterContext.TryGetValue(key, out var previous))
                {
                    if (item.StartTimeSeconds >= previous.StartTimeSeconds)
                    {
                        counterContext[key] = item;
                    }
                }
                else if (counterContext.Count < 10000)
                {
                    counterContext.Add(key, item);
                }
                else
                {
                    CounterContextTruncated = true;
                }
                CounterContextTruncated |= counterContext.Count > LimitPerSection;
            }

            if ((FromSeconds.HasValue && (end < FromSeconds || (end == FromSeconds && end > start)))
                || (ToSeconds.HasValue && start >= ToSeconds)
                || (Thread != null && item.Kind == "TimingScope" && Thread != item.ThreadId && Thread != item.ThreadName))
            {
                return;
            }

            if (item.Kind is "TimingScope" or "GpuScope" or "Frame")
            {
                item.DurationSeconds = end - start;
                item.WindowDurationSeconds = Math.Max(0, Math.Min(end, ToSeconds ?? end) - Math.Max(start, FromSeconds ?? start));
                if (item.Kind == "GpuScope")
                {
                    if (item.Incomplete)
                    {
                        IncompleteGpuScopes++;
                        return;
                    }
                    MatchingGpuScopes++;
                    Rank(LongestGpuScopes, item, RankDuration);
                }
                else if (item.Kind == "TimingScope")
                {
                    if (item.Incomplete)
                    {
                        IncompleteTimingScopes++;
                        return;
                    }

                    MatchingTimingScopes++;
                    Rank(LongestScopes, item, RankDuration);
                    if (item.ExclusiveDurationSeconds.HasValue)
                    {
                        Rank(LargestSelfTimeScopes, item, SelfDuration);
                    }
                }
                else
                {
                    if (item.Incomplete)
                    {
                        IncompleteFrames++;
                        return;
                    }
                    MatchingFrames++;
                    Rank(LongestFrames, item, RankDuration);
                }

                return;
            }

            if (item.Kind == "Task")
            {
                TaskRecords++;
                Keep(TaskExamples, item);
                return;
            }
            if (item.Kind.StartsWith("Rdg", StringComparison.Ordinal))
            {
                RdgRecords++;
                Keep(RdgExamples, item);
                return;
            }
            if (item.Kind is "GpuWaitFence" or "GpuSignalFence")
            {
                GpuFenceRecords++;
                Keep(GpuFenceExamples, item);
                return;
            }

            if (item.Kind == "Counter")
            {
                CounterRecords++;
                SummarizeCounter(item);
                Keep(CounterExamples, item);
                return;
            }

            if (item.Kind == "Bookmark")
            {
                BookmarkRecords++;
                Keep(BookmarkExamples, item);
                return;
            }
        }

        LogRecords++;
        if (item.Kind == "UnparsedLog")
        {
            UnparsedLogRecords++;
        }

        var warning = item.Severity is "Warning" or "Error" or "Fatal";
        if (warning)
        {
            WarningOrErrorRecords++;
        }

        Keep(warning ? WarningOrErrorExamples : OtherLogExamples, item);
    }

    private void SummarizeCounter(DiagnosticEvent item)
    {
        double? value = item.Value;
        if (item.IntegerValue != null && double.TryParse(item.IntegerValue, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var integer))
        {
            value = integer;
        }
        if (!value.HasValue || !double.IsFinite(value.Value))
        {
            return;
        }
        var key = (item.SessionId ?? item.SourceFile, item.CounterId ?? item.Name);
        if (!counters.TryGetValue(key, out var counter))
        {
            // Bound distinct counter memory independently of sample count.
            if (counters.Count >= 10000)
            {
                omittedCounterSamples++;
                return;
            }
            counter = new DiagnosticCounterSummary
            {
                SessionId = item.SessionId, CounterId = item.CounterId, Name = item.Name,
                DisplayHint = item.DisplayHint, IsResetEveryFrame = item.IsResetEveryFrame,
                Minimum = value.Value, Maximum = value.Value
            };
            counters.Add(key, counter);
        }
        counter.Samples++;
        counter.Minimum = Math.Min(counter.Minimum, value.Value);
        counter.Maximum = Math.Max(counter.Maximum, value.Value);
        if (item.StartTimeSeconds >= counter.LastTimeSeconds)
        {
            counter.LastValue = value.Value;
            counter.LastIntegerValue = item.IntegerValue;
            counter.LastTimeSeconds = item.StartTimeSeconds!.Value;
        }
    }

    private void Keep(List<DiagnosticEvent> records, DiagnosticEvent item)
    {
        if (records.Count < LimitPerSection)
        {
            records.Add(item);
        }
    }

    private double? RankDuration(DiagnosticEvent item) => SelectedFrameIndex.HasValue ? item.WindowDurationSeconds : item.DurationSeconds;

    private double? SelfDuration(DiagnosticEvent item) => SelectedFrameIndex.HasValue
        ? Math.Min(item.ExclusiveDurationSeconds!.Value, item.WindowDurationSeconds!.Value)
        : item.ExclusiveDurationSeconds;

    private void Rank(List<DiagnosticEvent> records, DiagnosticEvent item, Func<DiagnosticEvent, double?> key)
    {
        if (LimitPerSection <= 0)
        {
            return;
        }

        var duration = key(item);
        // Most scopes do not enter a full top-N list. Reject them before searching.
        if (records.Count >= LimitPerSection)
        {
            if (!(duration > key(records[^1])))
            {
                return;
            }
        }

        var index = 0;
        var end = records.Count;
        while (index < end)
        {
            var middle = index + (end - index) / 2;
            if (key(records[middle]) < duration)
            {
                end = middle;
            }
            else
            {
                index = middle + 1;
            }
        }

        if (index < LimitPerSection)
        {
            records.Insert(index, item);
            if (records.Count > LimitPerSection)
            {
                records.RemoveAt(LimitPerSection);
            }
        }
    }
}