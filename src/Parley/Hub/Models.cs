using System.Text.Json.Serialization;

namespace Parley.Hub;

/// <summary>An agent session taking part in the conversation (e.g. "backend", "frontend").</summary>
public class Session
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("workingDir")]
    public string? WorkingDir { get; set; }

    [JsonPropertyName("projectName")]
    public string? ProjectName { get; set; }

    [JsonPropertyName("lastSeen")]
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    /// <summary>Open push streams for this session; &gt; 0 means messages reach it without polling.</summary>
    [JsonPropertyName("listeners")]
    public int Listeners { get; set; }
}

public class Topic
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("subscribers")]
    public HashSet<string> Subscribers { get; set; } = new();

    /// <summary>
    /// Whether any session subscribed since load. A restored topic nobody holds a cursor on has no
    /// subscribers yet; without this it would be deleted by the first stray unsubscribe.
    /// </summary>
    [JsonIgnore]
    public bool HasHadSubscriber { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("createdBy")]
    public string CreatedBy { get; set; } = "";

    [JsonPropertyName("messageCount")]
    public int MessageCount { get; set; }
}

public class Message
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("topic")]
    public string Topic { get; set; } = "";

    [JsonPropertyName("sender")]
    public string Sender { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

internal class PersistedTopic
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("createdBy")]
    public string CreatedBy { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}

internal class PersistedState
{
    [JsonPropertyName("nextMessageId")]
    public int NextMessageId { get; set; }

    [JsonPropertyName("topics")]
    public List<PersistedTopic> Topics { get; set; } = new();

    [JsonPropertyName("messages")]
    public List<Message> Messages { get; set; } = new();

    [JsonPropertyName("cursors")]
    public Dictionary<string, Dictionary<string, int>> Cursors { get; set; } = new();
}
