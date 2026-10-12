// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace REDox.Unreal;

public static partial class LogImporter
{
    public static async IAsyncEnumerable<DiagnosticEvent> ReadAsync(TextReader reader, string sourceFile,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        DiagnosticEvent? pending = null;
        var lineNumber = 0L;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lineNumber++;
            // Engine lines may omit the category, as in "[time][frame]Message"; a timestamp or a category marks a new entry.
            var match = LogLine().Match(line);
            var parsed = match.Groups["timestamp"].Success || match.Groups["category"].Success;

            if (!parsed && pending != null && (line.Length == 0 || char.IsWhiteSpace(line[0])))
            {
                pending.Message += "\n" + line;
                pending.SourceEndLine = lineNumber;
                continue;
            }

            if (pending != null)
            {
                yield return pending;
            }

            pending = new DiagnosticEvent
            {
                Source = "Log",
                Kind = parsed ? "Log" : "UnparsedLog",
                SourceFile = sourceFile,
                SourceRecord = lineNumber,
                SourceEndLine = lineNumber,
                Message = parsed ? match.Groups["message"].Value : line
            };

            if (parsed)
            {
                pending.Timestamp = match.Groups["timestamp"].Success ? match.Groups["timestamp"].Value : null;
                pending.LogFrame = int.TryParse(match.Groups["frame"].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var frame) ? frame : null;
                pending.Category = match.Groups["category"].Success ? match.Groups["category"].Value : null;
                pending.Severity = match.Groups["severity"].Success ? match.Groups["severity"].Value : null;
            }
        }

        if (pending != null)
        {
            yield return pending;
        }
    }

    [GeneratedRegex(@"^(?:\[(?<timestamp>\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2}:\d+)\])?(?:\[\s*(?<frame>\d+)\s*\])?(?:(?<category>[^\s:\[\]]+):\s*(?:(?<severity>Fatal|Error|Warning|Display|VeryVerbose|Verbose|Log):\s*)?)?(?<message>.*)$")]
    private static partial Regex LogLine();
}