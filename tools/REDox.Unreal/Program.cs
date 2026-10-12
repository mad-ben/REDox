// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using REDox.Json;

namespace REDox.Unreal;

internal static class Program
{
    private const string Help = """
        REDox.Unreal - Unreal diagnostic data

        engines [--config <redox-unreal.json>] [--engine <name>]
        receive-live (--out <capture.ndjson> | --output-dir <folder>) [--summary]
        summarize <capture.ndjson | log.ndjson> [--out <summary.md>]
        ingest-log <Game.log> [--out <events.ndjson>]
        ingest-insights <TimingEvents.csv> [--tsv] [--out <events.ndjson>]
        report <events.ndjson>... [--from <seconds>] [--to <seconds>]
               [--frame <index>] [--frame-type <Game|Rendering>]
               [--thread <id-or-name>] [--limit <1-10000>] [--out <report.json>]

        Offline output defaults to stdout; receive-live requires an output file or folder.
        receive-live --summary also writes <capture>.summary.md; summarize renders it from a finished capture,
        or groups the warnings and errors of an ingest-log output.
        Output files must not already exist.
        Insights input requires StartTime/EndTime in seconds and thread/timer identifiers or names.
        Text logs retain their original clock and are not aligned to trace time.
        engines lists detected/custom installations as JSON; --engine selects one by name.
        """;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine(Help);
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        string? temporaryPath = null;
        try
        {
            var command = args[0];
            if (command == "receive-live")
            {
                var liveOptions = new Dictionary<string, string>(StringComparer.Ordinal);
                var writeSummary = false;
                for (var i = 1; i < args.Length; i++)
                {
                    var option = args[i];
                    if (option == "--summary" && !writeSummary)
                    {
                        writeSummary = true;
                        continue;
                    }

                    if (option is not ("--out" or "--output-dir")
                        || liveOptions.ContainsKey(option) || ++i == args.Length
                        || args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException("receive-live accepts --out or --output-dir, and optionally --summary.");
                    }

                    liveOptions.Add(option, args[i]);
                }

                var hasFile = liveOptions.TryGetValue("--out", out var liveOutput);
                var hasDirectory = liveOptions.TryGetValue("--output-dir", out var liveDirectory);
                if (hasFile == hasDirectory)
                {
                    throw new ArgumentException("Specify exactly one of --out <capture.ndjson> or --output-dir <folder>.");
                }

                liveOutput ??= Path.Combine(liveDirectory!, $"REDox-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.ndjson");
                await LiveCapture.ReceiveAsync(liveOutput, writeSummary, cancellation.Token).ConfigureAwait(false);
                return 0;
            }

            if (command == "engines")
            {
                string? configFile = null;
                string? engineName = null;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 1; i < args.Length; i++)
                {
                    var option = args[i];
                    if (option is not ("--config" or "--engine") || !seen.Add(option))
                    {
                        throw new ArgumentException($"Unknown or repeated engines option: {option}.");
                    }

                    if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Missing value for {option}.");
                    }

                    if (option == "--config")
                    {
                        configFile = args[i];
                    }
                    else
                    {
                        engineName = args[i];
                    }
                }

                var catalog = EngineDiscovery.Discover(configFile);
                Console.WriteLine(engineName == null
                    ? JsonSerializer.Serialize(catalog)
                    : JsonSerializer.Serialize(EngineDiscovery.Select(catalog, engineName)));
                return 0;
            }

            if (command is not ("ingest-log" or "ingest-insights" or "report" or "summarize"))
            {
                throw new ArgumentException($"Unknown command: {command}. Use --help.");
            }

            var inputs = new List<string>();
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 1; i < args.Length; i++)
            {
                var argument = args[i];
                if (!argument.StartsWith("--", StringComparison.Ordinal))
                {
                    inputs.Add(Path.GetFullPath(argument));
                    continue;
                }

                if (argument == "--tsv" && command == "ingest-insights")
                {
                    options.Add(argument, "true");
                }
                else if (argument == "--out" || (command == "report" && argument is "--from" or "--to" or "--thread" or "--limit" or "--frame" or "--frame-type"))
                {

                    if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Missing value for {argument}.");
                    }

                    options.Add(argument, args[i]);
                }
                else
                {
                    throw new ArgumentException($"Unknown option for {command}: {argument}.");
                }
            }

            if (inputs.Count == 0 || (command != "report" && inputs.Count != 1))
            {
                throw new ArgumentException("Specify one input for ingestion, or one or more inputs for report.");
            }

            var from = Number(options, "--from");
            var to = Number(options, "--to");
            var limit = options.TryGetValue("--limit", out var limitText) ? int.Parse(limitText, NumberStyles.None, CultureInfo.InvariantCulture) : 100;

            options.TryGetValue("--thread", out var thread);

            var outputPath = options.TryGetValue("--out", out var output) ? Path.GetFullPath(output) : null;

            if (outputPath != null && File.Exists(outputPath))
            {
                throw new IOException($"Output already exists: {outputPath}.");
            }

            if (outputPath != null)
            {
                temporaryPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            }

            var writer = temporaryPath == null ? Console.Out : new StreamWriter(new FileStream(temporaryPath, FileMode.CreateNew), new UTF8Encoding(false));
            try
            {
                if (command == "summarize")
                {
                    await writer.WriteAsync(await LlmSummary.BuildAsync(inputs[0], cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
                }
                else if (command == "report")
                {
                    if (options.ContainsKey("--frame-type") && !options.ContainsKey("--frame"))
                    {
                        throw new ArgumentException("--frame-type requires --frame.");
                    }
                    long? frame = options.TryGetValue("--frame", out var frameText)
                        ? long.Parse(frameText, NumberStyles.None, CultureInfo.InvariantCulture) : null;
                    var frameType = options.TryGetValue("--frame-type", out var typeText) ? typeText : "Game";
                    var report = await DiagnosticReport.BuildAsync(inputs.ToArray(), from, to, thread, limit,
                        cancellation.Token, frame, frameType).ConfigureAwait(false);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(report, CaptureReader.WriteSettings)).ConfigureAwait(false);
                }
                else
                {
                    await using var stream = File.OpenRead(inputs[0]);
                    using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true);
                    var events = command == "ingest-log"
                        ? LogImporter.ReadAsync(reader, inputs[0], cancellation.Token)
                        : InsightsImporter.ReadAsync(stream, inputs[0], options.ContainsKey("--tsv") ? '\t' : ',',
                            cancellation.Token);
                    await foreach (var item in events.ConfigureAwait(false))
                    {
                        await writer.WriteLineAsync(JsonSerializer.Serialize(item, CaptureReader.WriteSettings)).ConfigureAwait(false);
                    }
                }

                await writer.FlushAsync(cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                if (temporaryPath != null)
                {
                    await writer.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (temporaryPath != null)
            {
                File.Move(temporaryPath, outputPath!);
                temporaryPath = null;
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException
                                         or UnauthorizedAccessException
                                         or FormatException or OverflowException or DocumentParseException
                                         or REDox.Serialization.SerializationException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        finally
        {
            if (temporaryPath != null && File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static double? Number(Dictionary<string, string> options, string key)
    {
        return options.TryGetValue(key, out var value)
            ? double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)
            : null;
    }
}