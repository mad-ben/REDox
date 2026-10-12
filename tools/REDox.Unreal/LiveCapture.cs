// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using REDox.Json;

namespace REDox.Unreal;

public static class LiveCapture
{
    public const int ProtocolVersion = 3;

    public static async Task ReceiveAsync(string outputPath, bool writeSummary, CancellationToken cancellationToken = default)
    {
        outputPath = Path.GetFullPath(outputPath);
        if (!outputPath.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Live output must end in .ndjson.");
        }

        var basePath = outputPath[..^7];
        var partialPath = basePath + ".partial.ndjson";
        var summaryPath = basePath + ".summary.md";
        if (File.Exists(outputPath) || File.Exists(partialPath) || (writeSummary && File.Exists(summaryPath)))
        {
            throw new IOException("Capture output already exists.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var context = new CaptureContext();
        var summary = writeSummary ? new LlmSummary() : null;
        var statusClock = Stopwatch.StartNew();
        var receivedRecords = 0L;
        var logicalEvents = 0L;
        var ended = false;
        var completed = false;
        await using (var file = new FileStream(partialPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 65536))
        {
            using var input = Console.OpenStandardInput();
            Console.WriteLine($"REDOX_READY {ProtocolVersion}");
            await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
            // Archive the original UTF-8 chunks while REDox parses and validates them.
            await using var stream = new ArchiveReadStream(input, file);
            await foreach (var record in JsonSequence.DeserializeAsync<DiagnosticEvent>(stream,
                               options: CaptureReader.Options, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (ended)
                {
                    throw new InvalidDataException("Live input contains records after SessionEnd.");
                }

                var item = record ?? throw new InvalidDataException("Null live record.");
                if (receivedRecords == 0 && (item.Kind != "Session" || item.ProtocolVersion != ProtocolVersion
                                             || string.IsNullOrEmpty(item.SessionId)))
                {
                    throw new InvalidDataException($"Expected a protocol-{ProtocolVersion} Session record. Rebuild Unreal Insights.");
                }

                if (item.SourceRecord != receivedRecords + 1 || (receivedRecords > 0 && (item.Kind == "Session"
                        || (item.SessionId is { } sessionId && sessionId != context.Session!.SessionId))))
                {
                    throw new InvalidDataException("Live session or record sequence changed unexpectedly.");
                }

                context.Apply(item);
                receivedRecords++;
                logicalEvents += item.Count;
                summary?.Add(item);
                if (item.Kind == "SessionEnd")
                {
                    // The producer closes stdin after SessionEnd. Validate any read-ahead
                    // records before allowing the raw-byte archive to finalize.
                    ended = true;
                    completed = item.Completed == true;
                    continue;
                }

                if (receivedRecords == 2 || statusClock.Elapsed.TotalSeconds >= 1)
                {
                    await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                    Console.WriteLine($"REDOX_LIVE {receivedRecords} {logicalEvents}");
                    await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
                    statusClock.Restart();
                }
            }

            if (!ended)
            {
                throw new InvalidDataException("Live input ended without SessionEnd; the partial capture is retained.");
            }

            // A producer may omit its final newline. Keep the summary record on its own NDJSON line.
            file.Seek(-1, SeekOrigin.End);
            var lastByte = file.ReadByte();
            file.Seek(0, SeekOrigin.End);
            var captureSummary = new DiagnosticEvent
            {
                Source = "Trace", Kind = "CaptureSummary", SessionId = context.Session!.SessionId,
                SourceFile = outputPath, SourceRecord = receivedRecords + 1, Completed = completed,
                Attributes = new Dictionary<string, string>
                {
                    ["ReceivedRecords"] = receivedRecords.ToString(CultureInfo.InvariantCulture),
                    ["LogicalEvents"] = logicalEvents.ToString(CultureInfo.InvariantCulture)
                }
            };
            var line = (lastByte == '\n' ? "" : "\n") + JsonSerializer.Serialize(captureSummary, CaptureReader.WriteSettings) + "\n";
            await file.WriteAsync(Encoding.UTF8.GetBytes(line), cancellationToken).ConfigureAwait(false);
        }

        File.Move(partialPath, outputPath);
        if (summary is not null)
        {
            await File.WriteAllTextAsync(summaryPath, summary.Render(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine($"REDOX_DONE {receivedRecords} {logicalEvents}");
        await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    // This wrapper leaves both streams open; ReceiveAsync owns their lifetimes.
    private sealed class ArchiveReadStream(Stream input, Stream archive) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = input.Read(buffer);
            if (count > 0)
            {
                archive.Write(buffer[..count]);
            }

            return count;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count > 0)
            {
                await archive.WriteAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
            }

            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}