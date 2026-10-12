// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using REDox.Json;

namespace REDox.Unreal;

public sealed class EngineConfiguration
{
    public string? DefaultEngine { get; set; }
    public Dictionary<string, string> Engines { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class EngineInstallation
{
    public string Name { get; set; } = "";
    public string Root { get; set; } = "";
    public string Source { get; set; } = "";
    public bool Available => File.Exists(Path.Combine(Root, "Engine", "Build", "Build.version"));
    public string InsightsExecutable => Path.Combine(Root, "Engine", "Binaries", "Win64", "UnrealInsights.exe");
    public bool InsightsAvailable => File.Exists(InsightsExecutable);
}

public sealed class EngineCatalog
{
    public string? ConfigFile { get; set; }
    public string? DefaultEngine { get; set; }
    public List<EngineInstallation> Engines { get; set; } = [];
}

public static class EngineDiscovery
{
    public static EngineCatalog Discover(string? configFile = null)
    {
        var explicitConfig = configFile != null;
        configFile = Path.GetFullPath(configFile ?? Path.Combine(AppContext.BaseDirectory, "redox-unreal.json"));
        var catalog = new EngineCatalog();
        if (File.Exists(configFile))
        {
            using var stream = File.OpenRead(configFile);
            var configuration = JsonSerializer.Deserialize<EngineConfiguration>(stream)
                                ?? throw new InvalidDataException($"{configFile}: expected an engine configuration.");
            catalog.ConfigFile = configFile;
            catalog.DefaultEngine = configuration.DefaultEngine;
            if (configuration.Engines == null)
            {
                throw new InvalidDataException($"{configFile}: Engines must be an object of named engine paths.");
            }

            foreach (var engine in configuration.Engines)
            {
                if (string.IsNullOrWhiteSpace(engine.Key) || string.IsNullOrWhiteSpace(engine.Value))
                {
                    throw new InvalidDataException($"{configFile}: engine names and paths must not be empty.");
                }

                Add(catalog, engine.Key, Path.GetFullPath(engine.Value, Path.GetDirectoryName(configFile)!), "Config");
            }

        }
        else if (explicitConfig)
        {
            throw new FileNotFoundException("Engine configuration not found.", configFile);
        }

        if (OperatingSystem.IsWindows())
        {
            ReadLauncher(catalog);
            ReadRegistry(catalog);
        }

        if (catalog.DefaultEngine != null)
        {
            Select(catalog, catalog.DefaultEngine);
        }

        catalog.Engines.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
        return catalog;
    }

    public static EngineInstallation Select(EngineCatalog catalog, string name)
    {
        var matches = catalog.Engines.FindAll(engine => string.Equals(engine.Name, name,
            StringComparison.OrdinalIgnoreCase));
        if (matches.Count != 1)
        {
            throw new ArgumentException($"Engine '{name}' is missing or ambiguous. Configure a unique custom name.");
        }

        return matches[0];
    }

    private static void Add(EngineCatalog catalog, string name, string root, string source)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (catalog.Engines.Exists(engine => string.Equals(engine.Root, root, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        // Config entries remain visible when a drive is disconnected or Insights has not been built yet.
        if (source == "Config" || File.Exists(Path.Combine(root, "Engine", "Build", "Build.version")))
        {
            catalog.Engines.Add(new EngineInstallation { Name = name, Root = root, Source = source });
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ReadLauncher(EngineCatalog catalog)
    {
        var manifest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
        if (!File.Exists(manifest))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            if (document.RootElement.TryGetProperty("InstallationList", out var list))
            {
                foreach (var value in list.AsArray())
                {
                    var item = value.AsElement();
                    if (item.TryGetProperty("AppName", out var appName)
                        && item.TryGetProperty("InstallLocation", out var location)
                        && appName.GetString() is { } name && name.StartsWith("UE_", StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(location.GetString()))
                    {
                        Add(catalog, name[3..], location.GetString()!, "Launcher");
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                         or DocumentParseException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Could not read Launcher installations: {exception.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ReadRegistry(EngineCatalog catalog)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var launcher = machine.OpenSubKey(@"SOFTWARE\EpicGames\Unreal Engine");
                if (launcher != null)
                {
                    foreach (var name in launcher.GetSubKeyNames())
                    {
                        using var engine = launcher.OpenSubKey(name);
                        if (engine?.GetValue("InstalledDirectory") is string root && !string.IsNullOrWhiteSpace(root))
                        {
                            Add(catalog, name, root, "LauncherRegistry");
                        }
                    }
                }

                using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                using var builds = user.OpenSubKey(@"SOFTWARE\Epic Games\Unreal Engine\Builds");
                if (builds != null)
                {
                    foreach (var name in builds.GetValueNames())
                    {
                        if (builds.GetValue(name) is string root && !string.IsNullOrWhiteSpace(root))
                        {
                            Add(catalog, name, root, "SourceRegistry");
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                             or SecurityException or ArgumentException)
            {
                Console.Error.WriteLine($"Could not read {view} engine registrations: {exception.Message}");
            }
        }
    }
}