using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalMessaging.Client;
using LocalMessaging.WinUI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var results = new List<object>();
int failures = 0;
async Task Test(string name, Func<Task> action)
{
    try { await action(); results.Add(new { name, result = "passed" }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; results.Add(new { name, result = "failed", error = error.ToString() }); Console.WriteLine("FAIL " + name + ": " + error); }
}
static void Assert(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
static JsonObject Message(string id, string root, string? parent, long sequence) => new()
{
    ["id"] = id, ["threadRootMessageId"] = root, ["parentMessageId"] = parent, ["sequence"] = sequence.ToString()
};
await Test("A reply delivered during the initial thread snapshot survives its older response", () =>
{
    var state = new ThreadProjection();
    state.Open("root"); state.BeginPage();
    var reply = Message("reply", "root", "root", 2);
    Assert(!state.Observe(reply), "The pending query must buffer its live event.");
    state.CompletePage(new JsonObject
    {
        ["root"] = Message("root", "root", null, 1), ["items"] = new JsonArray(), ["contextMessages"] = new JsonArray(),
        ["snapshotCursor"] = "00000000-0000-4000-8000-000000000000:1", ["nextPageToken"] = null
    });
    Assert(state.Messages.Count == 2 && state.Messages.ContainsKey("reply") && !state.NeedsRefresh, "The live reply was lost across the query/stream gap.");
    reply["id"] = "mutated";
    Assert(state.Messages.ContainsKey("reply"), "The projection retained an externally mutable event.");
    return Task.CompletedTask;
});
await Test("Thread buffers and loaded windows remain bounded and retain missing-parent notice", () =>
{
    var state = new ThreadProjection(); state.Open("root"); state.BeginPage();
    for (int i = 0; i < 1000; i++) state.Observe(Message("r" + i, "root", "missing", i + 2));
    state.CompletePage(new JsonObject { ["root"] = Message("root", "root", null, 1), ["items"] = new JsonArray(), ["contextMessages"] = new JsonArray() });
    Assert(state.Messages.Count <= ThreadProjection.MaximumMessages && state.NeedsRefresh, "Thread exceeded its bound or hid omitted context.");
    return Task.CompletedTask;
});
await Test("Isolated endpoint options reject relative paths and preserve no-launch choice", () =>
{
    var path = Path.Combine(Path.GetTempPath(), "isolated", "endpoint.json");
    var options = NativeOptions.Parse(["--endpoint", path, "--no-launch-host"]);
    Assert(options.CustomEndpoint && !options.LaunchHost && options.EndpointPath == path, "The isolated options changed.");
    try { NativeOptions.Parse(["--endpoint", "relative.json"]); throw new Exception("Relative endpoint accepted."); }
    catch (ArgumentException) { }
    return Task.CompletedTask;
});

var epoch = Guid.NewGuid().ToString();
var instance = Guid.NewGuid();
var cursor = epoch + ":10";
var users = Enumerable.Range(1, 3).Select(i => new JsonObject
{
    ["id"] = Guid.NewGuid().ToString(), ["handle"] = "user" + i, ["displayName"] = "User " + i
}).ToArray();
var channelId = Guid.NewGuid().ToString();
var channel = new JsonObject { ["id"] = channelId, ["name"] = "Channel", ["ownerUserId"] = users[0]["id"]!.GetValue<string>() };
var sessions = Enumerable.Range(1, 51).Select(i => new JsonObject
{
    ["id"] = Guid.NewGuid().ToString(), ["channelId"] = channelId, ["title"] = "Session " + i, ["ownerUserId"] = users[0]["id"]!.GetValue<string>()
}).ToArray();
JsonObject Page(IEnumerable<JsonObject> values, string? next = null) => new()
{
    ["contractVersion"] = 1, ["items"] = new JsonArray(values.Select(x => (JsonNode)x.DeepClone()).ToArray()), ["snapshotCursor"] = cursor, ["nextPageToken"] = next
};
var liveDescriptors = new ConcurrentDictionary<string, string>();
var closeOwners = new ConcurrentQueue<string>();
var firstCreateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var releaseFirstCreate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
int creates = 0;
string? delayedSession = null;
var historyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var historyCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
var app = builder.Build();
app.MapGet("/api/v1/status", () => Results.Json(new { contractVersion = 1, ready = true, instanceId = instance, storeEpoch = epoch, headCursor = cursor }));
app.MapGet("/api/v1/users", () => Results.Json(Page(users)));
app.MapGet("/api/v1/users/{id}", (string id) => users.FirstOrDefault(u => u["id"]!.GetValue<string>() == id) is JsonObject user ? Results.Json(user) : Results.NotFound());
app.MapGet("/api/v1/channels", () => Results.Json(Page([channel])));
app.MapGet("/api/v1/channels/{id}", () => Results.Json(channel));
app.MapGet("/api/v1/channels/{id}/sessions", () => Results.Json(Page(sessions.Take(50), "next-session-page")));
app.MapGet("/api/v1/sessions/{id}", (string id) => Results.Json(sessions.Single(s => s["id"]!.GetValue<string>() == id)));
app.MapGet("/api/v1/sessions/{id}/messages", async (HttpContext context, string id) =>
{
    if (id == delayedSession)
    {
        historyEntered.TrySetResult();
        try { await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted); }
        catch (OperationCanceledException) { historyCanceled.TrySetResult(); throw; }
    }
    return Results.Json(Page([]));
});
app.MapPost("/api/v1/subscriptions", async (HttpContext context) =>
{
    var request = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
    if (Interlocked.Increment(ref creates) == 1)
    {
        firstCreateEntered.SetResult();
        await releaseFirstCreate.Task.WaitAsync(context.RequestAborted);
    }
    var id = Guid.NewGuid().ToString(); var owner = request["callerUserId"]!.GetValue<string>();
    liveDescriptors[id] = owner;
    return Results.Json(new JsonObject
    {
        ["contractVersion"] = 1, ["state"] = "committed", ["requestId"] = request["requestId"]!.GetValue<string>(), ["storeEpoch"] = epoch,
        ["subscription"] = new JsonObject { ["id"] = id, ["ownerUserId"] = owner, ["startCursor"] = cursor }
    });
});
app.MapPost("/api/v1/subscriptions/{id}/close", async (HttpContext context, string id) =>
{
    var request = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
    var owner = request["callerUserId"]!.GetValue<string>();
    Assert(liveDescriptors.TryRemove(id, out var actualOwner) && actualOwner == owner, "A transition closed another profile's subscription.");
    closeOwners.Enqueue(owner);
    return Results.Json(new { contractVersion = 1, state = "committed", requestId = request["requestId"]!.GetValue<string>(), storeEpoch = epoch });
});
app.MapGet("/api/v1/subscriptions/{id}/stream", async (HttpContext context) =>
{
    context.Response.ContentType = "text/event-stream";
    await context.Response.WriteAsync(": fixture heartbeat\n\n");
    await context.Response.Body.FlushAsync();
    await Task.Delay(Timeout.Infinite, context.RequestAborted);
});
await app.StartAsync();
var baseAddress = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/api/v1/";
var directory = Path.Combine(Path.GetTempPath(), "local-messaging-native-model-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var descriptorPath = Path.Combine(directory, "endpoint.json");
await File.WriteAllTextAsync(descriptorPath, JsonSerializer.Serialize(new EndpointDescriptor(instance, 1, Guid.Parse(epoch),
    "https://localhost:7443/messaging/v1/", baseAddress, new string('a', 64), 1, true), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
await using var model = new ShellViewModel(action => action(), new NativeOptions(descriptorPath, true, false));
try
{
    await model.ConnectAsync();
    await Test("Rapid profile changes and reconnect serialize subscription ownership without leaking a consumer", async () =>
    {
        var first = model.SelectUserAsync(Guid.Parse(users[0]["id"]!.GetValue<string>()));
        await firstCreateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = model.SelectUserAsync(Guid.Parse(users[1]["id"]!.GetValue<string>()));
        var third = model.SelectUserAsync(Guid.Parse(users[2]["id"]!.GetValue<string>()));
        var reconnect = model.ConnectAsync();
        releaseFirstCreate.SetResult();
        await Task.WhenAll(first, second, third, reconnect).WaitAsync(TimeSpan.FromSeconds(15));
        var finalOwner = users[2]["id"]!.GetValue<string>();
        Assert(model.ActiveUserId?.ToString() == finalOwner && liveDescriptors.Count == 1 && liveDescriptors.Values.Single() == finalOwner,
            "A stale transition changed the active profile or leaked a saved subscription.");
        Assert(creates == 4 && closeOwners.Count == 3, "Transitions did not stop and close each original owned subscription.");
    });
    await Test("A selected session outside the first navigation page remains selected after reload", async () =>
    {
        var hidden = sessions[50]["id"]!.GetValue<string>();
        await model.SelectChannelAsync(channelId);
        Assert(model.Sessions.All(s => s.Id != hidden), "The fixture must omit session 51 from its initial page.");
        await model.SelectSessionAsync(hidden);
        Assert(model.SessionId == hidden && model.SessionLabel.StartsWith("Session 51", StringComparison.Ordinal), "Direct navigation did not resolve the missing DTO.");
        await model.ReloadAsync();
        Assert(model.SessionId == hidden && model.Sessions.Any(s => s.Id == hidden), "Snapshot reload dropped a selected session beyond the first page.");
    });
    await Test("A new session cancels obsolete history before waiting for the transition gate", async () =>
    {
        delayedSession = sessions[0]["id"]!.GetValue<string>();
        var oldDraftScope = model.ComposerScope;
        var obsolete = model.SelectSessionAsync(delayedSession);
        await historyEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(!model.CanCompose(oldDraftScope) && !model.CanCompose(model.ComposerScope), "Composition remained enabled while the scope was changing.");
        var wanted = sessions[1]["id"]!.GetValue<string>();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await model.SelectSessionAsync(wanted).WaitAsync(TimeSpan.FromSeconds(3));
        await obsolete;
        await historyCanceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(model.SessionId == wanted && timer.Elapsed < TimeSpan.FromSeconds(3), "Obsolete history blocked or overwrote the new navigation.");
        Assert(!model.CanCompose(oldDraftScope) && model.CanCompose(model.ComposerScope), "The old conversation draft was admitted under the new selection.");
        delayedSession = null;
    });
    await Test("Navigation during snapshot bootstrap still installs one replay consumer", async () =>
    {
        delayedSession = model.SessionId;
        historyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        historyCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reload = model.ReloadAsync();
        await historyEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var wanted = sessions[2]["id"]!.GetValue<string>();
        await Task.WhenAll(reload, model.SelectSessionAsync(wanted)).WaitAsync(TimeSpan.FromSeconds(5));
        await historyCanceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(model.SessionId == wanted && liveDescriptors.Count == 1 && liveDescriptors.Values.Single() == model.ActiveUserId?.ToString(),
            "Canceled bootstrap history left the new view without an owned live subscription.");
        delayedSession = null;
    });
    await Test("A profile change never admits an old draft under the new caller during a held load", async () =>
    {
        var oldDraftScope = model.ComposerScope;
        var otherUser = Guid.Parse(users[1]["id"]!.GetValue<string>());
        delayedSession = model.SessionId;
        historyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        historyCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var switching = model.SelectUserAsync(otherUser);
        await historyEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(model.ActiveUserId == otherUser && model.IsTransitioning && !model.CanCompose(oldDraftScope)
            && !model.CanCompose(model.ComposerScope), "A new caller could consume an old or transitional composer scope.");
        await Task.WhenAll(switching, model.SelectSessionAsync(sessions[3]["id"]!.GetValue<string>())).WaitAsync(TimeSpan.FromSeconds(5));
        Assert(model.CanCompose(model.ComposerScope) && !model.CanCompose(oldDraftScope), "Profile change did not require a fresh matching composer scope.");
        Assert(liveDescriptors.Count == 1 && liveDescriptors.Values.Single() == otherUser.ToString(), "Profile bootstrap lost its new owned subscription.");
        delayedSession = null;
    });
    await model.DisposeAsync();
    Assert(liveDescriptors.IsEmpty, "Disposal left an owned subscription active.");
}
finally
{
    releaseFirstCreate.TrySetResult();
    await app.StopAsync(); await app.DisposeAsync();
    Directory.Delete(directory, true);
}
Console.WriteLine(JsonSerializer.Serialize(new { suite = "LocalMessaging.NativeModelTests", scope = "Actual native model source with an HTTP fixture; no WinUI runtime acceptance", passed = results.Count - failures, failed = failures, results }, new JsonSerializerOptions { WriteIndented = true }));
return failures == 0 ? 0 : 1;
