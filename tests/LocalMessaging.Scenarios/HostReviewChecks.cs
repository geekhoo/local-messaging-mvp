using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LocalMessaging.Contracts;
using HostSettings = LocalMessaging.Host.HostOptions;

/// <summary>Focused regressions for the independent host review. Uses only the isolated scenario state directory.</summary>
internal static class HostReviewChecks
{
    public static async Task RunAsync(HostSettings options, Action<bool, string> check)
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri(options.FrontendBaseUrl), Timeout = TimeSpan.FromSeconds(15)
        };
        http.DefaultRequestHeaders.Add(Wire.ClientHeader, Wire.ClientHeaderValue);
        var logDirectory = Path.Combine(options.StateDirectory, "logs");
        var logFile = Path.Combine(logDirectory, "host.jsonl");
        var token = Guid.NewGuid().ToString("N")[..12];
        var sensitiveName = "Private review payload " + token;

        async Task<(HttpStatusCode Status, JsonObject Body)> Send(JsonObject request)
        {
            using var response = await http.PostAsync("requests", new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));
            return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject());
        }

        JsonObject NewUser(string suffix) => new()
        {
            ["contractVersion"] = 1, ["requestId"] = Guid.NewGuid().ToString("D"),
            ["dataObject"] = new JsonObject { ["type"] = "user", ["handle"] = "review_" + token + suffix, ["displayName"] = sensitiveName }
        };

        var original = NewUser("a");
        var first = await Send(original);
        check(first.Status == HttpStatusCode.Created, "review.log_control_command_commits");
        var changed = (JsonObject)original.DeepClone();
        changed["dataObject"]!["displayName"] = sensitiveName + " changed";
        var conflict = await Send(changed);
        check(conflict.Status == HttpStatusCode.Conflict, "review.log_control_conflict");
        var requestId = original["requestId"]!.GetValue<string>();
        await WaitUntilAsync(async () => (await File.ReadAllTextAsync(logFile)).Split('\n').Any(line =>
            line.Contains(requestId, StringComparison.Ordinal) && line.Contains("status 409", StringComparison.Ordinal)));
        check(true, "review.command_log_records_translated_409_and_request_id");

        var savedLogs = Path.Combine(options.StateDirectory, "logs-review-recovery-" + token);
        var previousError = Console.Error;
        using var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        var blockedRequest = NewUser("b");
        var blockedId = blockedRequest["requestId"]!.GetValue<string>();
        var moved = false;
        try
        {
            Directory.Move(logDirectory, savedLogs);
            moved = true;
            await File.WriteAllTextAsync(logDirectory, "Deliberate test-only log path obstruction.");
            Console.SetError(TextWriter.Synchronized(diagnostics));
            var committed = await Send(blockedRequest);
            check(committed.Status == HttpStatusCode.Created, "review.log_io_failure_preserves_201_commit");
            using (var receiptResponse = await http.GetAsync("requests/" + blockedId))
            {
                var receipt = JsonNode.Parse(await receiptResponse.Content.ReadAsStringAsync())!.AsObject();
                check(receiptResponse.StatusCode == HttpStatusCode.OK && receipt["state"]!.GetValue<string>() == "committed"
                    && receipt["dataObjects"]!["user"]!["id"]!.GetValue<string>() == committed.Body["dataObjects"]!["user"]!["id"]!.GetValue<string>(),
                    "review.log_io_failure_receipt_remains_recoverable");
            }
            var retry = await Send(blockedRequest);
            check(retry.Status == HttpStatusCode.OK && retry.Body["replayed"]!.GetValue<bool>()
                && retry.Body["dataObjects"]!["user"]!["id"]!.GetValue<string>() == committed.Body["dataObjects"]!["user"]!["id"]!.GetValue<string>(),
                "review.log_io_failure_retry_is_idempotent");
            await WaitUntilAsync(() => Task.FromResult(diagnostics.ToString().Contains("diagnostic log unavailable", StringComparison.Ordinal)));
            check(diagnostics.ToString().Split('\n').Count(line => line.Contains("diagnostic log unavailable", StringComparison.Ordinal)) == 1,
                "review.log_io_failure_one_visible_diagnostic_per_episode");
        }
        finally
        {
            Console.SetError(previousError);
            if (moved)
            {
                if (File.Exists(logDirectory)) File.Delete(logDirectory);
                Directory.Move(savedLogs, logDirectory);
            }
        }

        var recovered = await Send(blockedRequest);
        check(recovered.Status == HttpStatusCode.OK, "review.log_io_recovery_preserves_success");
        await WaitUntilAsync(async () => (await File.ReadAllTextAsync(logFile)).Contains(blockedId, StringComparison.Ordinal));
        var logs = await File.ReadAllTextAsync(logFile);
        check(!logs.Contains(sensitiveName, StringComparison.Ordinal), "review.logs_exclude_request_payload");
        check(logs.Contains(blockedId, StringComparison.Ordinal), "review.log_io_recovery_resumes_logging");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!await condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(3)) throw new InvalidOperationException("The expected host diagnostic did not appear within its bounded wait.");
            await Task.Delay(20);
        }
    }
}
