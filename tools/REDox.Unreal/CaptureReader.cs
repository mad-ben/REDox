// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using REDox.Json;
using REDox.Serialization;

namespace REDox.Unreal;

// Protocol 3 sends SessionId, SourceFile and Source once; later trace records inherit them.
public sealed class CaptureContext
{
    private readonly Dictionary<int, DiagnosticEvent> timers = [];

    public DiagnosticEvent? Session { get; private set; }
    public IReadOnlyDictionary<int, DiagnosticEvent> Timers => timers;

    public DiagnosticEvent Apply(DiagnosticEvent item)
    {
        if (item.Kind == "Session")
        {
            Session = item;
            timers.Clear();
        }
        else if (item.Source.Length == 0 && Session is not null)
        {
            item.Source = Session.Source;
            item.SessionId ??= Session.SessionId;
            item.SourceFile = Session.SourceFile;
        }

        item.Validate();
        if (item.Kind == "TimerDef")
        {
            timers[item.DefId!.Value] = item;
        }

        return item;
    }
}

public static class CaptureReader
{
    public static JsonDocumentOptions Options { get; } = new() { UseNewlineDelimitedFormat = true };

    // Records leave out absent fields when written.
    public static DoxSerializerSettings WriteSettings { get; } = new() { NullValueHandling = NullValueHandling.IgnoreWrite };

    // Batches are expanded into TimingScope/GpuScope/Counter records only when requested. A scope batch
    // rejected by expandScopes is returned unexpanded, so callers can skip work outside a window.
    public static async IAsyncEnumerable<DiagnosticEvent> ReadAsync(string input, bool expand,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        Func<DiagnosticEvent, bool>? expandScopes = null)
    {
        var context = new CaptureContext();
        await using var stream = File.OpenRead(input);
        await foreach (var record in JsonSequence.DeserializeAsync<DiagnosticEvent>(stream, options: Options,
                           cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            var item = context.Apply(record ?? throw new InvalidDataException($"{input}: null diagnostic record."));
            if (expand && item.Kind is "ScopeBatch" or "GpuScopeBatch" && expandScopes?.Invoke(item) != false)
            {
                foreach (var scope in item.ExpandScopes(context.Timers))
                {
                    yield return scope;
                }
            }
            else if (expand && item.Kind == "CounterBatch")
            {
                foreach (var sample in item.ExpandCounters())
                {
                    yield return sample;
                }
            }
            else
            {
                yield return item;
            }
        }
    }
}