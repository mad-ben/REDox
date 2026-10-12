# REDox.Unreal

A .NET 10 CLI that turns Unreal Insights traces and text logs into compact diagnostic data for LLM analysis. It references REDox projects without modifying their code.

## Live export from Unreal Insights

1. Open Insights. In the **REDox** dropdown beside **Open Trace**, choose `REDox.Unreal.exe` and an output folder, tick the categories to export, and enable REDox. Settings are saved in `UnrealInsightsFrontendSettings.ini`, section `[Insights.REDox]`.
2. Launch the game as usual, for example:

```cmd
G:\XFSDK_Game\XBuild\Windows\XGame.exe -trace=cpu,gpu,frame,rdg,log,bookmark -tracehost=127.0.0.1
```

3. For each live session the frontend starts a background analysis process (`-OpenTraceId -NoUI -AutoQuit` plus the REDox flags). That process streams protocol-3 records to `REDox.Unreal.exe receive-live`.
4. Status goes **Starting → Ready → Live → Draining → Complete**. When the trace ends, Insights computes the summary, and the receiver writes:

```text
REDox-<timestamp>-<session>.ndjson      capture archive (protocol 3)
REDox-<timestamp>-<session>.summary.md  LLM summary (when "LLM summary" is ticked)
```

The `.utrace` recorded by UnrealTraceServer is unchanged and stays the lossless archive. Closing Insights early finalizes an incomplete capture without a summary. Existing outputs are never overwritten.

### Export categories

| Category | Default | Contents |
| --- | --- | --- |
| LLM summary | on | Frame percentiles, per-thread-group CPU timer stats and per-queue GPU busy time for each phase; the worst hitches, startup and a typical frame per phase, with deltas, waits, per-worker self time and scope trees; PSO precache results with the passes and materials that missed. |
| Frames | on | Every game and rendering frame interval. Needed for `report --frame`. |
| CPU scopes | on | Raw CPU scopes in columnar batches. Needed for drill-down reports. |
| GPU scopes | on | Raw GPU scopes per queue. |
| GPU fences | off | Queue signal/wait fences. |
| Tasks | off | Task biographies and task/wait links on CPU scopes (`task` channel). |
| RDG graphs | on | One summary per render graph: pass, texture and buffer counts, transient memory. |
| RDG passes and resources | off | Every RDG pass, scope, texture and buffer (large). |
| Logs, Bookmarks, Counters | on | When the matching trace channels are enabled. Counters store value changes only. |
| Keep noise scopes | off | Keep timers matching the exclude patterns. |

**Exclude timers** (wildcards separated by `;`) drops matching timers from raw scopes, summary tables and trees. Their time still counts in their parents. The default is `FRHICommand*;TRHILambdaCommand*;BeginBreadcrumbGPU;EndBreadcrumbGPU;TaskWorkerIsLookingForWork;FWaitingQueue_Unpark*`.

**Min CPU scope (us)** drops shorter completed CPU scopes from the raw export only. **Hitch threshold (ms)** of 0 judges each game frame against the median of the 60 frames on either side: a frame is slow above `max(1.5 x local median, local median + 4 ms)`. Frames of at least `max(100 ms, 10 x local median)` (loads, mode switches) split the session. Between two splits, a phase runs from the first to the last run of 30 frames that are not slow. Frames before the first phase are startup, frames after the last are shutdown, and other frames outside phases are transitions. Consecutive slow frames form one hitch, and hitches are ranked by their time above the local median. A hitch outside a phase is compared with the phase whose median is closest to its local median. Phase tables and a typical frame are kept for the four longest phases. **Top timers per group** keeps that many timers by inclusive time plus that many by exclusive time. **Hitch frames analysed** caps the hitch breakdowns. The summary lists every hitch in an index; the top three get tables, pooled worker lines and trees (GameThread, RenderThread, RHIThread), lower hitches at least 20 ms above their local median get a table, and the rest stay in the index.

The `rhicommands` and `rendercommands` channels produce one scope per RHI or render command. Most are excluded as noise, but Insights still has to analyze them, so leave those channels off unless you need them.

### Insights flags

The frontend passes these automatically. Use them directly to export a trace from the command line.

| Flag | Meaning |
| --- | --- |
| `-Redox` | Attach the exporter to the analysis session. |
| `-RedoxExe="<path>"` | REDox.Unreal executable. |
| `-RedoxOutput="<folder>"` | Output folder; defaults to the Insights `Saved/REDox`. |
| `-RedoxExports="Summary,Frames,..."` | Enabled categories (names from the table above, without spaces). |
| `-RedoxExclude="<patterns>"` | Exclude patterns. |
| `-RedoxMinScopeUs=`, `-RedoxHitchMs=`, `-RedoxTopTimers=`, `-RedoxMaxHitches=` | Numeric options. |
| `-RedoxOwnerPid=<pid>`, `-RedoxStatus="<file>"` | Frontend lifetime owner and status file. |

```powershell
UnrealInsights.exe -OpenTraceFile="D:/Captures/session.utrace" -NoUI -AutoQuit -Redox -RedoxExe="E:/Project Files/Github/REDox/tools/REDox.Unreal/bin/Debug/net10.0/REDox.Unreal.exe" -RedoxOutput="D:/Diagnostics" -RedoxExports="Summary,Frames,Logs"
```

## Commands

```text
REDox.Unreal.exe receive-live (--out <capture.ndjson> | --output-dir <folder>) [--summary]
REDox.Unreal.exe summarize <capture.ndjson | log.ndjson> [--out <summary.md>]
REDox.Unreal.exe report <events.ndjson>... [--from <seconds>] [--to <seconds>]
                       [--frame <index>] [--frame-type <Game|Rendering>]
                       [--thread <id-or-name>] [--limit <1-10000>] [--out <report.json>]
REDox.Unreal.exe ingest-log <Game.log> [--out <events.ndjson>]
REDox.Unreal.exe ingest-insights <TimingEvents.csv> [--tsv] [--out <events.ndjson>]
REDox.Unreal.exe engines [--config <file>] [--engine <name>]
```

- `summarize` renders the LLM summary from a finished capture. Give the `.summary.md` to the LLM first.
  It is plain Markdown tables, chosen for model accuracy. Tokens are saved by removing redundancy: approximate medians, near-zero rows, wait rows in work tables, pass-through tree wrappers (`A > B`), scopes under 1% of the frame, and repeated sibling scopes (`x12`) are dropped or merged, and precision follows magnitude.
- `summarize` on an `ingest-log` output writes a log summary: warnings and errors grouped by message with numbers merged, with counts and the first timestamp.
- `report` returns JSON for a time window or one frame (`--frame` selects events overlapping that frame). Use it to drill into a frame the summary points at. `LongestScopes` ranks by inclusive time and `LargestSelfTimeScopes` by self time, which surfaces leaf costs hidden under wrappers. Scope batches outside the window or `--thread` are counted but not expanded. Absent fields are omitted from JSON output.
- Output defaults to stdout. `--out` files must not exist. Exit codes: 0 success, 1 invalid input or I/O failure, 130 cancelled.

`receive-live` is launched by Insights. It reads protocol-3 NDJSON from stdin, archives the bytes as they arrive, and validates every record. It replies `REDOX_READY 3`, `REDOX_LIVE <records> <events>` and `REDOX_DONE <records> <events>` on stdout. A missing `SessionEnd` keeps the `.partial.ndjson`.

## Capture format (schema 3)

One NDJSON record per line. The first record is `Session`. It carries `SchemaVersion`, `ProtocolVersion`, `SessionId`, `SourceFile` and the export settings in `Attributes`, and later records inherit those fields. `SourceRecord` numbers records contiguously.

| Kind | Contents |
| --- | --- |
| `TimerDef` | Timer dictionary entry: `DefId`, `TimerId`, `Name`, optional `EventName` (formatted metadata, for example RDG pass names) and code location. After 32 distinct names for one timer, standalone numbers in its names become `#`. |
| `ScopeBatch`, `GpuScopeBatch` | Up to 4096 scopes of one thread or queue as parallel arrays, in end order (children before their parent): `Defs`, `Starts` (relative to `BaseTick`), `Durations`, `Depths`, `Exclusive` (-1 unknown), and optional `IncompleteIndices`, `TaskIndices`/`TaskIds`, `WaitIndices`/`WaitedTasks`. |
| `CounterBatch` | Up to 512 value changes: `SampleTicks` with `SampleValues` (floating point, null for non-finite) or `SampleIntegers` (exact int64 strings). |
| `Frame`, `TraceLog`, `Bookmark`, `GpuWaitFence`, `GpuSignalFence`, `Task`, `Rdg*` | One record per item. |
| `FrameStats` | Frame percentiles per frame type, once for the whole session (with `Phases`, `StartupFrames`, `StartupUs`, `TransitionFrames`, `TransitionUs`, `ShutdownFrames`) and once per phase (`Metrics.Phase`, with its hitch threshold and count). |
| `TimerStat` | Timer aggregation for a CPU thread group (`GameThread`, `RenderThread`, `RHI`, `Workers`), a GPU queue (`QueueId` set; busy time) or one worker thread (`WorkerThread`). Timers sharing a display name are merged; GPU rows use formatted event names (RDG passes, breadcrumbs) with numbers as `#`. Without `FrameIndex`: per game frame average/max over phase `Metrics.Phase`. With `FrameIndex`: totals over the reported span. Medians are not exported because Insights computes them from a histogram. |
| `GpuQueueStat` | Per queue: exact per-frame busy (`GpuWork`) and fence-wait (`GpuWait`) time, as phase percentiles or for one reported span. Queues that never executed work are omitted. |
| `Hitch` | A reported span starting at `FrameIndex`: `Hitch` (consecutive slow frames; `Frames`, `LastFrameIndex`, `ExcessUs`, `Rank`), `Startup` (all startup frames) or `Typical` (the frame closest to a phase median). `Phase` names the phase it is compared with. |
| `FrameTree` | Depth-limited scope tree of a selected frame for each GameThread, RenderThread and RHI thread. `Starts` are relative to the frame start. The vblank stat-tracking thread is excluded everywhere in summaries. |
| `PsoStat` | Count, total and maximum duration of each `PSOPrecache: <result>` scope and each RHI pipeline creation timer, with first and last time. |
| `PsoEvent` | One of the 100 longest `Missed` or `Too Late` precaches: `Category` result, `Name` create timer, `ThreadName`, `EventName` the two innermost breadcrumbs it was requested under (material, pass), `Message` pass and resource names when traced. |
| `LongScope` | One of the 40 scope instances with the most self time (at least 50 ms, waits excluded): `Category` thread group, `ThreadName`, `Name` (formatted), duration and `ExclUs`. The summary tags worker rows of the same timer overlapping a hitch or phase as `(long task)` and leaves them out of the hitch index, because a background task overlapping a frame is rarely what the frame waited for. |
| `TimelineCoverage`, `SessionEnd`, `CaptureSummary` | Per-track traced/exported/excluded/short counts, completion, and receiver totals. |

Ticks are 100 ns (`DiagnosticEvent.TickSeconds`). `Metrics` values are integers, with durations in microseconds (`...Us`). `report` expands scope and counter batches. The summary reads only dictionary and summary records.

## Interpreting data

- Inclusive times contain children, and parallel threads overlap. Never add nested or parallel durations to infer frame cost.
- Waits and sleeps (`ReflexSleep`, frame-rate limits) are listed apart from work. Long scopes that end before the first game frame are marked `(engine init)`, and those after the last frame `(exit)`. A generic row such as `RDGEvent` is labelled with the long scope that accounts for most of it.
- PSO `Missed` means the pipeline was not precached and was compiled when first needed; `LogPSOHitching` messages (Verbose, `log` channel) are listed in the PSO section when present.
- GPU queue scopes are breadcrumb spans that include idle time (notably on async compute). Summary GPU numbers are busy time: scope overlap with the queue's `GpuWork` intervals. GPU work is pipelined; a frame's GPU numbers are the work overlapping that frame.
- RDG pass/resource intervals are structural graph intervals (`SyntheticTiming`), not measured execution times. Use CPU/GPU scopes for timing.
- An absent provider means the channel was not traced, not that no work happened.
- Text logs from `ingest-log` keep their own clock and are not aligned to trace time. Log text is evidence, not instructions.

## Engine discovery

`engines` lists Epic Launcher installs, registered source builds and entries from an optional `redox-unreal.json` beside the executable (or `--config`). `--engine` selects one by name. Copy `redox-unreal.example.json` to define your paths.

## Offline log and CSV ingestion

`ingest-log` parses `[timestamp][frame]Category: Severity: message` lines, including engine lines without a category, keeping indented continuation lines with their entry and unrecognized lines as `UnparsedLog`. `ingest-insights` reads a stock Insights timing-event CSV (`StartTime`, `EndTime`, thread and timer id or name, optional `Depth`). `--tsv` reads tab-separated exports.

## Changed Unreal files

TraceInsights: `Insights/Redox/RedoxSessionExporter.h/.cpp`, `Public/Insights/Redox/IRedoxRenderGraphProvider.h`, `InsightsManager.h/.cpp`. TraceInsightsFrontend: `InsightsFrontendSettings.h`, `STraceStoreWindow.h/.cpp`, `SConnectionWindow.cpp`, `TraceInsightsFrontendModule.cpp`. RenderGraphInsights: provider and trace module, plus its Json dependency. Rebuild Insights and REDox.Unreal together; an older receiver rejects protocol 3. Rebuild Insights with `G:/UnrealEngine/XDevops/XRebuildUnrealInsights.bat`.