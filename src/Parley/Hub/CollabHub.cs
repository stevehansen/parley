namespace Parley.Hub;

/// <summary>
/// The conversation state: sessions, topics, messages and per-session read cursors. Thread-safe
/// via one lock. Topics auto-create on first use and auto-delete when their last subscriber leaves
/// (only after someone subscribed since load, see <see cref="Topic.HasHadSubscriber"/>).
/// With a state file, messages are durable as soon as they are sent (see <see cref="HubStore"/>);
/// topics and cursors are flushed every few seconds and on dispose. Sessions are not persisted
/// (they re-register on their next call); subscriptions are, through the read cursors.
/// </summary>
public sealed class CollabHub : IDisposable
{
    private const int MaxMessagesPerTopic = 1000;
    private const int MaxMessagesTotal = 10000;
    // Compact the log once it carries this many lines that retention or deletes have dropped.
    private const int CompactionSlack = 1000;
    private static readonly TimeSpan TopicMaxIdle = TimeSpan.FromDays(7);

    private readonly object _lock = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Topic> _topics = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Message> _messages = new();
    // session → topic → last read message id
    private readonly Dictionary<string, Dictionary<string, int>> _cursors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<TaskCompletionSource>> _topicWaiters = new(StringComparer.OrdinalIgnoreCase);
    private int _nextMessageId;

    private readonly HubStore? _store;
    private readonly Timer? _saveTimer;
    private bool _dirty;
    private bool _disposed;

    /// <summary>Raised (outside the lock) after any change a UI would want to redraw for.</summary>
    public event Action? StateChanged;

    /// <summary>Raised (outside the lock) for every sent message; drives push delivery.</summary>
    public event Action<Message>? MessageSent;

    /// <param name="stateFile">State file to persist to (the message log sits next to it), or null for in-memory only.</param>
    /// <param name="legacyStateFile">TerminalHost collab-state.json to import on first start.</param>
    public CollabHub(string? stateFile = null, string? legacyStateFile = null)
    {
        if (stateFile == null) return;

        _store = new HubStore(stateFile, legacyStateFile);
        LoadState();
        _saveTimer = new Timer(_ => SaveIfDirty(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    #region Sessions

    public void EnsureSession(string name, string? workingDir = null, string? projectName = null, string? device = null)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(name, out var s))
                _sessions[name] = s = new Session { Name = name };
            s.LastSeen = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(device)) s.Device = device;
            if (!string.IsNullOrEmpty(workingDir))
            {
                s.WorkingDir = workingDir;
                s.ProjectName ??= FolderName(workingDir);
            }
            if (!string.IsNullOrEmpty(projectName)) s.ProjectName = projectName;
        }
    }

    /// <summary>
    /// Renames a session, carrying over its subscriptions and read cursors so an agent that names
    /// itself after it started talking doesn't lose its place.
    /// </summary>
    public void RenameSession(string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return;
        lock (_lock)
        {
            if (_sessions.Remove(oldName, out var s))
            {
                s.Name = newName;
                _sessions[newName] = s;
            }
            else
            {
                _sessions[newName] = new Session { Name = newName };
            }

            foreach (var t in _topics.Values)
                if (t.Subscribers.Remove(oldName)) t.Subscribers.Add(newName);

            if (_cursors.Remove(oldName, out var cursors))
                _cursors[newName] = cursors;
            _dirty = true;
        }
        RaiseChanged();
    }

    public List<Session> GetSessions()
    {
        lock (_lock)
            return _sessions.Values.Select(s => new Session
            {
                Name = s.Name, WorkingDir = s.WorkingDir, ProjectName = s.ProjectName,
                LastSeen = s.LastSeen, Listeners = s.Listeners, Device = s.Device,
            }).ToList();
    }

    /// <summary>Tracks an open push stream; dispose the result when the stream closes.</summary>
    public IDisposable OpenListener(string session, string? device = null)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(session, out var s))
                _sessions[session] = s = new Session { Name = session };
            s.Listeners++;
            s.LastSeen = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(device)) s.Device = device;
        }
        RaiseChanged();
        return new Listener(this, session);
    }

    private sealed class Listener(CollabHub hub, string session) : IDisposable
    {
        private int _closed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 1) return;
            lock (hub._lock)
            {
                if (hub._sessions.TryGetValue(session, out var s) && s.Listeners > 0)
                {
                    s.Listeners--;
                    s.LastSeen = DateTime.UtcNow;
                }
            }
            hub.RaiseChanged();
        }
    }

    #endregion

    #region Topics

    public void Subscribe(string session, string topic, string? description = null)
    {
        lock (_lock)
        {
            EnsureTopicAndSubscribe(session, topic, description);
            _dirty = true;
        }
        RaiseChanged();
    }

    public (bool ok, string? error) Unsubscribe(string session, string topic)
    {
        lock (_lock)
        {
            if (!_topics.TryGetValue(topic, out var t))
                return (false, $"Topic '{topic}' does not exist.");

            t.Subscribers.Remove(session);
            // A cursor doubles as the persisted subscription, so it goes too.
            if (_cursors.TryGetValue(session, out var sc)) sc.Remove(topic);
            if (t.Subscribers.Count == 0 && t.HasHadSubscriber)
            {
                _topics.Remove(topic);
                _messages.RemoveAll(m => m.Topic.Equals(topic, StringComparison.OrdinalIgnoreCase));
                _topicWaiters.Remove(topic);
                foreach (var c in _cursors.Values) c.Remove(topic);
            }
            _dirty = true;
        }
        RaiseChanged();
        return (true, null);
    }

    /// <summary>Deletes a topic with its messages, whoever is subscribed. For humans cleaning up.</summary>
    public bool DeleteTopic(string topic)
    {
        lock (_lock)
        {
            if (!_topics.Remove(topic)) return false;
            _messages.RemoveAll(m => m.Topic.Equals(topic, StringComparison.OrdinalIgnoreCase));
            if (_topicWaiters.Remove(topic, out var waiters))
                foreach (var w in waiters) w.TrySetResult();
            foreach (var c in _cursors.Values) c.Remove(topic);
            _dirty = true;
        }
        RaiseChanged();
        return true;
    }

    public List<Topic> GetTopics()
    {
        lock (_lock)
            return _topics.Values.Select(t => new Topic
            {
                Name = t.Name,
                Description = t.Description,
                Subscribers = new HashSet<string>(t.Subscribers),
                CreatedAt = t.CreatedAt,
                CreatedBy = t.CreatedBy,
                MessageCount = _messages.Count(m => m.Topic.Equals(t.Name, StringComparison.OrdinalIgnoreCase)),
            }).ToList();
    }

    #endregion

    #region Messages

    /// <param name="subscribeSender">
    /// Agents join the topics they post to. A human posting from the web UI doesn't: a subscriber
    /// that never leaves would keep the topic from ever being cleaned up.
    /// </param>
    public Message SendMessage(string session, string topic, string content, bool subscribeSender = true)
    {
        Message msg;
        lock (_lock)
        {
            if (subscribeSender)
                EnsureTopicAndSubscribe(session, topic);
            else if (!_topics.ContainsKey(topic))
                _topics[topic] = new Topic { Name = topic, CreatedBy = session, HasHadSubscriber = true };
            // "@name" is that session's direct line: whoever writes there, it is listening.
            if (DirectRecipient(topic) is { } recipient && !recipient.Equals(session, StringComparison.OrdinalIgnoreCase))
                EnsureTopicAndSubscribe(recipient, topic, $"Direct messages to {recipient}");

            msg = new Message { Id = ++_nextMessageId, Topic = topic, Sender = session, Content = content };
            _messages.Add(msg);
            // Under the lock, so the log stays in id order.
            _store?.Append(msg);

            // The sender has obviously seen its own message.
            if (subscribeSender) _cursors[session][topic] = msg.Id;

            if (_topicWaiters.Remove(topic, out var waiters))
                foreach (var w in waiters) w.TrySetResult();
            _dirty = true;
        }
        MessageSent?.Invoke(msg);
        RaiseChanged();
        return msg;
    }

    /// <summary>
    /// Returns messages on <paramref name="topic"/> after <paramref name="sinceId"/> and moves the
    /// session's cursor to the newest one returned. With a timeout, waits for the first new message
    /// instead of returning an empty list straight away.
    /// </summary>
    public async Task<(List<Message> messages, int cursor)> ReadMessagesAsync(
        string session, string topic, int sinceId, int timeoutMs = 0, CancellationToken ct = default)
    {
        TaskCompletionSource tcs;
        lock (_lock)
        {
            EnsureTopicAndSubscribe(session, topic);
            var msgs = MessagesAfter(topic, sinceId);
            if (msgs.Count > 0 || timeoutMs <= 0)
                return Advance(session, topic, sinceId, msgs);

            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_topicWaiters.TryGetValue(topic, out var waiters))
                _topicWaiters[topic] = waiters = new();
            waiters.Add(tcs);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try
        {
            await tcs.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
                if (_topicWaiters.TryGetValue(topic, out var waiters)) waiters.Remove(tcs);
        }

        lock (_lock)
        {
            // The topic may have been deleted while we waited.
            EnsureTopicAndSubscribe(session, topic);
            return Advance(session, topic, sinceId, MessagesAfter(topic, sinceId));
        }
    }

    /// <summary>
    /// Messages after <paramref name="sinceId"/> that should be pushed to <paramref name="session"/>:
    /// on topics it subscribes to, sent by someone else. Does not move any cursor.
    /// </summary>
    public List<Message> GetDeliverable(string session, int sinceId)
    {
        lock (_lock)
            return _messages.Where(m => m.Id > sinceId && IsDeliverable(session, m)).ToList();
    }

    public bool IsDeliverable(string session, Message m)
    {
        lock (_lock)
            return !string.Equals(m.Sender, session, StringComparison.OrdinalIgnoreCase)
                && _topics.TryGetValue(m.Topic, out var t)
                && t.Subscribers.Contains(session);
    }

    public int LastMessageId
    {
        get { lock (_lock) return _nextMessageId; }
    }

    public List<Message> GetRecentMessages(int count = 20, string? topic = null)
    {
        lock (_lock)
            return _messages
                .Where(m => topic == null || m.Topic.Equals(topic, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.Id)
                .Take(count)
                .OrderBy(m => m.Id)
                .ToList();
    }

    public Dictionary<string, int> GetUnreadCounts(string session)
    {
        lock (_lock)
        {
            var result = new Dictionary<string, int>();
            foreach (var (name, topic) in _topics)
            {
                if (!topic.Subscribers.Contains(session)) continue;
                var cursor = _cursors.TryGetValue(session, out var tc) && tc.TryGetValue(name, out var c) ? c : 0;
                var unread = _messages.Count(m => m.Topic.Equals(name, StringComparison.OrdinalIgnoreCase) && m.Id > cursor);
                if (unread > 0) result[name] = unread;
            }
            return result;
        }
    }

    #endregion

    // Must be called under _lock.
    private List<Message> MessagesAfter(string topic, int sinceId) =>
        _messages.Where(m => m.Topic.Equals(topic, StringComparison.OrdinalIgnoreCase) && m.Id > sinceId).ToList();

    // Must be called under _lock.
    private (List<Message>, int) Advance(string session, string topic, int sinceId, List<Message> msgs)
    {
        var maxId = msgs.Count > 0 ? msgs.Max(m => m.Id) : sinceId;
        _cursors[session][topic] = maxId;
        _dirty = true;
        return (msgs, maxId);
    }

    // Must be called under _lock.
    private void EnsureTopicAndSubscribe(string session, string topic, string? description = null)
    {
        if (!_topics.TryGetValue(topic, out var t))
            _topics[topic] = t = new Topic { Name = topic, CreatedBy = session };
        if (description != null) t.Description = description;
        t.Subscribers.Add(session);
        t.HasHadSubscriber = true;

        if (!_cursors.TryGetValue(session, out var cursors))
            _cursors[session] = cursors = new(StringComparer.OrdinalIgnoreCase);
        cursors.TryAdd(topic, 0);
    }

    /// <summary>The session a direct topic (<c>@name</c>) belongs to, spelled as the hub knows it.</summary>
    private string? DirectRecipient(string topic)
    {
        if (topic.Length < 2 || topic[0] != '@') return null;
        var name = topic[1..];
        return _sessions.TryGetValue(name, out var s) ? s.Name : name;
    }

    private void RaiseChanged() => StateChanged?.Invoke();

    private static string FolderName(string path)
    {
        var p = path.Replace('\\', '/').TrimEnd('/');
        var i = p.LastIndexOf('/');
        return (i >= 0 ? p[(i + 1)..] : p).TrimEnd(':');
    }

    #region Persistence

    private void LoadState()
    {
        var (state, messages) = _store!.Load();
        lock (_lock)
        {
            _nextMessageId = state.NextMessageId;
            var cutoff = DateTime.UtcNow - TopicMaxIdle;
            var lastActivity = messages.GroupBy(m => m.Topic, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Max(m => m.CreatedAt), StringComparer.OrdinalIgnoreCase);

            foreach (var pt in state.Topics)
            {
                var active = lastActivity.TryGetValue(pt.Name, out var last) && last > pt.CreatedAt ? last : pt.CreatedAt;
                if (active < cutoff) continue;
                _topics[pt.Name] = new Topic
                {
                    Name = pt.Name, Description = pt.Description, CreatedBy = pt.CreatedBy, CreatedAt = pt.CreatedAt,
                };
            }

            _messages.AddRange(messages.Where(m => _topics.ContainsKey(m.Topic)).OrderBy(m => m.Id));
            EnforceRetention();

            // Every subscription has a cursor, so cursors restore subscriptions: a session that
            // reconnects after a hub restart keeps receiving pushes without re-subscribing.
            foreach (var (session, topicCursors) in state.Cursors)
            {
                var kept = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var (topic, cursor) in topicCursors)
                {
                    if (!_topics.TryGetValue(topic, out var t)) continue;
                    kept[topic] = cursor;
                    t.Subscribers.Add(session);
                    t.HasHadSubscriber = true;
                }
                if (kept.Count > 0) _cursors[session] = kept;
            }
            _dirty = true;
        }
        SaveIfDirty();
    }

    // Must be called under _lock.
    private void EnforceRetention()
    {
        foreach (var group in _messages.GroupBy(m => m.Topic, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > MaxMessagesPerTopic).ToList())
        {
            var keep = group.OrderByDescending(m => m.Id).Take(MaxMessagesPerTopic).Select(m => m.Id).ToHashSet();
            _messages.RemoveAll(m => m.Topic.Equals(group.Key, StringComparison.OrdinalIgnoreCase) && !keep.Contains(m.Id));
        }

        if (_messages.Count > MaxMessagesTotal)
            _messages.RemoveRange(0, _messages.Count - MaxMessagesTotal); // kept in id order
    }

    internal void SaveIfDirty()
    {
        if (_store == null) return;
        PersistedState snapshot;
        bool compact;
        lock (_lock)
        {
            if (!_dirty) return;
            _dirty = false;
            EnforceRetention();
            snapshot = new PersistedState
            {
                NextMessageId = _nextMessageId,
                Topics = _topics.Values.Select(t => new PersistedTopic
                {
                    Name = t.Name, Description = t.Description, CreatedBy = t.CreatedBy, CreatedAt = t.CreatedAt,
                }).ToList(),
                Cursors = _cursors.ToDictionary(kv => kv.Key, kv => new Dictionary<string, int>(kv.Value)),
            };
            compact = _store.LogLines > _messages.Count + CompactionSlack;
        }

        try
        {
            // State I/O outside the lock so messaging never waits on it.
            _store.SaveState(snapshot);
            // The rewrite holds the lock: a message appended mid-rewrite would otherwise be lost.
            // Rare enough (every CompactionSlack dropped lines) for that to be cheap.
            if (compact)
                lock (_lock) _store.Compact(_messages);
        }
        catch (IOException ex)
        {
            Log.Error("Could not save hub state; will retry", ex);
            lock (_lock) _dirty = true;
        }
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveTimer?.Dispose();
        SaveIfDirty();
    }
}
