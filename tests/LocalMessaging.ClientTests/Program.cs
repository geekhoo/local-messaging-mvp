using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LocalMessaging.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var results = new List<object>();
var failures = 0;
async Task Test(string name, Func<Task> test)
{
    try { await test(); results.Add(new { name, result = "passed" }); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; results.Add(new { name, result = "failed", error = e.ToString() }); Console.WriteLine("FAIL " + name + ": " + e); }
}
static void Assert(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException(reason);
}
await Test("SSE all byte chunk sizes preserve UTF-8, CR/LF, multiline fields and explicit cursor", () =>
{
    var text = "\uFEFF: heartbeat\r\nid: epoch:7\revent: message.created\ndata: first 😀\r\ndata: λ second\n\n";
    var bytes = Encoding.UTF8.GetBytes(text);
    for (var chunk = 1; chunk <= 32; chunk++)
    {
        var parser = new SseParser();
        var frames = new List<SseFrame>();
        for (var i = 0; i < bytes.Length; i += chunk) frames.AddRange(parser.Feed(bytes.AsSpan(i, Math.Min(chunk, bytes.Length - i))));
        parser.Complete();
        Assert(frames.Count == 1 && frames[0].Data == "first 😀\nλ second" && frames[0].Id == "epoch:7"
            && frames[0].HasExplicitId && frames[0].Event == "message.created", "Fragmented SSE lost data or cursor.");
    }
    return Task.CompletedTask;
});
await Test("SSE controls do not inherit cursor advancement and incomplete EOF does not dispatch", () =>
{
    var parser = new SseParser();
    var frames = parser.Feed(Encoding.UTF8.GetBytes("id: e:1\ndata: one\n\nevent: server.stopping\ndata: {}\n\nid: ignored\0bad\ndata: two\n\ndata: unfinished"));
    parser.Complete();
    Assert(frames.Count == 3 && frames[0].HasExplicitId && !frames[1].HasExplicitId && !frames[2].HasExplicitId,
        "Control or invalid id advanced the cursor; incomplete data dispatched.");
    return Task.CompletedTask;
});
await Test("SSE byte budgets and malformed UTF-8 fail explicitly", () =>
{
    var parser = new SseParser();
    bool oversized = false;
    try { parser.Feed(Encoding.UTF8.GetBytes("data: " + new string('x', SseParser.MaximumFrameBytes + 1))); }
    catch (InvalidDataException) { oversized = true; }
    Assert(oversized, "Oversize SSE line accepted.");
    bool invalid = false;
    try { new SseParser().Feed([0xc3, 0x28]); }
    catch (DecoderFallbackException) { invalid = true; }
    Assert(invalid, "Invalid UTF-8 accepted.");
    return Task.CompletedTask;
});
await Test("Frozen command JSON ownership cannot change after draft mutation", () =>
{
    var caller = Guid.NewGuid();
    var original = new JsonObject { ["type"] = "channel", ["name"] = "Before", ["initialSessionTitle"] = "General" };
    var command = FrozenCommand.Create(caller, original);
    original["name"] = "After";
    var projection = command.GetEnvelope(); projection["callerUserId"] = Guid.NewGuid().ToString();
    Assert(command.GetEnvelope()["dataObject"]!["name"]!.GetValue<string>() == "Before", "Mutable draft changed frozen JSON.");
    Assert(command.CallerUserId == caller && command.GetEnvelope()["callerUserId"]!.GetValue<string>() == caller.ToString(), "Caller changed.");
    return Task.CompletedTask;
});
await Test("Client refuses remote bases and absolute API paths", async () =>
{
    bool remote = false;
    try { using var unused = new MessagingClient(new Uri("https://example.com/api/v1/")); }
    catch (ArgumentException) { remote = true; }
    Assert(remote, "Remote base was accepted.");
    using var client = new MessagingClient(new Uri("http://127.0.0.1:65534/api/v1/"));
    try { await client.GetAsync("https://example.com/"); throw new Exception("Absolute request was accepted."); }
    catch (ArgumentException) { }
});

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
var app = builder.Build();
var sent = new List<(string Json, byte[]? Image, string Header)>();
int failures422 = 0;
int temporaryStreams = 0;
app.MapPost("/api/v1/requests", async (HttpContext context) =>
{
    string json;
    byte[]? image = null;
    if (context.Request.HasFormContentType)
    {
        var form = await context.Request.ReadFormAsync();
        json = form["request"].ToString();
        using var memory = new MemoryStream();
        await form.Files["image"]!.CopyToAsync(memory);
        image = memory.ToArray();
    }
    else
    {
        using var reader = new StreamReader(context.Request.Body);
        json = await reader.ReadToEndAsync();
    }
    sent.Add((json, image, context.Request.Headers[MessagingClient.ClientHeader].ToString()));
    return Results.Json(new { contractVersion = 1, state = "committed", requestId = JsonNode.Parse(json)!["requestId"]!.GetValue<string>(), storeEpoch = "00000000-0000-4000-8000-000000000000" });
});
app.MapPost("/api/v1/reject", () =>
{
    Interlocked.Increment(ref failures422);
    return Results.Json(new { contractVersion = 1, code = "invalid_contract", detail = "Deliberate validation rejection" }, statusCode: 422);
});
app.MapPost("/api/v1/malformed-receipt/{mode}", async (HttpContext context, string mode) =>
{
    var request = await JsonNode.ParseAsync(context.Request.Body);
    return Results.Json(new { contractVersion = 1, state = mode == "state" ? "queued" : "committed",
        requestId = mode == "identity" ? Guid.NewGuid().ToString() : request!["requestId"]!.GetValue<string>(),
        storeEpoch = "00000000-0000-4000-8000-000000000000" });
});
static string EventFrame(long sequence, long? bodySequence = null, string epoch = "00000000-0000-4000-8000-000000000000")
{
    var cursor = epoch + ":" + sequence;
    return "id: " + cursor + "\nevent: message.created\ndata: " + new JsonObject
    {
        ["contractVersion"] = 1, ["eventType"] = "message.created", ["cursor"] = cursor,
        ["sequence"] = (bodySequence ?? sequence).ToString(), ["dataObject"] = new JsonObject { ["id"] = "message-" + sequence }
    }.ToJsonString() + "\n\n";
}
app.MapGet("/api/v1/subscriptions/{id}/stream", async (HttpContext context, string id) =>
{
    context.Response.ContentType = "text/event-stream";
    await context.Response.StartAsync();
    if (id.StartsWith("11111111"))
    {
        for (var i = 0; i < 10; i++)
        {
            await context.Response.WriteAsync(": keepalive\n\n");
            await context.Response.Body.FlushAsync();
            await Task.Delay(30, context.RequestAborted);
        }
    }
    if (id.StartsWith("22222222")) { await Task.Delay(2000, context.RequestAborted); return; }
    if (id.StartsWith("55555555") && Interlocked.Increment(ref temporaryStreams) == 1)
    {
        await context.Response.WriteAsync("event: database_busy\ndata: {\"contractVersion\":1}\n\n");
        await context.Response.Body.FlushAsync();
        return;
    }
    if (id.StartsWith("66666666"))
    {
        await context.Response.WriteAsync("event: store_changed\ndata: {\"contractVersion\":1}\n\n");
        await context.Response.Body.FlushAsync();
        return;
    }
    if (id.StartsWith("77777777"))
    {
        await context.Response.WriteAsync(EventFrame(1) + EventFrame(1) + EventFrame(0)
            + "id: 00000000-0000-4000-8000-000000000000:2\nevent: stream.checkpoint\ndata: {\"contractVersion\":1,\"cursor\":\"00000000-0000-4000-8000-000000000000:2\"}\n\n");
    }
    else if (id.StartsWith("88888888"))
        await context.Response.WriteAsync("id: 00000000-0000-4000-8000-000000000000:3\nevent: stream.checkpoint\ndata: {\"contractVersion\":1,\"cursor\":\"00000000-0000-4000-8000-000000000000:2\"}\n\n");
    else if (id.StartsWith("99999999")) await context.Response.WriteAsync(EventFrame(1, epoch: "00000000-0000-4000-8000-000000000001"));
    else if (id.StartsWith("44444444")) await context.Response.WriteAsync(EventFrame(1, 2));
    else await context.Response.WriteAsync(EventFrame(1));
    await context.Response.Body.FlushAsync();
    await Task.Delay(Timeout.Infinite, context.RequestAborted);
});
await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
using var api = new MessagingClient(new Uri(address + "/api/v1/"));
try
{
    await Test("Actual HTTP sends exact header and frozen multipart bytes across retry", async () =>
    {
        var bytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3 };
        var originalBytes = bytes.ToArray();
        var command = FrozenCommand.Create(Guid.NewGuid(), new JsonObject
        {
            ["type"] = "message", ["content"] = new JsonObject { ["type"] = "image", ["caption"] = "before" }
        }, bytes);
        bytes[8] = 255;
        await api.SendAsync(command);
        await api.SendAsync(command);
        Assert(sent.Count == 2 && sent[0].Json == sent[1].Json, "Retry command JSON changed.");
        Assert(sent.All(s => s.Header == "1" && s.Image!.SequenceEqual(originalBytes)), "Header or immutable image bytes changed.");
    });
    await Test("Maximum-length Unicode text is sent as bounded UTF-8 rather than expanded escapes", async () =>
    {
        var content = new string('漢', 16384);
        var command = FrozenCommand.Create(Guid.NewGuid(), new JsonObject
        {
            ["type"] = "message", ["channel"] = new JsonObject { ["mode"] = "existing", ["id"] = Guid.NewGuid().ToString() },
            ["session"] = new JsonObject { ["mode"] = "existing", ["id"] = Guid.NewGuid().ToString() },
            ["content"] = new JsonObject { ["type"] = "text", ["text"] = content }
        });
        await api.SendAsync(command);
        Assert(Encoding.UTF8.GetByteCount(sent[^1].Json) < 64 * 1024
            && JsonNode.Parse(sent[^1].Json)!["dataObject"]!["content"]!["text"]!.GetValue<string>() == content,
            "A legal UTF-8 message expanded beyond the shared JSON request budget or changed content.");
    });
    await Test("Validation failures are never automatically retried", async () =>
    {
        var command = FrozenCommand.Operation("reject", new JsonObject { ["contractVersion"] = 1, ["requestId"] = Guid.NewGuid().ToString(), ["callerUserId"] = null });
        try { await api.SendWithRecoveryAsync(command); throw new Exception("Validation rejection was swallowed."); }
        catch (MessagingApiException e) { Assert(e.Status == 422 && e.Code == "invalid_contract", "Wrong error mapping."); }
        Assert(failures422 == 1, "Validation request was retried.");
    });
    await Test("Successful HTTP with an unrelated or uncommitted receipt preserves uncertainty", async () =>
    {
        foreach (var mode in new[] { "state", "identity" })
        {
            var command = FrozenCommand.Operation("malformed-receipt/" + mode, new JsonObject
            {
                ["contractVersion"] = 1, ["requestId"] = Guid.NewGuid().ToString(), ["callerUserId"] = null
            });
            var originalId = command.RequestId;
            try { await api.SendWithRecoveryAsync(command); throw new Exception("Malformed receipt was accepted."); }
            catch (MessagingApiException error) { Assert(error.Code == "invalid_receipt" && command.RequestId == originalId, "Uncertain receipt lost the retry identity."); }
        }
    });
    await Test("Quiet SSE comments renew the byte watchdog", async () =>
    {
        api.StreamIdleTimeout = TimeSpan.FromMilliseconds(150);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var frame in api.ReadStreamAsync("11111111-1111-4111-8111-111111111111", null, timeout.Token))
        {
            Assert(frame.Event == "message.created", "Expected durable frame after comments.");
            return;
        }
        throw new Exception("No event after heartbeats.");
    });
    await Test("A stream without bytes cancels at its finite idle bound", async () =>
    {
        api.StreamIdleTimeout = TimeSpan.FromMilliseconds(120);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await foreach (var frame in api.ReadStreamAsync("22222222-2222-4222-8222-222222222222", null)) { }
            throw new Exception("Silent stream did not time out.");
        }
        catch (OperationCanceledException) { Assert(timer.Elapsed < TimeSpan.FromSeconds(2), "Idle cancellation exceeded the bound."); }
    });
    await Test("Consumer cursor advances only after successfully awaited state application", async () =>
    {
        api.StreamIdleTimeout = TimeSpan.FromSeconds(5);
        const string beginning = "00000000-0000-4000-8000-000000000000:0";
        var consumer = new SubscriptionConsumer(api, "33333333-3333-4333-8333-333333333333", beginning);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var apply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var running = consumer.RunAsync(async (_, _) => { entered.SetResult(); await apply.Task; }, cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(consumer.Cursor == beginning, "Cursor advanced before apply completed.");
        apply.SetResult();
        while (consumer.Cursor == beginning) await Task.Delay(5, cancellation.Token);
        Assert(consumer.Cursor.EndsWith(":1", StringComparison.Ordinal), "Cursor did not advance after apply.");
        cancellation.Cancel();
        try { await running; } catch (OperationCanceledException) { }
    });
    await Test("ID-less store-change control requests resync without advancing a durable cursor", async () =>
    {
        const string beginning = "00000000-0000-4000-8000-000000000000:0";
        var consumer = new SubscriptionConsumer(api, "66666666-6666-4666-8666-666666666666", beginning);
        try { await consumer.RunAsync((_, _) => throw new Exception("Control was incorrectly delivered as a domain event.")); throw new Exception("No resync error."); }
        catch (MessagingApiException e) { Assert(e.Code == "store_changed" && consumer.Cursor == beginning, "Control changed cursor or wrong error."); }
    });
    await Test("ID-less temporary storage controls reconnect without advancing the cursor", async () =>
    {
        const string beginning = "00000000-0000-4000-8000-000000000000:0";
        var consumer = new SubscriptionConsumer(api, "55555555-5555-4555-8555-555555555555", beginning);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var applied = false;
        var task = consumer.RunAsync((_, _) =>
        {
            Assert(consumer.Cursor == beginning && temporaryStreams >= 2, "Temporary control changed a durable cursor or did not reconnect.");
            applied = true; return Task.CompletedTask;
        }, cancellationToken: cancellation.Token);
        while (!consumer.Cursor.EndsWith(":1", StringComparison.Ordinal)) await Task.Delay(5, cancellation.Token);
        Assert(applied, "Reconnected stream did not apply the durable event.");
        cancellation.Cancel();
        try { await task; } catch (OperationCanceledException) { }
    });
    await Test("Duplicate and out-of-order SSE frames never reapply or rewind a consumer", async () =>
    {
        var consumer = new SubscriptionConsumer(api, "77777777-7777-4777-8777-777777777777", "00000000-0000-4000-8000-000000000000:0");
        var count = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = consumer.RunAsync((_, _) => { count++; return Task.CompletedTask; }, cancellationToken: cancellation.Token);
        while (!consumer.Cursor.EndsWith(":2", StringComparison.Ordinal)) await Task.Delay(5, cancellation.Token);
        Assert(count == 1, "Duplicate or older message reapplied to client state.");
        cancellation.Cancel();
        try { await task; } catch (OperationCanceledException) { }
    });
    await Test("Checkpoint and domain envelope disagreement stop without advancing", async () =>
    {
        foreach (var id in new[] { "88888888-8888-4888-8888-888888888888", "44444444-4444-4444-8444-444444444444" })
        {
            const string beginning = "00000000-0000-4000-8000-000000000000:0";
            var consumer = new SubscriptionConsumer(api, id, beginning);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await consumer.RunAsync((_, _) => throw new Exception("Malformed frame was applied."), cancellationToken: cancellation.Token); throw new Exception("Malformed cursor accepted."); }
            catch (MessagingApiException error) { Assert(error.Code == "invalid_stream" && consumer.Cursor == beginning, "Malformed frame advanced cursor."); }
        }
    });
    await Test("Cross-epoch durable frames request resynchronization before applying", async () =>
    {
        const string beginning = "00000000-0000-4000-8000-000000000000:0";
        var consumer = new SubscriptionConsumer(api, "99999999-9999-4999-8999-999999999999", beginning);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await consumer.RunAsync((_, _) => throw new Exception("Cross-epoch frame was applied."), cancellationToken: cancellation.Token); throw new Exception("Cross-epoch frame accepted."); }
        catch (MessagingApiException error) { Assert(error.RequiresResynchronization && consumer.Cursor == beginning, "Wrong epoch recovery or cursor changed."); }
    });
    await Test("Local image copy rejects files above the upload bound and owns admitted bytes", async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), "local-messaging-image-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });
            var bytes = await ImageFileReader.ReadAsync(path);
            await File.WriteAllBytesAsync(path, new byte[] { 4, 5, 6 });
            Assert(bytes.SequenceEqual(new byte[] { 1, 2, 3 }), "Image read did not own the selected bytes.");
            using (var file = File.OpenWrite(path)) file.SetLength(ImageFileReader.MaximumBytes + 1L);
            try { await ImageFileReader.ReadAsync(path); throw new Exception("Oversized upload admitted."); }
            catch (InvalidDataException) { }
        }
        finally { File.Delete(path); }
    });

}
finally { await app.StopAsync(); await app.DisposeAsync(); }

var output = new { suite = "LocalMessaging.ClientTests", platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    scope = "Portable parser/client checks with an explicitly identified HTTP fixture, not Windows product acceptance", passed = results.Count - failures, failed = failures, results };
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(output, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
return failures == 0 ? 0 : 1;
