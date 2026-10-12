// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using REDox.Serialization;

namespace REDox.Unreal;

public sealed class DiagnosticEvent
{
    public const int CurrentSchemaVersion = 3;

    // Protocol 3 scope, counter and frame-tree times are integer 100 ns ticks.
    public const double TickSeconds = 1e-7;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public int? ProtocolVersion { get; set; }
    public string? SessionId { get; set; }
    public string Source { get; set; } = "";
    public string Kind { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public long SourceRecord { get; set; }
    public long? SourceEndLine { get; set; }
    public string? Timestamp { get; set; }
    public int? LogFrame { get; set; }
    public double? StartTimeSeconds { get; set; }
    public double? EndTimeSeconds { get; set; }
    public double? DurationSeconds { get; set; }
    public string? ThreadId { get; set; }
    public string? ThreadName { get; set; }
    public string? TimerId { get; set; }
    public int? DefId { get; set; }
    public string? Name { get; set; }
    public string? EventName { get; set; }
    public double? ExclusiveDurationSeconds { get; set; }
    public double? WindowDurationSeconds { get; set; }
    public string? QueueId { get; set; }
    public string? RelatedQueueId { get; set; }
    public string? TaskId { get; set; }
    public string? GraphId { get; set; }
    public string? Description { get; set; }
    public int? DisplayHint { get; set; }
    public bool SyntheticTiming { get; set; }
    public Dictionary<string, string>? Attributes { get; set; }
    public Dictionary<string, string[]>? Links { get; set; }
    public Dictionary<string, long>? Metrics { get; set; }
    public int? Depth { get; set; }
    public string? Category { get; set; }
    public string? Severity { get; set; }
    public string? Message { get; set; }
    public long? FrameIndex { get; set; }
    public string? FrameType { get; set; }
    public string? CounterId { get; set; }
    public int? SampleIndex { get; set; }
    public long[]? SampleTicks { get; set; }
    public double?[]? SampleValues { get; set; }
    public string[]? SampleIntegers { get; set; }
    public double? Value { get; set; }
    public string? IntegerValue { get; set; }
    public bool? IsResetEveryFrame { get; set; }
    public long? BaseTick { get; set; }
    public int[]? Defs { get; set; }
    public long[]? Starts { get; set; }
    public long[]? Durations { get; set; }
    public int[]? Depths { get; set; }
    public long[]? Exclusive { get; set; }
    public int[]? IncompleteIndices { get; set; }
    public int[]? TaskIndices { get; set; }
    public string[]? TaskIds { get; set; }
    public int[]? WaitIndices { get; set; }
    public string[][]? WaitedTasks { get; set; }
    public string? CodeFile { get; set; }
    public int? CodeLine { get; set; }
    public bool Incomplete { get; set; }
    public bool? Completed { get; set; }

    [DataIgnore]
    public long Count => Kind switch
    {
        "CounterBatch" => SampleTicks!.Length,
        "ScopeBatch" or "GpuScopeBatch" or "FrameTree" => Defs!.Length,
        _ => 1
    };

    // Expand only for offline analysis; captures retain the compact arrays.
    public IEnumerable<DiagnosticEvent> ExpandCounters()
    {
        for (var index = 0; index < SampleTicks!.Length; index++)
        {
            var incomplete = SampleValues is not null && SampleValues[index] is null;
            yield return new DiagnosticEvent
            {
                SchemaVersion = SchemaVersion, Source = Source, Kind = "Counter",
                SessionId = SessionId, SourceFile = SourceFile, SourceRecord = SourceRecord,
                CounterId = CounterId, Name = Name, Category = Category, Description = Description,
                DisplayHint = DisplayHint, IsResetEveryFrame = IsResetEveryFrame,
                SampleIndex = index, StartTimeSeconds = SampleTicks[index] * TickSeconds, Value = SampleValues?[index],
                IntegerValue = SampleIntegers?[index], Incomplete = incomplete,
                Message = incomplete ? "Non-finite counter value omitted." : null
            };
        }
    }

    // Resolves TimerDef ids into individual scopes. Unknown ids keep only DefId.
    public IEnumerable<DiagnosticEvent> ExpandScopes(IReadOnlyDictionary<int, DiagnosticEvent> timers)
    {
        var gpu = Kind == "GpuScopeBatch";
        var exclusive = Exclusive;
        var incomplete = IncompleteIndices is null ? null : new HashSet<int>(IncompleteIndices);
        var tasks = Map(TaskIndices, TaskIds);
        var waits = Map(WaitIndices, WaitedTasks);
        for (var index = 0; index < Defs!.Length; index++)
        {
            timers.TryGetValue(Defs[index], out var timer);
            var start = (BaseTick!.Value + Starts![index]) * TickSeconds;
            var duration = Durations![index] * TickSeconds;
            yield return new DiagnosticEvent
            {
                SchemaVersion = SchemaVersion, Source = Source, Kind = gpu ? "GpuScope" : "TimingScope",
                SessionId = SessionId, SourceFile = SourceFile, SourceRecord = SourceRecord, SampleIndex = index,
                ThreadId = ThreadId, QueueId = QueueId, ThreadName = ThreadName, FrameIndex = FrameIndex,
                Category = Category, DefId = Defs[index], TimerId = timer?.TimerId, Name = timer?.Name,
                EventName = timer?.EventName, CodeFile = timer?.CodeFile, CodeLine = timer?.CodeLine,
                StartTimeSeconds = start, EndTimeSeconds = start + duration, DurationSeconds = duration,
                Depth = Depths![index],
                ExclusiveDurationSeconds = exclusive is not null && exclusive[index] >= 0 ? exclusive[index] * TickSeconds : null,
                Incomplete = incomplete?.Contains(index) == true,
                TaskId = tasks?.GetValueOrDefault(index),
                Links = waits?.GetValueOrDefault(index) is { } waited ? new Dictionary<string, string[]> { ["WaitedTasks"] = waited } : null
            };
        }
    }

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported schema {SchemaVersion}; re-export the trace with the current REDox.Unreal and Insights.");
        }

        if (!((Source == "Log" && Kind is "Log" or "UnparsedLog")
            || (Source == "Trace" && Kind is "Session" or "SessionEnd" or "CaptureSummary" or "TimelineCoverage"
                or "TimerDef" or "ScopeBatch" or "GpuScopeBatch" or "TimingScope" or "GpuScope" or "Frame"
                or "TraceLog" or "Bookmark" or "CounterBatch" or "Counter" or "GpuWaitFence" or "GpuSignalFence"
                or "Task" or "RdgGraph" or "RdgPass" or "RdgScope" or "RdgTexture" or "RdgBuffer"
                or "FrameStats" or "TimerStat" or "GpuQueueStat" or "Hitch" or "FrameTree" or "PsoStat" or "PsoEvent" or "LongScope")))
        {
            throw new InvalidDataException($"Unsupported diagnostic record: {Source}/{Kind}.");
        }

        switch (Kind)
        {
            case "CounterBatch":
                ValidateCounterBatch();
                return;
            case "ScopeBatch" or "GpuScopeBatch" or "FrameTree":
                ValidateScopeBatch();
                return;
            case "TimerDef" when DefId is not >= 0:
                throw new InvalidDataException("TimerDef requires a nonnegative DefId.");
        }

        if (Source != "Trace" || Kind is "Session" or "SessionEnd" or "CaptureSummary" or "TimelineCoverage"
            or "TimerDef" or "FrameStats" or "TimerStat" or "GpuQueueStat" or "PsoStat")
        {
            return;
        }

        if (StartTimeSeconds is not { } start || !double.IsFinite(start) || start < 0)
        {
            throw new InvalidDataException("Trace records require finite nonnegative timestamps.");
        }

        var end = EndTimeSeconds ?? start;
        if (!double.IsFinite(end) || end < start)
        {
            throw new InvalidDataException("Invalid timing interval.");
        }

        if (Kind is "TimingScope" or "GpuScope" or "Frame")
        {
            DurationSeconds = end - start;
        }
    }

    private void ValidateCounterBatch()
    {
        if (SampleTicks is not { Length: > 0 and <= 512 } ticks
            || (SampleValues is null) == (SampleIntegers is null)
            || (SampleValues?.Length ?? SampleIntegers!.Length) != ticks.Length)
        {
            throw new InvalidDataException("Invalid counter batch arrays.");
        }

        for (var index = 0; index < ticks.Length; index++)
        {
            if (ticks[index] < 0 || (index > 0 && ticks[index] < ticks[index - 1])
                || (SampleValues?[index] is { } value && !double.IsFinite(value))
                || (SampleIntegers is not null && !long.TryParse(SampleIntegers[index],
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
            {
                throw new InvalidDataException("Invalid counter batch sample.");
            }
        }
    }

    private void ValidateScopeBatch()
    {
        var count = Defs?.Length ?? -1;
        if (count <= 0 || BaseTick is not >= 0 || Starts?.Length != count || Durations?.Length != count
            || Depths?.Length != count || (Exclusive is not null && Exclusive.Length != count)
            || (TaskIndices?.Length ?? 0) != (TaskIds?.Length ?? 0)
            || (WaitIndices?.Length ?? 0) != (WaitedTasks?.Length ?? 0))
        {
            throw new InvalidDataException("Invalid scope batch arrays.");
        }

        for (var index = 0; index < count; index++)
        {
            // Frame trees are relative to the frame start and may include parents that began earlier.
            if (Durations[index] < 0 || Depths[index] < 0 || (Kind != "FrameTree" && Starts[index] < 0))
            {
                throw new InvalidDataException("Invalid scope batch entry.");
            }
        }

        if (!InRange(IncompleteIndices) || !InRange(TaskIndices) || !InRange(WaitIndices))
        {
            throw new InvalidDataException("Scope batch index out of range.");
        }

        bool InRange(int[]? indices) => indices is null || Array.TrueForAll(indices, index => index >= 0 && index < count);
    }

    private static Dictionary<int, T>? Map<T>(int[]? indices, T[]? values)
    {
        if (indices is null || values is null)
        {
            return null;
        }

        var map = new Dictionary<int, T>(indices.Length);
        for (var i = 0; i < indices.Length; i++)
        {
            map[indices[i]] = values[i];
        }

        return map;
    }
}