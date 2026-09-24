using System.Text.Json;
using Parley.Hub;

namespace Parley.Tests;

public class CollabHubTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parley-tests-" + Guid.NewGuid().ToString("N"));
    private string StateFile => Path.Combine(_dir, "state.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void SendMessage_AutoCreatesTopicAndSubscribes()
    {
        using var hub = new CollabHub();
        hub.SendMessage("alice", "work", "hello");

        var topic = hub.GetTopics().ShouldHaveSingleItem();
        topic.Name.ShouldBe("work");
        topic.Subscribers.ShouldContain("alice");
        topic.MessageCount.ShouldBe(1);
    }

    [Fact]
    public async Task ReadMessages_ReturnsMessagesAfterSinceId()
    {
        using var hub = new CollabHub();
        hub.SendMessage("alice", "work", "msg1");
        hub.SendMessage("alice", "work", "msg2");
        hub.SendMessage("alice", "work", "msg3");

        var (msgs, cursor) = await hub.ReadMessagesAsync("bob", "work", 1);
        msgs.Select(m => m.Content).ShouldBe(["msg2", "msg3"]);
        cursor.ShouldBe(3);
    }

    [Fact]
    public async Task ReadMessages_WithTimeout_WakesOnNewMessage()
    {
        using var hub = new CollabHub();
        hub.Subscribe("bob", "work");

        var read = hub.ReadMessagesAsync("bob", "work", 0, timeoutMs: 10_000);
        read.IsCompleted.ShouldBeFalse();
        hub.SendMessage("alice", "work", "ping");

        var (msgs, _) = await read.WaitAsync(TimeSpan.FromSeconds(5));
        msgs.ShouldHaveSingleItem().Content.ShouldBe("ping");
    }

    [Fact]
    public async Task UnreadCounts_TrackPerTopic_AndExcludeOwnMessages()
    {
        using var hub = new CollabHub();
        hub.Subscribe("alice", "work");
        hub.SendMessage("bob", "work", "msg1");
        hub.SendMessage("bob", "work", "msg2");

        hub.GetUnreadCounts("alice")["work"].ShouldBe(2);
        hub.GetUnreadCounts("bob").ShouldBeEmpty();

        await hub.ReadMessagesAsync("alice", "work", 0);
        hub.GetUnreadCounts("alice").ShouldBeEmpty();
    }

    [Fact]
    public void Unsubscribe_LastSubscriber_DeletesTopic()
    {
        using var hub = new CollabHub();
        hub.SendMessage("alice", "work", "hello");

        hub.Unsubscribe("alice", "work").ok.ShouldBeTrue();
        hub.GetTopics().ShouldBeEmpty();
    }

    [Fact]
    public void GetDeliverable_OnlySubscribedTopics_NotOwnMessages()
    {
        using var hub = new CollabHub();
        hub.Subscribe("bob", "work");
        hub.SendMessage("alice", "work", "for bob");
        hub.SendMessage("bob", "work", "from bob");
        hub.SendMessage("alice", "other", "not for bob");

        hub.GetDeliverable("bob", 0).ShouldHaveSingleItem().Content.ShouldBe("for bob");
        hub.GetDeliverable("bob", 1).ShouldBeEmpty();
    }

    [Fact]
    public void RenameSession_CarriesSubscriptionsAndCursors()
    {
        using var hub = new CollabHub();
        hub.SendMessage("session-1", "work", "hi");
        hub.SendMessage("bob", "work", "hello");

        hub.RenameSession("session-1", "alice");

        var topic = hub.GetTopics().ShouldHaveSingleItem();
        topic.Subscribers.ShouldContain("alice");
        topic.Subscribers.ShouldNotContain("session-1");
        hub.GetUnreadCounts("alice")["work"].ShouldBe(1);
        hub.GetSessions().Select(s => s.Name).ShouldContain("alice");
        hub.GetSessions().Select(s => s.Name).ShouldNotContain("session-1");
    }

    [Fact]
    public void OpenListener_CountsWhileOpen()
    {
        using var hub = new CollabHub();
        var listener = hub.OpenListener("bob");
        hub.GetSessions().Single(s => s.Name == "bob").Listeners.ShouldBe(1);

        listener.Dispose();
        listener.Dispose();
        hub.GetSessions().Single(s => s.Name == "bob").Listeners.ShouldBe(0);
    }

    [Fact]
    public async Task Persistence_RoundTrip_KeepsMessagesIdsAndSubscriptions()
    {
        using (var hub = new CollabHub(StateFile))
        {
            hub.SendMessage("alice", "work", "hello world");
            hub.SendMessage("bob", "work", "hi alice");
            hub.Subscribe("charlie", "announcements", "Company announcements");
            await hub.ReadMessagesAsync("alice", "work", 0);
        }

        using var reloaded = new CollabHub(StateFile);
        var topics = reloaded.GetTopics();
        topics.Select(t => t.Name).ShouldBe(["work", "announcements"], ignoreOrder: true);
        topics.Single(t => t.Name == "work").Subscribers.ShouldBe(["alice", "bob"], ignoreOrder: true);
        topics.Single(t => t.Name == "announcements").Description.ShouldBe("Company announcements");

        // Restored subscriptions keep push delivery working without re-subscribing.
        reloaded.SendMessage("bob", "work", "after restart");
        reloaded.GetDeliverable("alice", 2).ShouldHaveSingleItem().Id.ShouldBe(3);
        reloaded.GetUnreadCounts("alice")["work"].ShouldBe(1);
    }

    [Fact]
    public void Persistence_Unsubscribe_IsNotRestored()
    {
        using (var hub = new CollabHub(StateFile))
        {
            hub.SendMessage("alice", "work", "hi");
            hub.Subscribe("bob", "work");
            hub.Unsubscribe("bob", "work");
        }

        using var reloaded = new CollabHub(StateFile);
        reloaded.GetTopics().Single().Subscribers.ShouldBe(["alice"]);
    }

    [Fact]
    public void Persistence_TopicWithoutSubscribers_SurvivesUntilSomeoneSubscribes()
    {
        WriteState(new
        {
            nextMessageId = 1,
            topics = new[] { new { name = "work", createdBy = "alice", createdAt = DateTime.UtcNow } },
            messages = new[] { new { id = 1, topic = "work", sender = "alice", content = "saved", createdAt = DateTime.UtcNow } },
            cursors = new Dictionary<string, Dictionary<string, int>>(),
        });

        using var hub = new CollabHub(StateFile);
        hub.Unsubscribe("stranger", "work");
        hub.GetTopics().ShouldHaveSingleItem();

        hub.Subscribe("bob", "work");
        hub.Unsubscribe("bob", "work");
        hub.GetTopics().ShouldBeEmpty();
    }

    [Fact]
    public void Persistence_StaleTopicsPruned()
    {
        var old = DateTime.UtcNow.AddDays(-10);
        WriteState(new
        {
            nextMessageId = 2,
            topics = new[]
            {
                new { name = "stale", createdBy = "old", createdAt = old },
                new { name = "fresh", createdBy = "new", createdAt = DateTime.UtcNow },
            },
            messages = new[]
            {
                new { id = 1, topic = "stale", sender = "old", content = "old", createdAt = old },
                new { id = 2, topic = "fresh", sender = "new", content = "new", createdAt = DateTime.UtcNow },
            },
            cursors = new Dictionary<string, Dictionary<string, int>>(),
        });

        using var hub = new CollabHub(StateFile);
        hub.GetTopics().ShouldHaveSingleItem().Name.ShouldBe("fresh");
    }

    [Fact]
    public void Persistence_CorruptFile_StartsEmpty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StateFile, "{ not json");

        using var hub = new CollabHub(StateFile);
        hub.GetTopics().ShouldBeEmpty();
    }

    private void WriteState(object state)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
    }
}
