// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using REDox.Csv;

namespace REDox.Unreal;

public static class InsightsImporter
{
    public static async IAsyncEnumerable<DiagnosticEvent> ReadAsync(
        Stream stream,
        string sourceFile,
        char separator = ',',
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var recordNumber = 1L;
        var options = new CsvDocumentOptions { HasHeaderRecord = true, SeparatorChar = separator };
        await foreach (var row in CsvSequence.ParseAsync(stream, options: options,
                           cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            recordNumber++;
            var start = Number(row, "StartTime", sourceFile, recordNumber);
            var end = Number(row, "EndTime", sourceFile, recordNumber);
            if (start < 0 || end < start)
            {
                throw new InvalidDataException($"{sourceFile}: record {recordNumber}: invalid timing interval.");
            }

            var depthText = Text(row, "Depth");
            int? depth = null;
            if (depthText != null)
            {
                if (!int.TryParse(depthText, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                {
                    throw new InvalidDataException($"{sourceFile}: record {recordNumber}: invalid Depth.");
                }

                depth = value;
            }

            var threadId = Text(row, "ThreadId");
            var threadName = Text(row, "ThreadName");
            var timerId = Text(row, "TimerId");
            var timerName = Text(row, "TimerName");

            if ((threadId == null && threadName == null) || (timerId == null && timerName == null))
            {
                throw new InvalidDataException($"{sourceFile}: record {recordNumber}: thread and timer identifiers or names are required.");
            }

            yield return new DiagnosticEvent
            {
                Source = "Trace",
                Kind = "TimingScope",
                SourceFile = sourceFile,
                SourceRecord = recordNumber,
                StartTimeSeconds = start,
                EndTimeSeconds = end,
                DurationSeconds = end - start,
                ThreadId = threadId,
                ThreadName = threadName,
                TimerId = timerId,
                Name = timerName,
                Depth = depth
            };
        }
    }

    private static string? Text(DElement row, string name)
    {
        return row.TryGetProperty(name, out var value) && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
    }

    private static double Number(DElement row, string name, string sourceFile, long recordNumber)
    {
        if (!double.TryParse(Text(row, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            throw new InvalidDataException($"{sourceFile}: record {recordNumber}: missing or invalid {name}.");
        }

        return value;
    }
}