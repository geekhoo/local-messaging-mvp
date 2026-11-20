using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalMessaging.Client;
using LocalMessaging.Contracts;
using Microsoft.Win32;
using EndpointDescriptor = LocalMessaging.Client.EndpointDescriptor;
using HostSettings = LocalMessaging.Host.HostOptions;

public static partial class ScenarioProgram
{
    // This path launches only files checked against the product's manifest. It never uses dotnet,
    // a source checkout, an in-process host, or a command path supplied by endpoint metadata.
    private static async Task<int> RunPublishedAsync(string product, string evidence)
    {
        _suite = "windows";
        _publishedMode = true;
        var productPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(product)) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(evidence).StartsWith(productPrefix, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Qualification evidence must be outside the immutable product directory.");
            return 2;
        }
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            || RuntimeInformation.OSArchitecture != Architecture.X64 || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            await Report(evidence, "blocked", ["Published-product qualification requires a Windows 11 x64 client OS and an x64 qualification executable."]);
            return 3;
        }
        var children = new List<PublishedChild>();
        var temporary = Path.Combine(Path.GetTempPath(), "LocalMessaging-published-" + Guid.NewGuid().ToString("N"));
        var state = Path.Combine(temporary, "state");
        var beforeCertificates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hostLogFiles = new List<string>();
        JsonObject? binding = null;
        var outcome = "failed";
        var limitations = new List<string>();
        try
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions"))
            {
                if (!string.Equals(key?.GetValue("ProductType") as string, "WinNT", StringComparison.Ordinal))
                    throw new PlatformNotSupportedException("A Windows 11 client OS (ProductType WinNT) is required; Windows Server is outside this qualification target.");
            }
            binding = await VerifyProductManifestAsync(product);
            beforeCertificates = ExistingCertificateThumbprints();
            Directory.CreateDirectory(temporary);
            var frontend = FreePort();
            var messaging = FreePort();
            while (messaging == frontend) messaging = FreePort();
            var options = new HostSettings { StateDirectory = state, FrontendPort = frontend, MessagingPort = messaging,
                Limits = new ProductLimits { HeartbeatSeconds = 1, ClientIdleSeconds = 4, StreamWriteSeconds = 2, ActiveStreams = 4 } };
            binding["qualificationSettings"] = new JsonObject { ["stateDirectory"] = state,
                ["frontendBaseUrl"] = options.FrontendBaseUrl, ["messagingBaseUrl"] = options.MessagingBaseUrl,
                ["limits"] = JsonSerializer.SerializeToNode(options.Limits, Wire.ResponseOptions),
                ["productDefaultLimits"] = JsonSerializer.SerializeToNode(new ProductLimits(), Wire.ResponseOptions),
                ["source"] = "Isolated qualification --config; effective limits are checked against each running host status." };
            var configuration = Path.Combine(temporary, "qualification-config.json");
            await File.WriteAllTextAsync(configuration, JsonSerializer.Serialize(options, Wire.RequestOptions));
            var executable = Path.Combine(product, "host", "LocalMessaging.Host.exe");
            PublishedChild Launch(string label, params string[] extra)
            {
                var child = new PublishedChild(executable, label, ["--config", configuration, .. extra]);
                children.Add(child);
                return child;
            }
            async Task<(PublishedChild Child, EndpointDescriptor Descriptor)> Start(string label)
            {
                var child = Launch(label);
                var descriptor = await WaitPublishedReadyAsync(child, options);
                await CheckPublishedLogsAsync(state, "startup_" + label);
                return (child, descriptor);
            }

            var initial = await Start("transport");
            ContractChecks();
            await TransportChecks(initial.Descriptor.InstanceId, options, initial.Descriptor.CertificateSha256);
            Check(await initial.Child.WaitAsync() == 0, "published.controlled_stop_exits_zero");
            await CheckPublishedLogsAsync(state, "transport_completed");
            await RegistryChecks();
            var logs = Directory.GetFiles(Path.Combine(state, "logs"), "host.jsonl*");
            Check(logs.Length is > 0 and <= 4 && logs.All(p => new FileInfo(p).Length <= 1024 * 1024 + 8192), "published.host_logs_bounded");
            Check(logs.All(p => !File.ReadAllText(p).Contains("<script>alert(1)</script>", StringComparison.Ordinal)), "published.host_logs_exclude_message_body");

            var reopened = await Start("reopen");
            Check(reopened.Descriptor.InstanceId != initial.Descriptor.InstanceId && reopened.Descriptor.StoreEpoch == initial.Descriptor.StoreEpoch,
                "published.restart_preserves_store_epoch");
            using var api = new MessagingClient(new Uri(options.FrontendBaseUrl));
            var alice = (await api.GetAsync("users?handlePrefix=alice"))["items"]!.AsArray().Single()!["id"]!.GetValue<string>();
            var definitions = (await api.GetAsync("subscriptions?limit=100&ownerUserId=" + alice))["items"]!.AsArray();
            Check(definitions.Count >= 3 && definitions.All(x => x!["activeConnectionCount"]!.GetValue<int>() == 0)
                && definitions.Any(x => x!["state"]!.GetValue<string>() == "active"), "published.saved_subscriptions_survive_without_connections");

            var user = await api.SendAsync(FrozenCommand.Create(null, new JsonObject { ["type"] = "user", ["handle"] = "package_qualifier", ["displayName"] = "Package qualifier" }));
            var owner = Id(user, "user");
            var channel = await api.SendAsync(FrozenCommand.Create(Guid.Parse(owner), new JsonObject { ["type"] = "channel", ["name"] = "Package lifecycle" }));
            var channelId = Id(channel, "channel");
            var sessionId = Id(channel, "session");
            var imageBytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "sample.png"));
            var imageData = Message(owner, channelId, sessionId, "unused")["dataObject"]!.AsObject();
            imageData["content"] = new JsonObject { ["type"] = "image", ["caption"] = "Retained through offline restore" };
            var imageReceipt = await api.SendAsync(FrozenCommand.Create(Guid.Parse(owner), imageData, imageBytes));
            var imageId = Id(imageReceipt, "message");
            var imagePath = imageReceipt["dataObjects"]!["message"]!["content"]!["image"]!["contentPath"]!.GetValue<string>();
            var head = (await api.GetAsync("status"))["headCursor"]!.GetValue<string>();
            var saved = await api.CreateSubscriptionAsync(Guid.Parse(owner), head);
            var subscriptionId = saved["id"]!.GetValue<string>();
            var frozen = FrozenCommand.Operation("requests", Message(owner, channelId, sessionId, "Committed before the sender read its response"));
            JsonObject committed;
            // Leave the actual sender's response unread; a separate receipt query observes commit.
            // Killing the actual host after that point tests durable recovery without a public test hook.
            using (var sender = new TcpClient())
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                await sender.ConnectAsync(IPAddress.Loopback, frontend, deadline.Token);
                var payload = Encoding.UTF8.GetBytes(frozen.GetEnvelope().ToJsonString());
                var header = Encoding.ASCII.GetBytes($"POST /api/v1/requests HTTP/1.1\r\nHost: 127.0.0.1:{frontend}\r\n{Wire.ClientHeader}: {Wire.ClientHeaderValue}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await sender.GetStream().WriteAsync(header, deadline.Token);
                await sender.GetStream().WriteAsync(payload, deadline.Token);
                committed = await WaitPublishedReceiptAsync(api, frozen.RequestId, deadline.Token);
                await CheckPublishedLogsAsync(state, "before_process_kill");
                await reopened.Child.KillAsync();
            }
            var recovered = await Start("crash-recovery");
            Check(recovered.Descriptor.InstanceId != reopened.Descriptor.InstanceId && recovered.Descriptor.StoreEpoch == reopened.Descriptor.StoreEpoch,
                "published.process_crash_preserves_epoch");
            var retried = await api.SendWithRecoveryAsync(frozen);
            var committedId = Id(committed, "message");
            var messages = (await api.GetAsync("sessions/" + sessionId + "/messages?view=flat&limit=100"))["items"]!.AsArray();
            var events = (await api.GetAsync("subscriptions/" + subscriptionId + "/events"))["items"]!.AsArray();
            Check(retried["replayed"]!.GetValue<bool>() && Id(retried, "message") == committedId
                && messages.Count(x => x!["id"]!.GetValue<string>() == committedId) == 1
                && events.Count(x => x!["dataObject"]!["id"]!.GetValue<string>() == committedId) == 1,
                "published.unread_response_crash_retry_one_message_event");
            Check((await api.GetAsync("subscriptions/" + subscriptionId))["activeConnectionCount"]!.GetValue<int>() == 0,
                "published.crash_clears_connections_keeps_definition");
            var forbiddenBackup = Path.Combine(temporary, "must-not-back-up-live.db");
            Check(await Launch("live-backup-rejected", "--backup", forbiddenBackup).WaitAsync() != 0 && !File.Exists(forbiddenBackup),
                "published.backup_requires_stopped_host");
            await StopPublishedAsync(api.BaseAddress, recovered.Child);
            await CheckPublishedLogsAsync(state, "recovery_completed");
            var backup = Path.Combine(temporary, "offline-backup.db");
            Check(await Launch("backup", "--backup", backup).WaitAsync() == 0 && new FileInfo(backup).Length > 0, "published.offline_backup_cli");

            var changed = await Start("post-backup-write");
            var later = await api.SendAsync(FrozenCommand.Operation("requests", Message(owner, channelId, sessionId, "Only in the safety backup")));
            var laterId = Id(later, "message");
            await StopPublishedAsync(api.BaseAddress, changed.Child);
            await CheckPublishedLogsAsync(state, "before_offline_restore");
            Check(await Launch("restore", "--restore", backup).WaitAsync() == 0, "published.offline_restore_cli");
            Check(Directory.GetFiles(options.EffectiveDataDirectory, "messaging.safety-restore-*.db").Length == 1,
                "published.restore_keeps_safety_backup");
            var restored = await Start("restored");
            Check(restored.Descriptor.StoreEpoch != recovered.Descriptor.StoreEpoch, "published.restore_rotates_epoch");
            Check((await api.GetAsync("channels/" + channelId))["ownerUserId"]!.GetValue<string>() == owner
                && (await api.GetAsync("sessions/" + sessionId))["ownerUserId"]!.GetValue<string>() == owner
                && (await api.GetAsync("users/" + owner))["handle"]!.GetValue<string>() == "package_qualifier", "published.restore_preserves_domain_ownership");
            Check((await api.GetAsync("messages/" + imageId))["content"]!["type"]!.GetValue<string>() == "image"
                && (await api.GetImageAsync(imagePath)).SequenceEqual(imageBytes), "published.restore_preserves_image_blob");
            var restoredReceipt = await api.GetAsync("requests/" + frozen.RequestId);
            Check(Id(restoredReceipt, "message") == committedId && restoredReceipt["storeEpoch"]!.GetValue<string>() == restored.Descriptor.StoreEpoch.ToString(),
                "published.restore_preserves_receipt_rebases_epoch");
            var restoredSubscriptions = (await api.GetAsync("subscriptions?limit=100&ownerUserId=" + owner))["items"]!.AsArray();
            var restoredAliceSubscriptions = (await api.GetAsync("subscriptions?limit=100&ownerUserId=" + alice))["items"]!.AsArray();
            Check(restoredSubscriptions.Count == 1 && restoredSubscriptions.All(x => x!["state"]!.GetValue<string>() == "closed"
                && x["activeConnectionCount"]!.GetValue<int>() == 0) && restoredAliceSubscriptions.Count == definitions.Count
                && restoredAliceSubscriptions.All(x => x!["state"]!.GetValue<string>() == "closed" && x["activeConnectionCount"]!.GetValue<int>() == 0),
                "published.restore_closes_saved_subscriptions");
            await ExpectPublishedFailureAsync(api, "messages/" + laterId, 404, "message_not_found", "published.restore_excludes_later_domain_write");
            await ExpectPublishedFailureAsync(api, "requests/" + later["requestId"]!.GetValue<string>(), 404, "request_not_committed", "published.restore_excludes_later_receipt");
            await ExpectPublishedFailureAsync(api, "channels?asOf=" + Uri.EscapeDataString(head), 409, "store_changed", "published.restore_rejects_old_cursor");
            Check((await api.SendAsync(frozen))["replayed"]!.GetValue<bool>(), "published.restore_retry_still_idempotent");
            await StopPublishedAsync(api.BaseAddress, restored.Child);
            await CheckPublishedLogsAsync(state, "restore_completed");

            var invalidState = Path.Combine(temporary, "missing-certificate");
            var currentCertificates = ExistingCertificateThumbprints();
            var missingThumbprint = Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
            while (currentCertificates.Contains(missingThumbprint)) missingThumbprint = Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
            var invalid = Launch("missing-certificate", "--state-dir", invalidState, "--certificate-thumbprint", missingThumbprint);
            Check(await invalid.WaitAsync() != 0 && !File.Exists(Path.Combine(invalidState, "endpoint.json"))
                && invalid.StandardError.Contains("certificate_invalid", StringComparison.Ordinal), "published.invalid_configured_certificate_fails_without_readiness");
            var unchanged = await VerifyProductManifestAsync(product, "_after_run");
            Check(binding["manifestSha256"]!.GetValue<string>() == unchanged["manifestSha256"]!.GetValue<string>(), "published.product_unchanged_by_qualification");
            outcome = "passed";
            limitations.Add("This suite qualifies the published host process and maintenance commands. Actual WinUI controls, browser interaction, proxy/PAC configuration and clean-machine installation remain separately evidenced gates.");
        }
        catch (PlatformNotSupportedException error)
        {
            outcome = "blocked";
            limitations.Add(error.Message);
        }
        catch (Exception error)
        {
            limitations.Add(error.GetType().Name + ": " + error.Message);
            Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
        }
        finally
        {
            foreach (var child in children)
            {
                try { await child.DisposeAsync(); }
                catch (Exception error) { outcome = "failed"; limitations.Add("Child cleanup: " + error.Message); }
                try { await child.SaveLogsAsync(evidence); }
                catch (Exception error) { outcome = "failed"; limitations.Add("Child log capture: " + error.Message); }
            }
            try
            {
                var logDirectory = Path.Combine(state, "logs");
                if (Directory.Exists(logDirectory))
                {
                    var evidenceDirectory = Path.GetDirectoryName(evidence)!;
                    Directory.CreateDirectory(evidenceDirectory);
                    foreach (var path in Directory.GetFiles(logDirectory, "host.jsonl*").Take(4))
                    {
                        if (new FileInfo(path).Length > 1024 * 1024 + 8192) throw new InvalidDataException("Refusing an unbounded host-log evidence copy.");
                        var name = Path.GetFileNameWithoutExtension(evidence) + ".host." + Path.GetFileName(path);
                        File.Copy(path, Path.Combine(evidenceDirectory, name), overwrite: true);
                        hostLogFiles.Add(name);
                    }
                }
            }
            catch (Exception error) { outcome = "failed"; limitations.Add("Host log evidence capture: " + error.Message); }
            try
            {
                RemoveCreatedCertificate(state, beforeCertificates);
                if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
            }
            catch (Exception error) { outcome = "failed"; limitations.Add("Isolated fixture cleanup: " + error.Message); }
        }
        await Report(evidence, outcome, limitations.ToArray());
        var report = JsonNode.Parse(await File.ReadAllTextAsync(evidence))!.AsObject();
        report["product"] = binding;
        report["childLogs"] = new JsonArray(children.Select(c => (JsonNode?)JsonValue.Create(c.Label)).ToArray());
        report["hostLogs"] = new JsonArray(hostLogFiles.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
        await File.WriteAllTextAsync(evidence, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return outcome == "passed" ? 0 : outcome == "blocked" ? 3 : 1;
    }

    private static async Task<JsonObject> VerifyProductManifestAsync(string product, string checkSuffix = "")
    {
        var manifest = Path.Combine(product, "manifest.json");
        if (!Directory.Exists(product) || (File.GetAttributes(product) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Use an actual product directory without reparse points.");
        if (!File.Exists(manifest) || new FileInfo(manifest).Length > 16 * 1024 * 1024) throw new InvalidDataException("Missing or oversized product manifest.");
        var bytes = await File.ReadAllBytesAsync(manifest);
        var document = JsonNode.Parse(bytes)!.AsObject();
        var commit = document["candidateCommit"]?.GetValue<string>() ?? "";
        Check(document["schemaVersion"]?.GetValue<int>() == 1 && commit.Length is 40 or 64 && commit.All(Uri.IsHexDigit)
            && document["configuration"]?.GetValue<string>() == "Release" && document["runtimeIdentifier"]?.GetValue<string>() == "win-x64"
            && document["selfContained"]?.GetValue<bool>() == true, "published.manifest_candidate_release_win_x64_self_contained" + checkSuffix);
        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Directory.EnumerateFileSystemEntries(product, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Product paths cannot contain reparse points.");
            if ((attributes & FileAttributes.Directory) == 0) actual.Add(Path.GetRelativePath(product, entry).Replace('\\', '/'));
        }
        actual.Remove("manifest.json");
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document["files"]!.AsArray())
        {
            var path = entry!["path"]!.GetValue<string>();
            if (path.Length == 0 || path.Contains('\\') || path.Contains(':') || path.StartsWith('/') || path.Split('/').Any(p => p is "" or "." or "..") || !expected.Add(path))
                throw new InvalidDataException("Unsafe or duplicate product manifest path.");
            var absolute = Path.Combine(product, path.Replace('/', Path.DirectorySeparatorChar));
            if (!actual.Contains(path) || new FileInfo(absolute).Length != entry["bytes"]!.GetValue<long>()) throw new InvalidDataException("Missing or wrong-sized product file: " + path);
            await using var content = File.OpenRead(absolute);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(content));
            if (!string.Equals(digest, entry["sha256"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Product hash mismatch: " + path);
        }
        Check(expected.SetEquals(actual) && expected.Contains("host/LocalMessaging.Host.exe") && expected.Contains("winui/LocalMessaging.WinUI.exe")
            && expected.Contains("host/coreclr.dll") && expected.Contains("winui/coreclr.dll"), "published.all_product_files_hash_verified" + checkSuffix);
        return new JsonObject { ["candidateCommit"] = commit, ["manifestSha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            ["configuration"] = "Release", ["runtimeIdentifier"] = "win-x64", ["selfContained"] = true, ["fileCount"] = expected.Count };
    }

    private static async Task<EndpointDescriptor> WaitPublishedReadyAsync(PublishedChild child, HostSettings options)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var client = new MessagingClient(new Uri(options.FrontendBaseUrl)) { RequestTimeout = TimeSpan.FromSeconds(2) };
        while (!timeout.IsCancellationRequested)
        {
            if (child.HasExited) throw new InvalidOperationException("Published host exited before readiness: " + child.Label);
            try
            {
                var descriptor = await EndpointDescriptor.ReadAsync(options.DescriptorPath, timeout.Token);
                if (descriptor.ProcessId == child.Id && descriptor.FrontendBaseUrl == options.FrontendBaseUrl && descriptor.MessagingBaseUrl == options.MessagingBaseUrl)
                {
                    var status = await client.GetAsync("status", timeout.Token);
                    if (status["ready"]?.GetValue<bool>() == true && status["instanceId"]?.GetValue<string>() == descriptor.InstanceId.ToString()
                        && status["storeEpoch"]?.GetValue<string>() == descriptor.StoreEpoch.ToString())
                    {
                        Check(JsonNode.DeepEquals(status["limits"], JsonSerializer.SerializeToNode(options.Limits, Wire.ResponseOptions)), "published.effective_limits_" + child.Label);
                        Check(true, "published.readiness_process_instance_" + child.Label);
                        return descriptor;
                    }
                }
            }
            catch (Exception error) when (error is IOException or HttpRequestException or JsonException or InvalidDataException or OperationCanceledException or MessagingApiException { IsTemporary: true }) { }
            await Task.Delay(100, timeout.Token);
        }
        throw new TimeoutException("Published host did not produce verified readiness within 45 seconds.");
    }

    private static async Task<JsonObject> WaitPublishedReceiptAsync(MessagingClient client, Guid id, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return await client.GetAsync("requests/" + id, token); }
            catch (MessagingApiException error) when (error.Status == 404) { await Task.Delay(25, token); }
        }
    }
    private static async Task StopPublishedAsync(Uri address, PublishedChild child)
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Add(Wire.ClientHeader, Wire.ClientHeaderValue);
        using var response = await http.PostAsync("host/stop", new StringContent("{}", Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        Check(await child.WaitAsync() == 0, "published.clean_process_exit_" + child.Label);
    }
    private static async Task ExpectPublishedFailureAsync(MessagingClient client, string path, int status, string code, string name)
    {
        try { await client.GetAsync(path); throw new InvalidOperationException("Expected failure: " + name); }
        catch (MessagingApiException error) { Check(error.Status == status && error.Code == code, name); }
    }

    private static async Task CheckPublishedLogsAsync(string state, string phase)
    {
        // Readers share writes/deletes so inspection cannot make the host's append/rotation fail.
        // A partially observed append is retried, never silently discarded before kill/restore.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var paths = Directory.GetFiles(Path.Combine(state, "logs"), "host.jsonl*");
                if (paths.Length is < 1 or > 4) throw new InvalidDataException("Expected one to four bounded host logs.");
                foreach (var path in paths)
                {
                    await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (file.Length > 1024 * 1024 + 8192) throw new InvalidDataException("Host log exceeds its bound.");
                    using var reader = new StreamReader(file, new UTF8Encoding(false, true));
                    var text = await reader.ReadToEndAsync();
                    if (text.Length != 0 && !text.EndsWith('\n')) throw new IOException("Host log append is incomplete.");
                    foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var entry = JsonNode.Parse(line)!.AsObject();
                        var level = entry["level"]?.GetValue<string>() ?? throw new InvalidDataException("Host log lacks a severity.");
                        if (level is "Warning" or "Error" or "Critical")
                            throw new InvalidOperationException("Unexpected host " + level + " during " + phase + ": " + entry["category"]?.GetValue<string>() + ". Inspect the isolated host logs.");
                    }
                }
                Check(true, "published.no_unexpected_host_diagnostics_" + phase);
                return;
            }
            catch (Exception error) when (attempt < 4 && error is IOException or JsonException or DecoderFallbackException)
            { await Task.Delay(50); }
        }
    }
    [SupportedOSPlatform("windows")]
    private static HashSet<string> ExistingCertificateThumbprints()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var thumbprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var certificate in store.Certificates)
        {
            thumbprints.Add(certificate.Thumbprint);
            certificate.Dispose();
        }
        return thumbprints;
    }
    [SupportedOSPlatform("windows")]
    private static void RemoveCreatedCertificate(string state, HashSet<string> previous)
    {
        var pointer = Path.Combine(state, "certificate.thumbprint");
        if (!File.Exists(pointer)) return;
        var thumbprint = File.ReadAllText(pointer).Trim();
        if (thumbprint.Length != 40 || !thumbprint.All(Uri.IsHexDigit) || previous.Contains(thumbprint)) throw new InvalidDataException("Refusing to remove a certificate without proven isolated-test ownership.");
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        foreach (var certificate in matches) { store.Remove(certificate); certificate.Dispose(); }
    }

    private sealed class PublishedChild : IAsyncDisposable
    {
        private const int CaptureCharacters = 64 * 1024;
        private readonly Process process;
        private readonly StringBuilder output = new(), error = new();
        private readonly Task drain;
        private bool disposed;
        private bool diagnosticsChecked;
        private long errorCharacters;
        public string Label { get; }
        public int Id { get; }
        public bool HasExited => process.HasExited;
        public string StandardError => error.ToString();
        public PublishedChild(string executable, string label, string[] arguments)
        {
            Label = label;
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            process = Process.Start(start) ?? throw new IOException("Could not launch the published host executable.");
            Id = process.Id;
            drain = Task.WhenAll(CaptureAsync(process.StandardOutput, output), CaptureAsync(process.StandardError, error));
        }
        private async Task CaptureAsync(StreamReader source, StringBuilder target)
        {
            var buffer = new char[2048];
            int count;
            while ((count = await source.ReadAsync(buffer)) != 0)
            {
                if (ReferenceEquals(target, error)) errorCharacters += count;
                target.Append(buffer, 0, count);
                if (target.Length > CaptureCharacters) target.Remove(0, target.Length - CaptureCharacters);
            }
        }
        public async Task<int> WaitAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            await drain.WaitAsync(timeout.Token);
            if (!diagnosticsChecked)
            {
                diagnosticsChecked = true;
                var expected = Label switch
                {
                    "live-backup-rejected" => "storage_unavailable: The data directory is already in use or its exclusive lock is unavailable.",
                    "missing-certificate" => "startup_or_maintenance_failed: certificate_invalid: configured current-user certificate was not found uniquely.",
                    _ => ""
                };
                Check(errorCharacters <= CaptureCharacters && error.ToString().Trim() == expected,
                    "published.only_expected_stderr_" + Label);
            }
            return process.ExitCode;
        }
        public async Task KillAsync()
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await WaitAsync();
        }
        public async Task SaveLogsAsync(string evidence)
        {
            var directory = Path.GetDirectoryName(evidence)!;
            Directory.CreateDirectory(directory);
            var prefix = Path.GetFileNameWithoutExtension(evidence) + "." + Label;
            await File.WriteAllTextAsync(Path.Combine(directory, prefix + ".stdout.log"), output.ToString());
            await File.WriteAllTextAsync(Path.Combine(directory, prefix + ".stderr.log"), error.ToString());
        }
        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            try { await KillAsync(); }
            finally { process.Dispose(); disposed = true; }
        }
    }
}
