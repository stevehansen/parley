using System.Text.Json;
using Parley.Hub;
using Parley.Update;

namespace Parley.Tests;

public class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parley-tests-" + Guid.NewGuid().ToString("N"));
    private string StateFile => Path.Combine(_dir, "state.json");
    private string LogFile => Path.Combine(_dir, "messages.jsonl");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Message_IsOnDisk_BeforeAnyFlush()
    {
        using var hub = new CollabHub(StateFile);
        hub.SendMessage("alice", "work", "durable");

        // No Dispose, no save tick: a crash right now must not lose the message.
        var line = File.ReadAllLines(LogFile).ShouldHaveSingleItem();
        JsonDocument.Parse(line).RootElement.GetProperty("content").GetString().ShouldBe("durable");
    }

    [Fact]
    public void TornLastLine_IsSkipped()
    {
        using (var hub = new CollabHub(StateFile))
        {
            hub.SendMessage("alice", "work", "one");
            hub.SendMessage("alice", "work", "two");
        }
        File.AppendAllText(LogFile, "{\"id\":3,\"topic\":\"work\",\"sen");

        using var reloaded = new CollabHub(StateFile);
        reloaded.GetRecentMessages().Select(m => m.Content).ShouldBe(["one", "two"]);
        reloaded.SendMessage("alice", "work", "three").Id.ShouldBe(3);
    }

    [Fact]
    public void DeletedTopics_AreCompactedOutOfTheLog()
    {
        using (var hub = new CollabHub(StateFile))
        {
            for (var i = 0; i < 1100; i++) hub.SendMessage("alice", "noise", $"m{i}");
            hub.SendMessage("alice", "keep", "kept");
            hub.DeleteTopic("noise");
            hub.SaveIfDirty();
        }

        File.ReadAllLines(LogFile).ShouldHaveSingleItem().ShouldContain("kept");
        using var reloaded = new CollabHub(StateFile);
        reloaded.GetTopics().ShouldHaveSingleItem().Name.ShouldBe("keep");
        reloaded.SendMessage("alice", "keep", "next").Id.ShouldBe(1102);
    }

    [Fact]
    public void DeleteTopic_RemovesMessagesAndSubscriptions()
    {
        using var hub = new CollabHub();
        hub.SendMessage("alice", "work", "hi");
        hub.Subscribe("bob", "work");

        hub.DeleteTopic("work").ShouldBeTrue();
        hub.GetTopics().ShouldBeEmpty();
        hub.GetRecentMessages().ShouldBeEmpty();
        hub.GetUnreadCounts("bob").ShouldBeEmpty();
        hub.DeleteTopic("work").ShouldBeFalse();
    }

    [Fact]
    public void HumanSend_DoesNotSubscribeTheSender()
    {
        using var hub = new CollabHub();
        hub.Subscribe("agent", "work");
        hub.SendMessage("steve", "work", "please rebase", subscribeSender: false);

        hub.GetTopics().Single().Subscribers.ShouldBe(["agent"]);
        hub.GetDeliverable("agent", 0).ShouldHaveSingleItem().Sender.ShouldBe("steve");
    }

    [Fact]
    public void FirstStart_ImportsTerminalHostConversations()
    {
        Directory.CreateDirectory(_dir);
        var legacy = Path.Combine(_dir, "collab-state.json");
        File.WriteAllText(legacy, JsonSerializer.Serialize(new
        {
            nextMessageId = 7,
            topics = new[] { new { name = "api", createdBy = "backend", createdAt = DateTime.UtcNow } },
            messages = new[] { new { id = 7, topic = "api", sender = "backend", content = "from TerminalHost", createdAt = DateTime.UtcNow } },
            cursors = new Dictionary<string, Dictionary<string, int>> { ["frontend"] = new() { ["api"] = 0 } },
        }));

        using (var hub = new CollabHub(StateFile, legacy))
        {
            hub.GetRecentMessages().ShouldHaveSingleItem().Content.ShouldBe("from TerminalHost");
            hub.GetTopics().Single().Subscribers.ShouldBe(["frontend"]);
            hub.SendMessage("x", "api", "next").Id.ShouldBe(8);
        }

        // Only once: afterwards Parley's own files win.
        using var again = new CollabHub(StateFile, legacy);
        again.GetRecentMessages().Count.ShouldBe(2);
    }

    /// <summary>A NuGet registration index with one inline page.</summary>
    private static string Registration(params string[] versions) =>
        JsonSerializer.Serialize(new { items = new[] { new { items = versions.Select(v => new
        {
            catalogEntry = new { version = v.TrimEnd('!'), listed = !v.EndsWith('!') }, // "x!" = unlisted
        }) } } });

    [Theory]
    [InlineData("0.1.0,0.2.0,0.10.0-beta", "0.2.0")]
    [InlineData("0.9.0,0.10.0", "0.10.0")]
    [InlineData("0.1.0,0.2.0!", "0.1.0")]
    [InlineData("", null)]
    public void UpdateChecker_PicksNewestListedStable(string versions, string? expected)
    {
        var json = Registration(versions.Split(',', StringSplitOptions.RemoveEmptyEntries));
        UpdateChecker.SelectLatestStable(json)?.ToString(3).ShouldBe(expected);
        if (expected == null) UpdateChecker.SelectLatestStable(json).ShouldBeNull();
    }

    [Fact]
    public void UpdateChecker_ReadsPagedOutIndexes_AndSurvivesGarbage()
    {
        UpdateChecker.SelectLatestStable("""{"items":[{"lower":"0.1.0","upper":"0.3.0"}]}""")?.ToString(3).ShouldBe("0.3.0");
        UpdateChecker.SelectLatestStable("not json").ShouldBeNull();
        UpdateChecker.SelectLatestStable("""{"items":[{"items":[{}]}]}""").ShouldBeNull();
    }

    [Fact]
    public void UpdateChecker_ListsReleasesOldestFirst()
    {
        UpdateChecker.ListedStable(Registration("0.2.0", "0.1.0", "0.3.0-rc", "0.1.1!", "0.10.0"))
            .Select(v => v.ToString(3)).ShouldBe(["0.1.0", "0.2.0", "0.10.0"]);
    }

    [Fact]
    public void Rollback_StepsBackThroughUpdates_NotRollbacks()
    {
        static UpdateHistory.Entry E(string from, string to, bool rollback = false) => new(DateTime.UtcNow, from, to, rollback);
        var history = new[]
        {
            E("0.1.0", "0.1.2"),        // skipped 0.1.1
            E("0.1.2", "0.3.0"),
            E("0.3.0", "0.1.2", true),  // rolled back
        };

        UpdateHistory.PredecessorOf(history, new Version(0, 3, 0)).ShouldBe(new Version(0, 1, 2));
        // After the rollback, the next one continues backwards instead of undoing it.
        UpdateHistory.PredecessorOf(history, new Version(0, 1, 2)).ShouldBe(new Version(0, 1, 0));
        UpdateHistory.PredecessorOf(history, new Version(0, 1, 0)).ShouldBeNull();
    }

    [Fact]
    public async Task UpdateChecker_SameVersion_IsNotAnUpdate()
    {
        var current = UpdateChecker.Current.ToString(3);
        var checker = new UpdateChecker(_ => Task.FromResult<string?>(Registration(current)));
        await checker.CheckAsync();
        checker.UpdateAvailable.ShouldBeFalse();

        var newer = new UpdateChecker(_ => Task.FromResult<string?>(Registration("99.0.0")));
        await newer.CheckAsync();
        newer.UpdateAvailable.ShouldBeTrue();
    }
}
