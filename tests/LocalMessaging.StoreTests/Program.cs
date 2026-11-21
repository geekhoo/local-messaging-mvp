using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalMessaging.Application;
using LocalMessaging.Contracts;
using LocalMessaging.Infrastructure;
using Microsoft.Data.Sqlite;

if (args.Length > 0 && args[0] == "--workload") return await Workload.RunAsync(args[1..]);

var results = new List<object>();
var failed = 0;
var cases = new (string Id, string Name, Func<Task> Run)[]
{
    ("STORE01", "Real SQLite migration, WAL/FK/FULL, exclusive lock, and stable reopen", Tests.Storage),
    ("STORE02", "Normalized user and immutable receipt retry", Tests.UserRetry),
    ("STORE03", "Strict shapes, duplicate keys, default normalization and global ID conflict", Tests.Contracts),
    ("STORE04", "Channel creates exactly one caller-owned initial session", Tests.Channel),
    ("STORE05", "Independent session ownership and new-channel session cardinality", Tests.Session),
    ("STORE06", "Compound text objects and ordered committed events", Tests.Compound),
    ("STORE07", "Missing/mismatched references never create implicitly", Tests.References),
    ("STORE08", "Invalid targets or parents roll back whole compound graph", Tests.Rollback),
    ("STORE09", "Exact mention grammar, canonical target union and unknown handles", Tests.Mentions),
    ("STORE10", "Three thread levels and cross-session database constraints", Tests.Threads),
    ("STORE11", "Targeting is public and reply targets are not inherited", Tests.PublicTargets),
    ("STORE12", "Atomic image bytes/descriptors/retry and conflicting byte identity", Tests.Images),
    ("STORE13", "Saved subscription lifecycle, receipt state and restart", Tests.Subscriptions),
    ("STORE14", "Coupled automatic-session subscription and immutable receipt after close", Tests.Coupled),
    ("STORE15", "Sparse event scans advance within bounds without skipped match", Tests.Sparse),
    ("STORE16", "OR filters deduplicate and consumers retain independent cursors", Tests.Overlap),
    ("STORE17", "Epoch, malformed, ahead and before-start cursor errors", Tests.Cursors),
    ("STORE18", "Snapshot catalog paging ignores later commits and validates tokens", Tests.Snapshots),
    ("STORE19", "Thread pages provide ancestors and counts respect snapshot H", Tests.ThreadPages),
    ("STORE20", "Concurrent identical requests commit one graph and event range", Tests.Concurrent),
    ("STORE21", "Before-commit failure rolls back and retry creates once", Tests.BeforeCommit),
    ("STORE22", "After-commit notification failure recovers by receipt and replay", Tests.AfterCommit),
    ("STORE23", "Backup/restore preserves full store and rotates epoch before publication", Tests.Restore),
    ("STORE24", "Interrupted restore before publication preserves current history", Tests.RestoreBefore),
    ("STORE25", "Interrupted restore after publication always exposes new epoch", Tests.RestoreAfter),
    ("STORE26", "Invalid backup leaves current store usable and refuses live maintenance", Tests.InvalidRestore),
    ("STORE27", "Configured content/filter/page/resource bounds are enforced", Tests.Limits),
    ("STORE28", "Byte-budget continuation never advances past excluded matching event", Tests.ByteBudget),
    ("STORE29", "Operational subscription pagination has no duplicate or omitted rows", Tests.SubscriptionPages),
    ("STORE30", "SQLite root/depth/content/unique-image constraints remain authoritative", Tests.RelationalConstraints),
    ("STORE31", "Actual SQLite writer contention times out without partial publication", Tests.WriterContention),
    ("STORE32", "Unsupported migration fails startup and preserves existing history", Tests.UnknownMigration),
    ("STORE33", "Corrupt database fails startup without replacement or content changes", Tests.CorruptStore),
    ("STORE34", "Public profile acknowledgement retries, snapshots, restart and restore", Tests.Acknowledgements),
    ("STORE35", "Append-only participation scopes, events and roster snapshots", Tests.Participants)
};
foreach (var test in cases)
{
    var watch = Stopwatch.StartNew();
    try
    {
        await test.Run();
        results.Add(new { id = test.Id, name = test.Name, status = "passed", elapsedMs = watch.ElapsedMilliseconds });
        Console.WriteLine($"PASS {test.Id}: {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        results.Add(new { id = test.Id, name = test.Name, status = "failed", elapsedMs = watch.ElapsedMilliseconds, error = ex.ToString() });
        Console.Error.WriteLine($"FAIL {test.Id}: {ex}");
    }
}
var report = new
{
    suite = "LocalMessaging.StoreTests", platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    executedAtUtc = DateTime.UtcNow.ToString("O"), result = failed == 0 ? "passed" : "failed",
    passed = cases.Length - failed, failed,
    scope = "Actual real-file SQLite/application execution. Supporting implementation evidence; not Windows/H3/WIC/WinUI or milestone acceptance.",
    tests = results
};
var output = args.Length == 2 && args[0] == "--output" ? Path.GetFullPath(args[1]) : null;
if (args.Length != 0 && output is null) { Console.Error.WriteLine("Usage: LocalMessaging.StoreTests [--output <report.json>]"); return 2; }
if (output is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}
Console.WriteLine($"RESULT: {cases.Length - failed}/{cases.Length} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

static class Tests
{
    public static async Task Storage()
    {
        await using var f = await Fixture.Create(false);
        var epoch = f.Store.StoreEpoch;
        Eq("wal", await f.Scalar("PRAGMA journal_mode;"));
        Eq("1", await f.Scalar("PRAGMA foreign_keys;"));
        Eq("2", await f.Scalar("PRAGMA synchronous;"));
        Eq("2", await f.Scalar("SELECT count(*) FROM __EFMigrationsHistory;"));
        await using (var other = new MessagingStore(new StoreOptions(f.Directory)))
            await Error(503, "storage_unavailable", () => other.InitializeAsync());
        await f.Reopen();
        Eq(epoch, f.Store.StoreEpoch);
        Eq("1", await f.Scalar("SELECT count(*) FROM AppMetadata;"));
    }
    public static async Task UserRetry()
    {
        await using var f = await Fixture.Create(false);
        var requestId = Guid.NewGuid();
        var request = Fixture.Envelope(null, new JsonObject { ["type"] = "user", ["handle"] = "  ALICE  ", ["displayName"] = " Alice " }, requestId);
        var one = await f.Send(request);
        var changedOrder = Fixture.Envelope(null, new JsonObject { ["displayName"] = "Alice", ["handle"] = "alice", ["type"] = "user" }, requestId);
        var two = await f.Send(changedOrder);
        Eq("alice", one["dataObjects"]!["user"]!["handle"]!.GetValue<string>());
        Eq(one["dataObjects"]!["user"]!["id"]!.GetValue<string>(), two["dataObjects"]!["user"]!["id"]!.GetValue<string>());
        True(two["replayed"]!.GetValue<bool>(), "Retry flag");
        Eq("1", await f.Scalar("SELECT count(*) FROM Users;"));
        Eq("1", await f.Scalar("SELECT count(*) FROM EventLog;"));
        var receipt = await f.Store.GetReceiptAsync(requestId);
        Eq("committed", receipt["state"]!.GetValue<string>());
        True(receipt["replayed"] is null, "GET receipt is not a POST retry.");
        await Error(409, "handle_already_exists", () => f.Send(Fixture.Envelope(null, new JsonObject { ["type"] = "user", ["handle"] = "ALICE", ["displayName"] = "Someone" })));
    }
    public static async Task Contracts()
    {
        await using var f = await Fixture.Create();
        var request = f.Text("Hello");
        request["unknown"] = true;
        await Error(400, "invalid_contract", () => f.Send(request));
        using var duplicate = JsonDocument.Parse($$$"""{"contractVersion":1,"contractVersion":1,"requestId":"{{{Guid.NewGuid()}}}","dataObject":{"type":"user","handle":"delta","displayName":"Delta"}}""");
        await Error(400, "invalid_contract", () => f.Store.ExecuteAsync(duplicate.RootElement));
        var original = f.Text("same\r\nbody");
        var first = await f.Send(original);
        original["dataObject"]!["content"]!["text"] = "same\nbody";
        original["dataObject"]!["targetUserIds"] = new JsonArray();
        var retry = await f.Send(original);
        True(retry["replayed"]!.GetValue<bool>(), "Omitted/empty targets and CRLF normalize.");
        original["callerUserId"] = f.B;
        await Error(409, "idempotency_conflict", () => f.Send(original));
        var sub = f.Subscription(new JsonObject { ["allMessages"] = true }, "now", requestId: Guid.Parse(first["requestId"]!.GetValue<string>()));
        await Error(409, "idempotency_conflict", () => f.Store.CreateSubscriptionAsync(Fixture.Element(sub)));
    }
    public static async Task Channel()
    {
        await using var f = await Fixture.Create();
        var response = await f.Send(Fixture.Envelope(f.A, new JsonObject { ["type"] = "channel", ["name"] = "New" }));
        var channel = response["dataObjects"]!["channel"]!;
        var session = response["dataObjects"]!["session"]!;
        Eq(f.A, channel["ownerUserId"]!.GetValue<string>());
        Eq(f.A, session["ownerUserId"]!.GetValue<string>());
        Eq("General", session["title"]!.GetValue<string>());
        var page = await f.Store.QueryAsync("channelSessions", Guid.Parse(channel["id"]!.GetValue<string>()));
        Eq(1, page["items"]!.AsArray().Count);
        Eq("channel", response["created"]![0]!["type"]!.GetValue<string>());
        Eq("session", response["created"]![1]!["type"]!.GetValue<string>());
    }
    public static async Task Session()
    {
        await using var f = await Fixture.Create();
        var response = await f.Send(Fixture.Envelope(f.B, new JsonObject { ["type"] = "session", ["channel"] = Fixture.Existing(f.Channel), ["title"] = "B's session" }));
        Eq(f.A, response["dataObjects"]!["channel"]!["ownerUserId"]!.GetValue<string>());
        Eq(f.B, response["dataObjects"]!["session"]!["ownerUserId"]!.GetValue<string>());
        var created = await f.Send(Fixture.Envelope(f.B, new JsonObject { ["type"] = "session", ["channel"] = new JsonObject { ["mode"] = "new", ["name"] = "B channel" }, ["title"] = "Only session" }));
        var channel = created["dataObjects"]!["channel"]!["id"]!.GetValue<string>();
        Eq(1, (await f.Store.QueryAsync("channelSessions", Guid.Parse(channel)))["items"]!.AsArray().Count);
        Eq(2, created["created"]!.AsArray().Count);
    }
    public static async Task Compound()
    {
        await using var f = await Fixture.Create();
        var response = await f.Send(f.Text("First", newContainers: true));
        Eq(3, response["created"]!.AsArray().Count);
        Eq("channel", response["created"]![0]!["type"]!.GetValue<string>());
        Eq("session", response["created"]![1]!["type"]!.GetValue<string>());
        Eq("message", response["created"]![2]!["type"]!.GetValue<string>());
        var message = response["dataObjects"]!["message"]!;
        Eq(message["id"]!.GetValue<string>(), message["threadRootMessageId"]!.GetValue<string>());
        Eq(1, message["depth"]!.GetValue<int>());
        True(message["createdAtUtc"]!.GetValue<string>().EndsWith('Z'), "Wire time ends Z.");
        var first = long.Parse(response["eventRange"]!["firstSequence"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var last = long.Parse(response["eventRange"]!["lastSequence"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        Eq(first + 2, last);
    }
    public static async Task References()
    {
        await using var f = await Fixture.Create();
        var request = f.Text("Missing");
        request["dataObject"]!["session"]!["id"] = Guid.NewGuid().ToString();
        var before = await f.Counts();
        await Error(404, "session_not_found", () => f.Send(request));
        Eq(before, await f.Counts());
        var other = await f.Send(Fixture.Envelope(f.B, new JsonObject { ["type"] = "channel", ["name"] = "Other" }));
        request = f.Text("Mismatch");
        request["dataObject"]!["session"]!["id"] = other["dataObjects"]!["session"]!["id"]!.GetValue<string>();
        await Error(422, "channel_session_mismatch", () => f.Send(request));
    }
    public static async Task Rollback()
    {
        await using var f = await Fixture.Create();
        var before = await f.Counts();
        var request = f.Text("Hello @missing_handle", newContainers: true);
        await Error(422, "unknown_mention", () => f.Send(request));
        Eq(before, await f.Counts());
        await Error(404, "request_not_committed", () => f.Store.GetReceiptAsync(Guid.Parse(request["requestId"]!.GetValue<string>())));
        request = f.Text("Reply", parent: Guid.NewGuid().ToString(), newContainers: true);
        await Error(404, "message_not_found", () => f.Send(request));
        Eq(before, await f.Counts());
    }
    public static async Task Mentions()
    {
        var parsed = CommandNormalizer.Mentions("@BRYAN ( @bryan), [@alice] person@casey.com \\@casey @ab @2casey @bryan___________________________________");
        Eq("alice,bryan", string.Join(',', parsed));
        await using var f = await Fixture.Create();
        var request = f.Text("Hello @BRYAN and (@bryan), person@casey.com \\@casey");
        request["dataObject"]!["targetUserIds"] = new JsonArray(f.B, f.B);
        var message = (await f.Send(request))["dataObjects"]!["message"]!;
        Eq(1, message["targetUserIds"]!.AsArray().Count);
        Eq(f.B, message["targetUserIds"]![0]!.GetValue<string>());
        Eq(1, (await f.Store.QueryAsync("mentions", Guid.Parse(f.B)))["items"]!.AsArray().Count);
        await Error(422, "unknown_mention", () => f.Send(f.Text("@not_present")));
    }
    public static async Task Threads()
    {
        await using var f = await Fixture.Create();
        var root = Fixture.MessageId(await f.Send(f.Text("Root")));
        var child = Fixture.MessageId(await f.Send(f.Text("Child", parent: root)));
        var grandchild = (await f.Send(f.Text("Nested", parent: child)))["dataObjects"]!["message"]!;
        Eq(3, grandchild["depth"]!.GetValue<int>());
        Eq(root, grandchild["threadRootMessageId"]!.GetValue<string>());
        var before = await f.Counts();
        await Error(422, "thread_depth_exceeded", () => f.Send(f.Text("Fourth", parent: grandchild["id"]!.GetValue<string>())));
        Eq(before, await f.Counts());
        var other = await f.Send(Fixture.Envelope(f.B, new JsonObject { ["type"] = "session", ["channel"] = Fixture.Existing(f.Channel), ["title"] = "Other" }));
        var request = f.Text("Bad cross-session", parent: root);
        request["dataObject"]!["session"]!["id"] = other["dataObjects"]!["session"]!["id"]!.GetValue<string>();
        await Error(422, "parent_session_mismatch", () => f.Send(request));
    }
    public static async Task PublicTargets()
    {
        await using var f = await Fixture.Create();
        var root = Fixture.MessageId(await f.Send(f.Text("@bryan attention")));
        var reply = (await f.Send(f.Text("Public reply", parent: root)))["dataObjects"]!["message"]!;
        Eq(0, reply["targetUserIds"]!.AsArray().Count);
        Eq(1, (await f.Store.QueryAsync("mentions", Guid.Parse(f.B)))["items"]!.AsArray().Count);
        var history = await f.Store.QueryAsync("sessionMessages", Guid.Parse(f.Session), new PageQuery(View: "flat"));
        Eq(2, history["items"]!.AsArray().Count);
    }
    public static async Task Images()
    {
        await using var f = await Fixture.Create();
        var image = Fixture.Image();
        var request = f.Text("", newContainers: true);
        request["dataObject"]!["content"] = new JsonObject { ["type"] = "image", ["caption"] = "@bryan pixels" };
        var response = await f.Store.ExecuteAsync(Fixture.Element(request), image);
        var descriptor = response["dataObjects"]!["message"]!["content"]!["image"]!;
        var bytes = await f.Store.GetImageAsync(Guid.Parse(descriptor["id"]!.GetValue<string>()));
        True(bytes.Bytes.SequenceEqual(image.Bytes), "Stored image bytes match original.");
        Eq(image.Sha256, descriptor["sha256"]!.GetValue<string>());
        var retried = await f.Store.ExecuteAsync(Fixture.Element(request), image with { Bytes = image.Bytes.ToArray() });
        True(retried["replayed"]!.GetValue<bool>(), "Equivalent byte retry.");
        var changed = image.Bytes.Concat(new byte[] { 0 }).ToArray();
        await Error(409, "idempotency_conflict", () => f.Store.ExecuteAsync(Fixture.Element(request), image with { Bytes = changed, Sha256 = Convert.ToHexStringLower(SHA256.HashData(changed)) }));
        var stored = await f.Store.QueryAsync("messages", Guid.Parse(Fixture.MessageId(response)));
        True(!stored.ToJsonString().Contains(Convert.ToBase64String(image.Bytes), StringComparison.Ordinal), "No base64 in message JSON.");
        var bad = f.Text("", newContainers: true);
        bad["dataObject"]!["content"] = new JsonObject { ["type"] = "image" };
        var before = await f.Counts();
        await Error(422, "image_dimensions_exceeded", () => f.Store.ExecuteAsync(Fixture.Element(bad), image with { Width = 8193 }));
        Eq(before, await f.Counts());
    }
    public static async Task Subscriptions()
    {
        await using var f = await Fixture.Create();
        var beforeEvents = await f.Scalar("SELECT count(*) FROM EventLog;");
        var request = f.Subscription(new JsonObject { ["catalog"] = true }, "beginning");
        var created = await f.Store.CreateSubscriptionAsync(Fixture.Element(request));
        var id = Fixture.SubscriptionId(created);
        True((await f.Store.CreateSubscriptionAsync(Fixture.Element(request)))["replayed"]!.GetValue<bool>(), "Create subscription retry.");
        await f.Reopen();
        Eq("active", (await f.Store.GetSubscriptionAsync(id))["state"]!.GetValue<string>());
        await Error(403, "subscription_caller_mismatch", () => f.Close(id, f.B));
        var close = await f.Close(id);
        Eq("committed", close["state"]!.GetValue<string>());
        Eq("closed", close["subscriptionState"]!.GetValue<string>());
        Eq("closed", (await f.Store.GetSubscriptionAsync(id))["state"]!.GetValue<string>());
        await Error(410, "subscription_closed", () => f.Store.ReadEventsAsync(id));
        Eq(beforeEvents, await f.Scalar("SELECT count(*) FROM EventLog;"));
        Eq("active", (await f.Store.GetReceiptAsync(Guid.Parse(request["requestId"]!.GetValue<string>())))["subscription"]!["state"]!.GetValue<string>());
    }
    public static async Task Coupled()
    {
        await using var f = await Fixture.Create();
        var request = Fixture.Envelope(f.A, new JsonObject { ["type"] = "channel", ["name"] = "Coupled" });
        request["subscribe"] = new JsonObject { ["scope"] = "resolvedSession", ["start"] = "beforeRequest" };
        var response = await f.Send(request);
        var id = Fixture.SubscriptionId(response);
        var page = await f.Store.ReadEventsAsync(id);
        Eq(1, page["items"]!.AsArray().Count);
        Eq("session.created", page["items"]![0]!["eventType"]!.GetValue<string>());
        await f.Close(id);
        var retry = await f.Send(request);
        Eq(id.ToString("D"), retry["subscription"]!["id"]!.GetValue<string>());
        Eq("closed", (await f.Store.GetSubscriptionAsync(id))["state"]!.GetValue<string>());
    }
    public static async Task Sparse()
    {
        await using var f = await Fixture.Create(limits: new ProductLimits { EventHeaders = 3 });
        var id = Fixture.SubscriptionId(await f.Store.CreateSubscriptionAsync(Fixture.Element(f.Subscription(new JsonObject { ["sessionIds"] = new JsonArray(f.Session) }, "now"))));
        for (var i = 0; i < 8; i++) await f.Send(Fixture.Envelope(null, new JsonObject { ["type"] = "user", ["handle"] = "spare_" + i, ["displayName"] = "Spare" }));
        var message = Fixture.MessageId(await f.Send(f.Text("Matching")));
        var page = await f.Store.ReadEventsAsync(id);
        Eq(0, page["items"]!.AsArray().Count);
        True(page["hasMore"]!.GetValue<bool>(), "Sparse first batch has more.");
        var all = new List<string>();
        var previous = "";
        for (var i = 0; i < 10; i++)
        {
            foreach (var item in page["items"]!.AsArray()) all.Add(item!["dataObject"]!["id"]!.GetValue<string>());
            var cursor = page["nextCursor"]!.GetValue<string>();
            True(cursor != previous, "Sparse cursor progresses.");
            if (!page["hasMore"]!.GetValue<bool>()) break;
            previous = cursor; page = await f.Store.ReadEventsAsync(id, cursor);
        }
        Eq(message, string.Join(',', all));
    }
    public static async Task Overlap()
    {
        await using var f = await Fixture.Create();
        var id = Fixture.SubscriptionId(await f.Store.CreateSubscriptionAsync(Fixture.Element(f.Subscription(new JsonObject { ["channelIds"] = new JsonArray(f.Channel), ["targetUserId"] = f.B }, "now"))));
        await f.Send(f.Text("@bryan one"));
        await f.Send(f.Text("@bryan two"));
        var first = await f.Store.ReadEventsAsync(id, limit: 1);
        Eq(1, first["items"]!.AsArray().Count);
        var second = await f.Store.ReadEventsAsync(id, first["nextCursor"]!.GetValue<string>());
        Eq(1, second["items"]!.AsArray().Count);
        var independent = await f.Store.ReadEventsAsync(id);
        Eq(2, independent["items"]!.AsArray().Count);
    }
    public static async Task Cursors()
    {
        await using var f = await Fixture.Create();
        var id = Fixture.SubscriptionId(await f.Store.CreateSubscriptionAsync(Fixture.Element(f.Subscription(new JsonObject { ["allMessages"] = true }, "now"))));
        await Error(400, "invalid_cursor", () => f.Store.ReadEventsAsync(id, "invalid"));
        await Error(409, "store_changed", () => f.Store.ReadEventsAsync(id, Guid.NewGuid() + ":0"));
        await Error(409, "cursor_ahead_of_store", () => f.Store.ReadEventsAsync(id, f.Store.StoreEpoch + ":999999"));
        await Error(400, "cursor_before_subscription", () => f.Store.ReadEventsAsync(id, f.Store.StoreEpoch + ":0"));
        var valid = await f.Store.ReadEventsAsync(id);
        Eq(0, valid["items"]!.AsArray().Count);
    }
    public static async Task Snapshots()
    {
        await using var f = await Fixture.Create();
        var first = await f.Store.QueryAsync("users", query: new PageQuery(Limit: 1));
        var h = first["snapshotCursor"]!.GetValue<string>();
        await f.Send(Fixture.Envelope(null, new JsonObject { ["type"] = "user", ["handle"] = "later", ["displayName"] = "Later" }));
        var all = new List<string>();
        var page = first;
        while (true)
        {
            all.AddRange(page["items"]!.AsArray().Select(x => x!["handle"]!.GetValue<string>()));
            var token = page["nextPageToken"]?.GetValue<string>();
            if (token is null) break;
            page = await f.Store.QueryAsync("users", query: new PageQuery(Limit: 1, PageToken: token, AsOf: h));
        }
        Eq("alice,bryan,casey", string.Join(',', all));
        await Error(400, "invalid_page_token", () => f.Store.QueryAsync("users", query: new PageQuery(Limit: 1, PageToken: first["nextPageToken"]!.GetValue<string>(), HandlePrefix: "a")));
        await Error(400, "invalid_page_token", () => f.Store.QueryAsync("users", query: new PageQuery(PageToken: "!!!")));
    }
    public static async Task ThreadPages()
    {
        await using var f = await Fixture.Create();
        var root = Fixture.MessageId(await f.Send(f.Text("Root\rsecond\r\nthird\nfourth")));
        var h = (await f.Store.GetStatusAsync())["headCursor"]!.GetValue<string>();
        var child = Fixture.MessageId(await f.Send(f.Text("Child\rsecond\r\nthird\nfourth", parent: root)));
        var nested = Fixture.MessageId(await f.Send(f.Text("Nested", parent: child)));
        var old = await f.Store.QueryAsync("sessionMessages", Guid.Parse(f.Session), new PageQuery(AsOf: h));
        Eq(0, old["items"]![0]!["replyCount"]!.GetValue<int>());
        var first = await f.Store.QueryAsync("thread", Guid.Parse(nested), new PageQuery(Limit: 1));
        Eq(root, first["root"]!["id"]!.GetValue<string>());
        Eq(child, first["items"]![0]!["id"]!.GetValue<string>());
        Eq("Root\nsecond\nthird\nfourth", first["root"]!["content"]!["text"]!.GetValue<string>());
        Eq("Child\nsecond\nthird\nfourth", first["items"]![0]!["content"]!["text"]!.GetValue<string>());
        Eq(1, first["items"]![0]!["replyCount"]!.GetValue<int>());
        await Error(400, "invalid_contract", () => f.Send(f.Text("Still invalid\u0001", parent: root)));
        var second = await f.Store.QueryAsync("thread", Guid.Parse(nested), new PageQuery(Limit: 1, PageToken: first["nextPageToken"]!.GetValue<string>()));
        Eq(nested, second["items"]![0]!["id"]!.GetValue<string>());
        Eq(child, second["contextMessages"]![0]!["id"]!.GetValue<string>());
        Eq(2, second["root"]!["replyCount"]!.GetValue<int>());
    }
    public static async Task Concurrent()
    {
        await using var f = await Fixture.Create();
        var request = f.Text("Concurrent", newContainers: true);
        var before = long.Parse(await f.Scalar("SELECT count(*) FROM EventLog;"), CultureInfo.InvariantCulture);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => f.Send(request.DeepClone().AsObject())));
        Eq(1, results.Select(Fixture.MessageId).Distinct().Count());
        Eq(before + 3, long.Parse(await f.Scalar("SELECT count(*) FROM EventLog;"), CultureInfo.InvariantCulture));
        Eq(11, results.Count(r => r["replayed"]!.GetValue<bool>()));
    }
    public static async Task BeforeCommit()
    {
        bool armed = false;
        await using var f = await Fixture.Create(fault: point => { if (armed && point == StoreFaultPoint.BeforeCommit) { armed = false; throw new InjectedFault(); } });
        var before = await f.Counts();
        var request = f.Text("Crash before", newContainers: true);
        armed = true;
        await Throws<InjectedFault>(() => f.Send(request));
        Eq(before, await f.Counts());
        var accepted = await f.Send(request);
        Eq(3, accepted["created"]!.AsArray().Count);
    }
    public static async Task AfterCommit()
    {
        bool armed = false;
        await using var f = await Fixture.Create(fault: point => { if (armed && point == StoreFaultPoint.AfterCommitBeforeNotification) { armed = false; throw new InjectedFault(); } });
        var id = Fixture.SubscriptionId(await f.Store.CreateSubscriptionAsync(Fixture.Element(f.Subscription(new JsonObject { ["allMessages"] = true }, "now"))));
        var request = f.Text("Crash after");
        var notified = 0;
        f.Store.Committed += () => notified++;
        armed = true;
        await Throws<InjectedFault>(() => f.Send(request));
        Eq(0, notified);
        var retry = await f.Send(request);
        True(retry["replayed"]!.GetValue<bool>(), "Committed retry recovered.");
        Eq(1, (await f.Store.ReadEventsAsync(id))["items"]!.AsArray().Count);
    }
    public static async Task Acknowledgements()
    {
        var armed = false;
        await using var f = await Fixture.Create(fault: point => { if (armed && point == StoreFaultPoint.AfterCommitBeforeNotification) { armed = false; throw new InjectedFault(); } });
        var message = Guid.Parse(Fixture.MessageId(await f.Send(f.Text("Public message"))));
        var another = Guid.Parse(Fixture.MessageId(await f.Send(f.Text("Another message"))));
        var before = await f.Store.QueryAsync("messages", message);
        var beforeCursor = (await f.Store.GetStatusAsync())["headCursor"]!.GetValue<string>();
        var sub = Fixture.SubscriptionId(await f.Store.CreateSubscriptionAsync(Fixture.Element(f.Subscription(new JsonObject { ["allMessages"] = true }, "beginning"))));
        var ackSub = Fixture.SubscriptionId(await f.Store.CreateSubscriptionAsync(Fixture.Element(f.Subscription(new JsonObject { ["allAcknowledgements"] = true }, "beginning"))));
        await f.Store.ReadEventsAsync(sub);
        Eq(0, (await f.Store.QueryAsync("acknowledgements", message))["items"]!.AsArray().Count);
        JsonObject Ack(string user, Guid id) => Fixture.Envelope(user, new JsonObject { ["type"] = "acknowledgement", ["messageId"] = id.ToString() });
        var command = Ack(f.B, message);
        armed = true;
        await Throws<InjectedFault>(() => f.Send(command));
        await f.Reopen();
        var committed = await f.Store.GetReceiptAsync(Guid.Parse(command["requestId"]!.GetValue<string>()));
        var retry = await f.Send(command);
        var duplicate = await f.Send(Ack(f.B, message));
        True(retry["replayed"]!.GetValue<bool>(), "Recover the lost response by request ID.");
        Eq(committed["dataObjects"]!.ToJsonString(), duplicate["dataObjects"]!.ToJsonString());
        Eq(0, duplicate["created"]!.AsArray().Count);
        await f.Send(Ack(f.C, message));
        await f.Send(Ack(f.B, another));
        var first = await f.Store.QueryAsync("acknowledgements", message, new PageQuery(Limit: 1));
        Eq(f.B, first["items"]![0]!["profile"]!["id"]!.GetValue<string>());
        var token = first["nextPageToken"]!.GetValue<string>();
        await f.Send(Ack(f.A, message));
        var second = await f.Store.QueryAsync("acknowledgements", message, new PageQuery(Limit: 1, PageToken: token));
        Eq(f.C, second["items"]![0]!["profile"]!["id"]!.GetValue<string>());
        True(second["nextPageToken"] is null, "The acknowledgement page retains its original snapshot.");
        Eq(1, (await f.Store.QueryAsync("acknowledgements", another))["items"]!.AsArray().Count);
        await Error(404, "message_not_found", () => f.Send(Ack(f.B, Guid.NewGuid())));
        await Error(404, "user_not_found", () => f.Send(Ack(Guid.NewGuid().ToString(), message)));
        var current = await f.Store.QueryAsync("messages", message);
        Eq(3, current["acknowledgementCount"]!.GetValue<int>());
        Eq("alice", current["latestAcknowledgingHandle"]!.GetValue<string>());
        current["acknowledgementCount"] = 0;
        current["latestAcknowledgingHandle"] = null;
        Eq(before.ToJsonString(), current.ToJsonString());
        var snapshot = await f.Store.QueryAsync("sessionMessages", Guid.Parse(f.Session), new PageQuery(AsOf: beforeCursor));
        Eq(0, snapshot["items"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == message.ToString())!["acknowledgementCount"]!.GetValue<int>());
        var events = await f.Store.ReadEventsAsync(ackSub, beforeCursor);
        Eq(0, (await f.Store.ReadEventsAsync(sub, beforeCursor))["items"]!.AsArray().Count);
        Eq(4, events["items"]!.AsArray().Count(item => item!["eventType"]!.GetValue<string>() == "acknowledgement.created"));
        Eq("4", await f.Scalar("SELECT count(*) FROM Acknowledgements;"));
        await Throws<SqliteException>(() => f.Scalar("INSERT INTO Acknowledgements SELECT MessageId, UserId, lower(hex(randomblob(16))), AcknowledgedUtcMs, Sequence FROM Acknowledgements LIMIT 1;"));
        var backup = Path.Combine(f.Directory, "public-profile-backup.db");
        await f.Store.DisposeAsync();
        await StoreMaintenance.BackupAsync(f.Directory, backup);
        await StoreMaintenance.RestoreAsync(f.Directory, backup);
        await f.Reopen();
        Eq(3, (await f.Store.QueryAsync("acknowledgements", message))["items"]!.AsArray().Count);
        True((await f.Send(command))["replayed"]!.GetValue<bool>(), "Restore preserves the acknowledgement receipt.");
        await Error(409, "store_changed", () => f.Store.QueryAsync("acknowledgements", message, new PageQuery(PageToken: token)));
    }

    public static async Task Participants()
    {
        await using var f = await Fixture.Create();
        var channelId = Guid.Parse(f.Channel);
        var sessionId = Guid.Parse(f.Session);
        var sibling = (await f.Send(Fixture.Envelope(f.A, new JsonObject { ["type"] = "session", ["channel"] = Fixture.Existing(f.Channel), ["title"] = "Sibling" })))["dataObjects"]!["session"]!["id"]!.GetValue<string>();
        var saved = Fixture.SubscriptionId(await f.Store.CreateSubscriptionAsync(Fixture.Element(f.Subscription(new JsonObject { ["channelIds"] = new JsonArray(f.Channel) }, "now"))));
        Task<JsonObject> Change(string user, bool joined, string? session = null) => f.Send(Fixture.Envelope(user,
            new JsonObject { ["type"] = "participation", [session is null ? "channelId" : "sessionId"] = session ?? f.Channel, ["joined"] = joined }));
        async Task<string[]> Roster(string kind, Guid id) => (await f.Store.QueryAsync(kind, id))["items"]!.AsArray()
            .Select(x => x!["profile"]!["id"]!.GetValue<string>()).ToArray();
        Eq(0, (await Roster("channelParticipants", channelId)).Length);
        await Change(f.B, true, f.Session);
        await Change(f.B, true, sibling);
        await Change(f.C, true);
        await Change(f.B, true);
        Eq(2, (await Roster("channelParticipants", channelId)).Length);
        Eq(f.B, (await Roster("sessionParticipants", sessionId)).Single());
        var first = await f.Store.QueryAsync("channelParticipants", channelId, new PageQuery(Limit: 1));
        Eq(f.C, first["items"]![0]!["profile"]!["id"]!.GetValue<string>());
        var channelLeave = await Change(f.B, false);
        True(channelLeave["dataObjects"]!["participation"]!["channelJoined"]!.GetValue<bool>(), "Session membership keeps the profile in its parent roster.");
        await Change(f.B, false, f.Session);
        Eq(0, (await Roster("sessionParticipants", sessionId)).Length);
        Eq(f.B, (await Roster("sessionParticipants", Guid.Parse(sibling))).Single());
        await Change(f.B, false, sibling);
        Eq(f.C, (await Roster("channelParticipants", channelId)).Single());
        Eq(0, (await Change(f.B, false, sibling))["created"]!.AsArray().Count);
        var second = await f.Store.QueryAsync("channelParticipants", channelId, new PageQuery(PageToken: first["nextPageToken"]!.GetValue<string>()));
        Eq(f.B, second["items"]![0]!["profile"]!["id"]!.GetValue<string>());
        var events = (await f.Store.ReadEventsAsync(saved))["items"]!.AsArray();
        Eq(7, events.Count);
        True(events.All(x => x!["eventType"]!.GetValue<string>() == "participation.changed"), "Only transitions emit scoped events.");
        await f.Close(saved);
        await f.Reopen();
        Eq(f.C, (await Roster("channelParticipants", channelId)).Single());
        Eq("7", await f.Scalar("SELECT count(*) FROM Participation;"));
    }

    public static async Task Restore() => await RestoreScenario(null);
    public static async Task RestoreBefore() => await RestoreScenario(RestoreFaultPoint.PreparedBeforePublication);
    public static async Task RestoreAfter() => await RestoreScenario(RestoreFaultPoint.Published);
    private static async Task RestoreScenario(RestoreFaultPoint? fault)
    {
        await using var f = await Fixture.Create();
        var saved = await f.Send(f.Text("Backed up"));
        var sub = Fixture.SubscriptionId(await f.Store.CreateSubscriptionAsync(Fixture.Element(f.Subscription(new JsonObject { ["allMessages"] = true }, "now"))));
        var epoch = f.Store.StoreEpoch;
        var path = Path.Combine(f.Directory, "selected-backup.db");
        await f.Store.DisposeAsync();
        await StoreMaintenance.BackupAsync(f.Directory, path);
        await f.Reopen();
        await f.Send(f.Text("Later"));
        await f.Store.DisposeAsync();
        if (fault.HasValue)
            await Throws<InjectedFault>(() => StoreMaintenance.RestoreAsync(f.Directory, path, faultInjector: point => { if (point == fault.Value) throw new InjectedFault(); }));
        else await StoreMaintenance.RestoreAsync(f.Directory, path);
        await f.Reopen();
        var after = fault != RestoreFaultPoint.PreparedBeforePublication;
        Eq(after ? 1 : 2, (await f.Store.QueryAsync("sessionMessages", Guid.Parse(f.Session)))["items"]!.AsArray().Count);
        True(after ? f.Store.StoreEpoch != epoch : f.Store.StoreEpoch == epoch, "Epoch changes exactly when prepared restore is published.");
        Eq(after ? "closed" : "active", (await f.Store.GetSubscriptionAsync(sub))["state"]!.GetValue<string>());
        var receipt = await f.Store.GetReceiptAsync(Guid.Parse(saved["requestId"]!.GetValue<string>()));
        True(receipt["afterRequestCursor"]!.GetValue<string>().StartsWith(f.Store.StoreEpoch + ":", StringComparison.Ordinal), "Receipt cursor projected into current history.");
        True(System.IO.Directory.GetFiles(f.Directory, "messaging.safety-restore-*.db").Length > 0, "Safety backup retained.");
    }
    public static async Task InvalidRestore()
    {
        await using var f = await Fixture.Create();
        var destination = Path.Combine(f.Directory, "backup.db");
        await Error(503, "storage_unavailable", () => StoreMaintenance.BackupAsync(f.Directory, destination));
        var epoch = f.Store.StoreEpoch;
        var invalid = Path.Combine(f.Directory, "invalid.db");
        await File.WriteAllTextAsync(invalid, "Not SQLite");
        await f.Store.DisposeAsync();
        await Throws<SqliteException>(() => StoreMaintenance.RestoreAsync(f.Directory, invalid));
        await f.Reopen();
        Eq(epoch, f.Store.StoreEpoch);
        Eq(3, (await f.Store.QueryAsync("users"))["items"]!.AsArray().Count);
    }
    public static async Task Limits()
    {
        await using var f = await Fixture.Create(limits: new ProductLimits { TextLength = 8, CaptionLength = 4, TargetUsers = 1, FilterIds = 1, PageSize = 2 });
        await Error(400, "invalid_contract", () => f.Send(f.Text("too long text")));
        var targets = f.Text("ok"); targets["dataObject"]!["targetUserIds"] = new JsonArray(f.B, f.C);
        await Error(400, "invalid_contract", () => f.Send(targets));
        await Error(400, "invalid_contract", () => f.Store.QueryAsync("users", query: new PageQuery(Limit: 3)));
        var sub = f.Subscription(new JsonObject { ["channelIds"] = new JsonArray(f.Channel), ["sessionIds"] = new JsonArray(f.Session) }, "now");
        await Error(400, "invalid_contract", () => f.Store.CreateSubscriptionAsync(Fixture.Element(sub)));
    }
    public static async Task ByteBudget()
    {
        await using var f = await Fixture.Create();
        var sub = Fixture.SubscriptionId(await f.Store.CreateSubscriptionAsync(Fixture.Element(f.Subscription(new JsonObject { ["allMessages"] = true }, "now"))));
        await f.Send(f.Text(new string('x', 150)));
        await f.Send(f.Text(new string('y', 150)));
        var full = await f.Store.ReadEventsAsync(sub);
        var firstBytes = Encoding.UTF8.GetByteCount(full["items"]![0]!.ToJsonString());
        await f.Reopen(new ProductLimits { EventPayloadBytes = firstBytes + 8, EventFrameBytes = firstBytes + 8 });
        var first = await f.Store.ReadEventsAsync(sub);
        Eq(1, first["items"]!.AsArray().Count);
        True(first["hasMore"]!.GetValue<bool>(), "Byte budget retains excluded next match.");
        var second = await f.Store.ReadEventsAsync(sub, first["nextCursor"]!.GetValue<string>());
        Eq(1, second["items"]!.AsArray().Count);
        True(first["items"]![0]!["cursor"]!.GetValue<string>() != second["items"]![0]!["cursor"]!.GetValue<string>(), "Second matching event not skipped or duplicated.");
    }
    public static async Task SubscriptionPages()
    {
        await using var f = await Fixture.Create();
        for (var i = 0; i < 5; i++) await f.Store.CreateSubscriptionAsync(Fixture.Element(f.Subscription(new JsonObject { ["catalog"] = true }, "now")));
        var all = new List<string>(); string? token = null;
        do
        {
            var page = await f.Store.QueryAsync("subscriptions", query: new PageQuery(Limit: 2, PageToken: token, OwnerUserId: Guid.Parse(f.A)));
            all.AddRange(page["items"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()));
            token = page["nextPageToken"]?.GetValue<string>();
        } while (token is not null);
        Eq(5, all.Count); Eq(5, all.Distinct().Count());
    }
    public static async Task RelationalConstraints()
    {
        await using var f = await Fixture.Create();
        var root = Fixture.MessageId(await f.Send(f.Text("Root")));
        var other = (await f.Send(Fixture.Envelope(f.B, new JsonObject { ["type"] = "session", ["channel"] = Fixture.Existing(f.Channel), ["title"] = "Other" })))["dataObjects"]!["session"]!["id"]!.GetValue<string>();
        await using var connection = f.Connection();
        await connection.OpenAsync();
        foreach (var values in new[] { (other, root, root, 2), (f.Session, root, root, 4), (f.Session, (string?)null, root, 1) })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO Messages(Id,SessionId,AuthorUserId,Kind,Text,ParentMessageId,ThreadRootMessageId,Depth,CreatedUtcMs,CreatedSequence) VALUES($id,$session,$author,'text','bad',$parent,$root,$depth,0,10000);";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$session", values.Item1);
            command.Parameters.AddWithValue("$author", f.A);
            command.Parameters.AddWithValue("$parent", (object?)values.Item2 ?? DBNull.Value);
            command.Parameters.AddWithValue("$root", values.Item3);
            command.Parameters.AddWithValue("$depth", values.Item4);
            await Throws<SqliteException>(() => command.ExecuteNonQueryAsync());
        }
        await using (var invalidContent = connection.CreateCommand())
        {
            invalidContent.CommandText = "INSERT INTO Messages(Id,SessionId,AuthorUserId,Kind,Text,ImageId,ParentMessageId,ThreadRootMessageId,Depth,CreatedUtcMs,CreatedSequence) VALUES($id,$session,$author,'text',NULL,NULL,NULL,$id,1,0,10001);";
            invalidContent.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            invalidContent.Parameters.AddWithValue("$session", f.Session);
            invalidContent.Parameters.AddWithValue("$author", f.A);
            await Throws<SqliteException>(() => invalidContent.ExecuteNonQueryAsync());
        }
        var imageRequest = f.Text("");
        imageRequest["dataObject"]!["content"] = new JsonObject { ["type"] = "image" };
        var imageReceipt = await f.Store.ExecuteAsync(Fixture.Element(imageRequest), Fixture.Image());
        var imageId = imageReceipt["dataObjects"]!["message"]!["content"]!["image"]!["id"]!.GetValue<string>();
        await using (var reusedImage = connection.CreateCommand())
        {
            reusedImage.CommandText = "INSERT INTO Messages(Id,SessionId,AuthorUserId,Kind,Text,ImageId,ParentMessageId,ThreadRootMessageId,Depth,CreatedUtcMs,CreatedSequence) VALUES($id,$session,$author,'image',NULL,$image,NULL,$id,1,0,10002);";
            reusedImage.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            reusedImage.Parameters.AddWithValue("$session", f.Session);
            reusedImage.Parameters.AddWithValue("$author", f.A);
            reusedImage.Parameters.AddWithValue("$image", imageId);
            await Throws<SqliteException>(() => reusedImage.ExecuteNonQueryAsync());
        }
        Eq("2", await f.Scalar("SELECT count(*) FROM Messages;"));
    }

    public static async Task WriterContention()
    {
        await using var f = await Fixture.Create(limits: new ProductLimits { WriteWaitSeconds = 1, DatabaseTimeoutSeconds = 1 });
        var before = await f.Counts();
        var request = f.Text("Contention must remain atomic", newContainers: true);
        await using var connection = f.Connection();
        await connection.OpenAsync();
        await using (var held = connection.BeginTransaction())
        {
            var timer = Stopwatch.StartNew();
            await Error(503, "database_busy", () => f.Send(request));
            True(timer.Elapsed < TimeSpan.FromSeconds(5), "SQLite contention exceeded the finite configured timeout.");
            Eq(before, await f.Counts());
            await Error(404, "request_not_committed", () => f.Store.GetReceiptAsync(Guid.Parse(request["requestId"]!.GetValue<string>())));
            await held.RollbackAsync();
        }
        var receipt = await f.Send(request);
        True(!receipt["replayed"]!.GetValue<bool>(), "The failed request must be safely retryable.");
        Eq("1", await f.Scalar("SELECT count(*) FROM Messages;"));
    }

    public static async Task UnknownMigration()
    {
        await using var f = await Fixture.Create();
        var before = await f.Counts();
        var epoch = f.Store.StoreEpoch;
        await f.Store.DisposeAsync();
        await f.Scalar("INSERT INTO __EFMigrationsHistory(MigrationId,ProductVersion) VALUES('20991231000000_UnknownFutureSchema','99.0.0');");
        await Throws<InvalidDataException>(() => f.Reopen());
        Eq(before, await f.Counts());
        Eq(epoch, await f.Scalar("SELECT StoreEpoch FROM AppMetadata WHERE Id=1;"));
        Eq("3", await f.Scalar("SELECT count(*) FROM __EFMigrationsHistory;"));
        await f.Scalar("DELETE FROM __EFMigrationsHistory WHERE MigrationId='20991231000000_UnknownFutureSchema';");
        await f.Reopen();
        Eq(epoch, f.Store.StoreEpoch);
    }

    public static async Task CorruptStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "local-messaging-corrupt-test-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "messaging.db");
        var contents = Encoding.UTF8.GetBytes("Deliberately invalid SQLite database; startup must preserve these exact bytes.");
        await File.WriteAllBytesAsync(path, contents);
        try
        {
            SqliteConnection.ClearAllPools();
            await using var store = new MessagingStore(new StoreOptions(directory));
            await Throws<SqliteException>(() => store.InitializeAsync());
            SqliteConnection.ClearAllPools();
            var afterFailure = await File.ReadAllBytesAsync(path);
            True(contents.SequenceEqual(afterFailure), "Failed startup changed the corrupt store.");
            await using var retry = new MessagingStore(new StoreOptions(directory));
            await Throws<SqliteException>(() => retry.InitializeAsync());
            SqliteConnection.ClearAllPools();
            var afterRetry = await File.ReadAllBytesAsync(path);
            True(contents.SequenceEqual(afterRetry), "Startup retry replaced the corrupt store.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    private static void Eq<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}.");
    }
    private static void True(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Error(int status, string code, Func<Task> action)
    {
        try { await action(); throw new Exception("Expected API error " + code); }
        catch (ApiException ex) { Eq(status, ex.Status); Eq(code, ex.Code); }
    }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected exception " + typeof(T).Name);
    }
    private sealed class InjectedFault : Exception;
}

sealed class Fixture : IAsyncDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "local-messaging-store-test-" + Guid.NewGuid().ToString("N"));
    public MessagingStore Store { get; private set; } = null!;
    public string A { get; private set; } = "";
    public string B { get; private set; } = "";
    public string C { get; private set; } = "";
    public string Channel { get; private set; } = "";
    public string Session { get; private set; } = "";
    private ProductLimits? _limits;
    private Action<StoreFaultPoint>? _fault;
    public static async Task<Fixture> Create(bool seed = true, ProductLimits? limits = null, Action<StoreFaultPoint>? fault = null)
    {
        var f = new Fixture { _limits = limits, _fault = fault };
        f.Store = new MessagingStore(new StoreOptions(f.Directory, Limits: limits), fault);
        await f.Store.InitializeAsync();
        if (seed)
        {
            var users = new List<string>();
            foreach (var handle in new[] { "alice", "bryan", "casey" })
                users.Add((await f.Send(Envelope(null, new JsonObject { ["type"] = "user", ["handle"] = handle, ["displayName"] = handle })))["dataObjects"]!["user"]!["id"]!.GetValue<string>());
            f.A = users[0]; f.B = users[1]; f.C = users[2];
            var channel = await f.Send(Envelope(f.A, new JsonObject { ["type"] = "channel", ["name"] = "General" }));
            f.Channel = channel["dataObjects"]!["channel"]!["id"]!.GetValue<string>();
            f.Session = channel["dataObjects"]!["session"]!["id"]!.GetValue<string>();
        }
        return f;
    }
    public async Task Reopen(ProductLimits? limits = null)
    {
        await Store.DisposeAsync();
        if (limits is not null) _limits = limits;
        Store = new MessagingStore(new StoreOptions(Directory, Limits: _limits), _fault);
        await Store.InitializeAsync();
    }
    public Task<JsonObject> Send(JsonObject node) => Store.ExecuteAsync(Element(node));
    public Task<JsonObject> Close(Guid id, string? caller = null) => Store.CloseSubscriptionAsync(id, Element(new JsonObject
    {
        ["contractVersion"] = 1, ["requestId"] = Guid.NewGuid().ToString("D"), ["callerUserId"] = caller ?? A
    }));
    public JsonObject Text(string text, string? parent = null, bool newContainers = false) => Envelope(A, new JsonObject
    {
        ["type"] = "message",
        ["channel"] = newContainers ? new JsonObject { ["mode"] = "new", ["name"] = "Compound" } : Existing(Channel),
        ["session"] = newContainers ? new JsonObject { ["mode"] = "new", ["title"] = "Discussion" } : Existing(Session),
        ["content"] = new JsonObject { ["type"] = "text", ["text"] = text }, ["parentMessageId"] = parent
    });
    public JsonObject Subscription(JsonObject filter, string mode, string? cursor = null, Guid? requestId = null) => new()
    {
        ["contractVersion"] = 1, ["requestId"] = (requestId ?? Guid.NewGuid()).ToString("D"), ["callerUserId"] = A,
        ["filter"] = filter, ["start"] = cursor is null ? new JsonObject { ["mode"] = mode } : new JsonObject { ["mode"] = mode, ["cursor"] = cursor }
    };
    public static JsonObject Envelope(string? caller, JsonObject data, Guid? requestId = null) => new()
    {
        ["contractVersion"] = 1, ["requestId"] = (requestId ?? Guid.NewGuid()).ToString("D"), ["callerUserId"] = caller, ["dataObject"] = data
    };
    public static JsonObject Existing(string id) => new() { ["mode"] = "existing", ["id"] = id };
    public static JsonElement Element(JsonNode node) { using var document = JsonDocument.Parse(node.ToJsonString()); return document.RootElement.Clone(); }
    public static string MessageId(JsonObject receipt) => receipt["dataObjects"]!["message"]!["id"]!.GetValue<string>();
    public static Guid SubscriptionId(JsonObject receipt) => Guid.Parse(receipt["subscription"]!["id"]!.GetValue<string>());
    public static ValidatedImage Image() => Workload.CreatePngFixture(1);
    public SqliteConnection Connection() => new(new SqliteConnectionStringBuilder { DataSource = Store.DatabasePath, ForeignKeys = true, Pooling = false }.ToString());
    public async Task<string> Scalar(string sql)
    {
        await using var connection = Connection(); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? "";
    }
    public async Task<string> Counts()
    {
        var counts = new List<string>();
        foreach (var table in new[] { "Users", "Channels", "Sessions", "Messages", "MessageTargets", "ImageAssets", "EventLog", "RequestReceipts", "Subscriptions" })
            counts.Add(await Scalar("SELECT count(*) FROM " + table + ";"));
        return string.Join(",", counts);
    }
    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
    }
}
