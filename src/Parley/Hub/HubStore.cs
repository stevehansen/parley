using System.Text;
using System.Text.Json;

namespace Parley.Hub;

/// <summary>
/// Files behind the hub, in one directory:
///   messages.jsonl — one message per line, appended (and flushed) the moment it is sent, so a
///                    crash or reboot loses nothing that was acknowledged;
///   state.json     — topics, read cursors and the id counter: small, rewritten on change.
/// The log only grows between compactions; <see cref="Compact"/> rewrites it from the messages
/// retention kept. A torn last line (power loss mid-append) is skipped on load.
/// </summary>
internal sealed class HubStore
{
    private static readonly JsonSerializerOptions StateJson = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions LineJson = new() { PropertyNameCaseInsensitive = true };

    private readonly object _fileLock = new();
    private readonly string _statePath;
    private readonly string _logPath;
    private readonly string? _legacyStateFile;

    /// <summary>Lines in the log, including ones retention has since dropped from memory.</summary>
    public int LogLines { get; private set; }

    /// <param name="legacyStateFile">A TerminalHost collab-state.json to import from when no Parley state exists yet.</param>
    public HubStore(string stateFile, string? legacyStateFile = null)
    {
        _legacyStateFile = legacyStateFile;
        _statePath = Path.GetFullPath(stateFile);
        _logPath = Path.Combine(Path.GetDirectoryName(_statePath)!, "messages.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
    }

    public (PersistedState state, List<Message> messages) Load()
    {
        var state = ReadState() ?? ImportTerminalHostState() ?? new PersistedState();
        var messages = new List<Message>();

        if (File.Exists(_logPath))
        {
            foreach (var line in File.ReadLines(_logPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                LogLines++;
                try
                {
                    if (JsonSerializer.Deserialize<Message>(line, LineJson) is { } m) messages.Add(m);
                }
                catch (JsonException)
                {
                    Log.Error($"Skipping unreadable line {LogLines} in {_logPath}");
                }
            }
        }
        else if (state.Messages.Count > 0)
        {
            // Older snapshots carried the messages inline; move them into the log once.
            messages.AddRange(state.Messages);
            Compact(messages);
        }

        state.Messages = [];
        if (messages.Count > 0) state.NextMessageId = Math.Max(state.NextMessageId, messages.Max(m => m.Id));
        return (state, messages);
    }

    public void Append(Message message)
    {
        var line = JsonSerializer.Serialize(message) + "\n";
        lock (_fileLock)
        {
            using var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(Encoding.UTF8.GetBytes(line));
            stream.Flush(flushToDisk: true);
            LogLines++;
        }
    }

    public void SaveState(PersistedState state)
    {
        lock (_fileLock)
            WriteAtomically(_statePath, JsonSerializer.Serialize(state, StateJson));
    }

    /// <summary>Rewrites the log to exactly <paramref name="messages"/>.</summary>
    public void Compact(IReadOnlyCollection<Message> messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages) sb.Append(JsonSerializer.Serialize(m)).Append('\n');
        lock (_fileLock)
        {
            WriteAtomically(_logPath, sb.ToString());
            LogLines = messages.Count;
        }
    }

    private PersistedState? ReadState()
    {
        if (!File.Exists(_statePath)) return null;
        try
        {
            return JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_statePath), StateJson);
        }
        catch (JsonException ex)
        {
            Log.Error($"Could not read {_statePath}; starting without topics and cursors", ex);
            return null;
        }
    }

    /// <summary>
    /// First start on a machine that used TerminalHost's built-in collab server: carry its
    /// conversations over (same shape, messages inline). Only when Parley has no state at all.
    /// </summary>
    private PersistedState? ImportTerminalHostState()
    {
        if (_legacyStateFile is not { } legacy || File.Exists(_logPath) || !File.Exists(legacy)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(legacy), StateJson);
            if (state != null) Log.Info($"Imported {state.Topics.Count} topic(s) from {legacy}");
            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private static void WriteAtomically(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
