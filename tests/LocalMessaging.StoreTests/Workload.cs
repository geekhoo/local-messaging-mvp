using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalMessaging.Contracts;

static class Workload
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 0 && !(args.Length == 2 && args[0] == "--output"))
        {
            Console.Error.WriteLine("Usage: LocalMessaging.StoreTests --workload [--output <report.json>]");
            return 2;
        }
        var output = args.Length == 2 ? Path.GetFullPath(args[1]) : null;
        var clock = Stopwatch.StartNew();
        var phases = new Dictionary<string, double>();
        try
        {
            await using var f = await Fixture.Create();
            var channels = new List<(string Channel, string InitialSession)> { (f.Channel, f.Session) };
            for (var i = 1; i < 100; i++)
            {
                var receipt = await f.Send(Fixture.Envelope(f.A, new JsonObject { ["type"] = "channel", ["name"] = $"Channel {i:000}", ["initialSessionTitle"] = "Session 0" }));
                channels.Add((receipt["dataObjects"]!["channel"]!["id"]!.GetValue<string>(), receipt["dataObjects"]!["session"]!["id"]!.GetValue<string>()));
            }
            var sessions = new List<(string Channel, string Session)>();
            foreach (var channel in channels)
            {
                sessions.Add((channel.Channel, channel.InitialSession));
                for (var i = 1; i < 10; i++)
                {
                    var receipt = await f.Send(Fixture.Envelope(i % 2 == 0 ? f.A : f.B, new JsonObject
                    {
                        ["type"] = "session", ["channel"] = Fixture.Existing(channel.Channel), ["title"] = $"Session {i}"
                    }));
                    sessions.Add((channel.Channel, receipt["dataObjects"]!["session"]!["id"]!.GetValue<string>()));
                }
            }
            phases["seedSeconds"] = clock.Elapsed.TotalSeconds;
            Console.WriteLine("WORKLOAD seeded 100 channels / 1000 sessions.");
            var sub = Fixture.SubscriptionId(await f.Store.CreateSubscriptionAsync(Fixture.Element(f.Subscription(new JsonObject { ["allMessages"] = true }, "now"))));
            var start = (await f.Store.GetSubscriptionAsync(sub))["startCursor"]!.GetValue<string>();
            var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var consumerTasks = Enumerable.Range(0, 20).Select(index => Task.Run(async () =>
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var cursor = start;
                var polls = 0;
                var duplicateApplicationsAvoided = 0;
                var replayed = false;
                string? previousCursor = null;
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    var page = await f.Store.ReadEventsAsync(sub, cursor, 200, timeout.Token);
                    polls++;
                    foreach (var item in page["items"]!.AsArray())
                    {
                        var id = item!["dataObject"]!["id"]!.GetValue<string>();
                        if (!seen.Add(id)) duplicateApplicationsAvoided++;
                    }
                    previousCursor ??= cursor;
                    cursor = page["nextCursor"]!.GetValue<string>();
                    if (!replayed && seen.Count >= 300)
                    {
                        cursor = previousCursor; replayed = true; // Deliberate reconnect from an older safe cursor.
                    }
                    else previousCursor = cursor;
                    // A page's head was captured before its reads. Publication may finish while
                    // that older page is in flight, so only the final publisher head can end replay.
                    if (done.Task.IsCompletedSuccessfully && cursor == done.Task.Result) break;
                    if (page["items"]!.AsArray().Count == 0) await Task.Delay(25, timeout.Token);
                }
                return new { index, count = seen.Count, cursor, polls, duplicateApplicationsAvoided, ids = seen };
            }, timeout.Token)).ToArray();

            var latencies = new ConcurrentBag<double>();
            var expected = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            var published = 0;
            var publishStart = clock.Elapsed.TotalSeconds;
            await Parallel.ForEachAsync(Enumerable.Range(0, 20_000), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = timeout.Token }, async (i, ct) =>
            {
                var target = sessions[i % sessions.Count];
                var request = Fixture.Envelope(i % 2 == 0 ? f.A : f.B, new JsonObject
                {
                    ["type"] = "message", ["channel"] = Fixture.Existing(target.Channel), ["session"] = Fixture.Existing(target.Session),
                    ["content"] = new JsonObject { ["type"] = "text", ["text"] = $"Message {i:00000}: deterministic workload {(i % 10 == 0 ? "@bryan" : "public")} payload." }
                }, DeterministicId(i + 1));
                var timer = Stopwatch.StartNew();
                var receipt = await f.Store.ExecuteAsync(Fixture.Element(request), cancellationToken: ct);
                latencies.Add(timer.Elapsed.TotalMilliseconds);
                expected.TryAdd(Fixture.MessageId(receipt), 0);
                if (i % 1000 == 0)
                {
                    var retry = await f.Store.ExecuteAsync(Fixture.Element(request), cancellationToken: ct);
                    if (!retry["replayed"]!.GetValue<bool>() || Fixture.MessageId(retry) != Fixture.MessageId(receipt))
                        throw new Exception("Idempotent workload retry changed the committed result.");
                }
                var count = Interlocked.Increment(ref published);
                if (count % 1000 == 0) Console.WriteLine($"WORKLOAD published {count}/20000 text messages; elapsed {clock.Elapsed.TotalSeconds:F1}s.");
            });
            var image = CreatePngFixture();
            for (var i = 0; i < 100; i++)
            {
                var target = sessions[i * 10];
                var request = Fixture.Envelope(f.A, new JsonObject
                {
                    ["type"] = "message", ["channel"] = Fixture.Existing(target.Channel), ["session"] = Fixture.Existing(target.Session),
                    ["content"] = new JsonObject { ["type"] = "image", ["caption"] = $"Image {i:000} @bryan" }
                }, DeterministicId(30_000 + i));
                var receipt = await f.Store.ExecuteAsync(Fixture.Element(request), image, timeout.Token);
                expected.TryAdd(Fixture.MessageId(receipt), 0);
            }
            phases["publishSeconds"] = clock.Elapsed.TotalSeconds - publishStart;
            done.SetResult((await f.Store.GetStatusAsync())["headCursor"]!.GetValue<string>());
            Console.WriteLine("WORKLOAD finished publication; reconciling 20 independent consumers.");
            var consumers = await Task.WhenAll(consumerTasks);
            if (expected.Count != 20_100) throw new Exception("Committed message count mismatch.");
            foreach (var consumer in consumers)
                if (!consumer.ids.SetEquals(expected.Keys))
                    throw new Exception($"Consumer {consumer.index} reconciled {consumer.count}/{expected.Count} committed messages; missing {expected.Keys.Except(consumer.ids).Count()}, unexpected {consumer.ids.Except(expected.Keys).Count()}.");
            if (await f.Scalar("SELECT count(*) FROM Channels;") != "100" || await f.Scalar("SELECT count(*) FROM Sessions;") != "1000")
                throw new Exception("Workload container count mismatch.");
            if (await f.Scalar("SELECT count(*) FROM Messages WHERE Kind='text';") != "20000" || await f.Scalar("SELECT count(*) FROM ImageAssets;") != "100")
                throw new Exception("Workload content count mismatch.");
            await using (var connection = f.Connection())
            {
                await connection.OpenAsync(timeout.Token);
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await using var reader = await command.ExecuteReaderAsync(timeout.Token);
                if (!await reader.ReadAsync(timeout.Token) || reader.GetInt32(0) != 0) throw new Exception("WAL checkpoint remained pinned after finite consumers finished.");
            }
            var ordered = latencies.Order().ToArray();
            var process = Process.GetCurrentProcess(); process.Refresh();
            var finalHead = (await f.Store.GetStatusAsync())["headCursor"]!.GetValue<string>();
            if (consumers.Any(c => c.cursor != finalHead)) throw new Exception("Consumer cursor did not reach the final committed head.");
            var report = new
            {
                suite = "LocalMessaging.StoreWorkload", result = "passed", executedAtUtc = DateTime.UtcNow.ToString("O"),
                scope = "Real EF/SQLite application workload with finite event consumers. Does not qualify HTTP streaming or WIC decoding; the PNG fixture is generated/validated structurally by the harness and passed as ValidatedImage.",
                dataset = new { users = 3, channels = 100, sessions = 1000, textMessages = 20_000, imageMessages = 100, imageWidth = 256, imageHeight = 256, imageBytesEach = image.Bytes.Length },
                environment = new { os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, processors = Environment.ProcessorCount },
                effectiveLimits = new ProductLimits(), elapsedSeconds = clock.Elapsed.TotalSeconds, phases,
                measured = new
                {
                    publishLatencyMs = new { p50 = ordered[ordered.Length / 2], p95 = ordered[(int)(ordered.Length * .95)], p99 = ordered[(int)(ordered.Length * .99)], maximum = ordered[^1] },
                    processWorkingSetBytes = process.WorkingSet64, peakWorkingSetBytes = process.PeakWorkingSet64,
                    databaseBytes = new FileInfo(f.Store.DatabasePath).Length,
                    walBytes = File.Exists(f.Store.DatabasePath + "-wal") ? new FileInfo(f.Store.DatabasePath + "-wal").Length : 0
                },
                consumers = consumers.Select(c => new { c.index, c.count, c.cursor, c.polls, c.duplicateApplicationsAvoided }),
                assertions = new[] { "100 channels / 1000 sessions", "20000 text / 100 images", "20 independent consumers reconcile all committed messages", "Duplicate retry creates no new message", "Replay duplicate applications avoided", "All cursors reach final head", "WAL checkpoint completes" }
            };
            if (output is not null)
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
            Console.WriteLine($"WORKLOAD PASS: 20100 messages reconciled by 20 consumers in {clock.Elapsed.TotalSeconds:F1}s.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("WORKLOAD FAIL: " + ex);
            if (output is not null)
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { suite = "LocalMessaging.StoreWorkload", result = "failed", elapsedSeconds = clock.Elapsed.TotalSeconds, error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            }
            return 1;
        }
    }

    private static Guid DeterministicId(int index) => Guid.Parse($"10000000-0000-4000-8000-{index:000000000000}");

    // A deterministic, fully framed PNG corpus image, not a user-content transformation.
    internal static ValidatedImage CreatePngFixture(int side = 256)
    {
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), side);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), side);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk(png, "IHDR", ihdr);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var random = new Random(20261120);
            var row = new byte[side * 4 + 1];
            for (var y = 0; y < side; y++)
            {
                random.NextBytes(row.AsSpan(1)); row[0] = 0;
                for (var x = 0; x < side; x++) row[1 + x * 4 + 3] = 255;
                zlib.Write(row);
            }
        }
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        var bytes = png.ToArray();
        return new ValidatedImage(bytes, "image/png", side, side, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static void Chunk(Stream target, string name, byte[] payload)
    {
        Span<byte> number = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(number, payload.Length); target.Write(number);
        var type = Encoding.ASCII.GetBytes(name); target.Write(type); target.Write(payload);
        uint crc = uint.MaxValue;
        foreach (var b in type.Concat(payload))
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); target.Write(number);
    }
}
