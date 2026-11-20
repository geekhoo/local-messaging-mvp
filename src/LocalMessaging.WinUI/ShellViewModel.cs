using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using LocalMessaging.Client;

namespace LocalMessaging.WinUI;

public sealed record NavigationItem(string Id, string Title, string Subtitle, JsonObject Data)
{
    public override string ToString() => string.IsNullOrEmpty(Subtitle) ? Title : $"{Title} · {Subtitle}";
}

public sealed class ShellViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly Func<Func<Task>, Task> dispatch;
    private readonly SemaphoreSlim transition = new(1, 1);
    private int disposed;
    private MessagingClient? client;
    private CancellationTokenSource lifetime = new();
    private CancellationTokenSource? streamCancellation;
    private CancellationTokenSource? viewCancellation;
    private Task? streamTask;
    private string? subscriptionId;
    private Guid? subscriptionOwner;
    private long generation;
    private long navigationGeneration;
    private string? userPage, channelPage, sessionPage, historyPage, mentionsPage;
    private string? historySnapshot, mentionsSnapshot;
    private string snapshot = "";
    private bool initializingSnapshot;
    private string userPrefix = "";
    private readonly string settingsPath;
    private readonly NativeOptions options;
    public ObservableCollection<NavigationItem> Users { get; } = [];
    public ObservableCollection<NavigationItem> Channels { get; } = [];
    public ObservableCollection<NavigationItem> Sessions { get; } = [];
    public ObservableCollection<JsonObject> Messages { get; } = [];
    public ObservableCollection<JsonObject> Mentions { get; } = [];
    public Guid? ActiveUserId { get; private set; }
    public string? ChannelId { get; private set; }
    public string? SessionId { get; private set; }
    public string? StoreEpoch { get; private set; }
    public bool IsTransitioning { get; private set; }
    public bool SupportsProfileAcknowledgements { get; private set; }
    public bool SupportsParticipantRosters { get; private set; }
    public string? ComposerScope => ActiveUserId is Guid user && SessionId is string session ? StoreEpoch + ":" + user + ":" + session : null;
    public bool CanCompose(string? scope) => !IsTransitioning && scope is not null && scope == ComposerScope;
    public string ConnectionState { get; private set; } = "Starting";
    public string? LastError { get; private set; }
    public bool HasMoreUsers => userPage is not null;
    public bool HasMoreChannels => channelPage is not null;
    public bool HasMoreSessions => sessionPage is not null;
    public bool HasMoreHistory => historyPage is not null;
    public bool HasMoreMentions => mentionsPage is not null;
    public bool ShowingMentions { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;
    public event Action<JsonObject>? MessageArrived;
    public MessagingClient Api => client ?? throw new InvalidOperationException("Host is not connected.");
    public ShellViewModel(Func<Func<Task>, Task> dispatcher, NativeOptions options)
    {
        dispatch = dispatcher;
        this.options = options;
        settingsPath = Path.Combine(Path.GetDirectoryName(options.EndpointPath)!, "winui-settings.json");
    }

    private void Notify()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        Changed?.Invoke();
    }

    public string UserLabel(string? id) => Users.FirstOrDefault(u => u.Id == id)?.Title ?? (id is { Length: > 8 } ? id[..8] : id ?? "");
    public string ChannelLabel => Channels.FirstOrDefault(x => x.Id == ChannelId)?.ToString() ?? "Choose a channel";
    public string SessionLabel => Sessions.FirstOrDefault(x => x.Id == SessionId)?.ToString() ?? "Choose a session";
    public string ChannelTitle => Channels.FirstOrDefault(x => x.Id == ChannelId)?.Title ?? "Choose a channel";
    public string SessionTitle => Sessions.FirstOrDefault(x => x.Id == SessionId)?.Title ?? "Choose a session";
    private NavigationItem User(JsonObject o) => new(o["id"]!.GetValue<string>(), o["displayName"]!.GetValue<string>(), "@" + o["handle"]!.GetValue<string>(), o);
    private NavigationItem Channel(JsonObject o) => new(o["id"]!.GetValue<string>(), o["name"]!.GetValue<string>(), "owner " + UserLabel(o["ownerUserId"]?.GetValue<string>()), o);
    private NavigationItem Session(JsonObject o) => new(o["id"]!.GetValue<string>(), o["title"]!.GetValue<string>(), "owner " + UserLabel(o["ownerUserId"]?.GetValue<string>()), o);

    // One transition owns profile, snapshot, navigation, and subscription fields at a time.
    // The dispatcher remains responsive while this asynchronous gate is held.
    private async Task TransitionAsync(Func<Task> action)
    {
        await transition.WaitAsync(lifetime.Token);
        IsTransitioning = true;
        try { Notify(); await action(); }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested && viewCancellation?.IsCancellationRequested == true)
        {
            // A newer navigation deliberately superseded this read. Do not surface it as an API failure.
        }
        finally { IsTransitioning = false; transition.Release(); Notify(); }
    }

    private Task ProfileTransitionAsync(Func<Task> action)
    {
        Interlocked.Increment(ref navigationGeneration);
        viewCancellation?.Cancel();
        return TransitionAsync(action);
    }

    private Task NavigateAsync(Func<Task> action)
    {
        var requested = Interlocked.Increment(ref navigationGeneration);
        viewCancellation?.Cancel(); // Cancel the obsolete read before waiting for transition ownership.
        return TransitionAsync(async () =>
        {
            if (requested == navigationGeneration) await action();
        });
    }

    private Task ViewReadAsync(Func<Task> action)
    {
        var requested = navigationGeneration;
        return TransitionAsync(async () =>
        {
            if (requested == navigationGeneration) await action();
        });
    }

    public Task ConnectAsync() => ProfileTransitionAsync(ConnectCoreAsync);
    public Task ReloadAsync() => ProfileTransitionAsync(ReloadCoreAsync);
    public Task SelectUserAsync(Guid id) => ProfileTransitionAsync(() => SelectUserCoreAsync(id));
    public Task LoadUsersAsync(string? prefix = null) => TransitionAsync(() => LoadUsersCoreAsync(prefix));
    public Task LoadChannelsAsync() => TransitionAsync(LoadChannelsCoreAsync);
    public Task SelectChannelAsync(string id) => NavigateAsync(() => SelectChannelCoreAsync(id));
    public Task LoadSessionsAsync() => ViewReadAsync(LoadSessionsCoreAsync);
    public Task SelectSessionAsync(string id) => NavigateAsync(() => SelectSessionCoreAsync(id));
    public Task LoadHistoryAsync() => ViewReadAsync(LoadHistoryCoreAsync);
    public Task ShowMentionsAsync(bool more = false) => more ? ViewReadAsync(() => ShowMentionsCoreAsync(true)) : NavigateAsync(() => ShowMentionsCoreAsync(false));
    public Task NavigateReceiptAsync(JsonObject receipt) => NavigateAsync(() => NavigateReceiptCoreAsync(receipt));

    private async Task ConnectCoreAsync()
    {
        await StopConsumerAsync();
        LastError = null;
        ConnectionState = "Starting";
        Notify();
        client?.Dispose();
        client = null;
        using var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        readyTimeout.CancelAfter(TimeSpan.FromSeconds(20));
        async Task<MessagingClient> ReadyClientAsync()
        {
            EndpointDescriptor? descriptor = null;
            if (File.Exists(options.EndpointPath)) descriptor = await EndpointDescriptor.ReadAsync(options.EndpointPath, readyTimeout.Token);
            else if (options.CustomEndpoint) throw new IOException("The isolated endpoint descriptor is not available yet.");
            var candidate = descriptor is null ? new MessagingClient(new Uri("http://127.0.0.1:7444/api/v1/")) : MessagingClient.FromDescriptor(descriptor);
            candidate.RequestTimeout = TimeSpan.FromSeconds(2);
            try
            {
                var status = await candidate.GetAsync("status", readyTimeout.Token);
                if (status["ready"]?.GetValue<bool>() != true) throw new IOException("The host has not completed readiness checks yet.");
                if (descriptor is not null && status["instanceId"]?.GetValue<string>() != descriptor.InstanceId.ToString())
                    throw new IOException("The endpoint descriptor refers to a different host instance. Waiting for a current descriptor.");
                candidate.RequestTimeout = TimeSpan.FromSeconds(30);
                return candidate;
            }
            catch { candidate.Dispose(); throw; }
        }
        try { client = await ReadyClientAsync(); }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException
            or OperationCanceledException or MessagingApiException { IsTemporary: true })
        {
            if (!options.LaunchHost)
                throw new IOException("The configured host is unavailable. Start that host, then choose Reconnect. Automatic launch is disabled.", e);
            var knownHost = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "host", "LocalMessaging.Host.exe"));
            if (!File.Exists(knownHost)) throw new IOException("Start LocalMessaging.Host, then choose Reconnect. The bundled host was not found in ../host/.", e);
            if (!string.Equals(Path.GetFileName(options.EndpointPath), "endpoint.json", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Automatic launch writes endpoint.json. Start the host manually for a differently named descriptor.", e);
            var start = new ProcessStartInfo(knownHost) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(knownHost)! };
            if (options.CustomEndpoint)
            {
                start.ArgumentList.Add("--state-dir");
                start.ArgumentList.Add(Path.GetDirectoryName(options.EndpointPath)!);
            }
            using var launched = Process.Start(start);
            while (true)
            {
                readyTimeout.Token.ThrowIfCancellationRequested();
                await Task.Delay(500, readyTimeout.Token);
                try { client = await ReadyClientAsync(); break; }
                catch (Exception retry) when (!readyTimeout.IsCancellationRequested && retry is HttpRequestException or IOException
                    or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException or MessagingApiException { IsTemporary: true }) { }
            }
        }
        if (ActiveUserId is null && File.Exists(settingsPath))
        {
            try
            {
                var settings = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath, lifetime.Token));
                if (Guid.TryParse(settings?["userId"]?.GetValue<string>(), out var user)) ActiveUserId = user;
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { }
        }
        await ReloadCoreAsync();
    }

    private async Task ReloadCoreAsync()
    {
        await StopConsumerAsync();
        ConnectionState = "Loading snapshot";
        SupportsProfileAcknowledgements = SupportsParticipantRosters = false;
        Notify();
        var status = await Api.GetAsync("status", lifetime.Token);
        var epoch = status["storeEpoch"]!.GetValue<string>();
        var capabilities = status["capabilities"] as JsonObject;
        SupportsProfileAcknowledgements = CapabilityEnabled(capabilities, "profileAcknowledgements");
        SupportsParticipantRosters = CapabilityEnabled(capabilities, "participantRosters");
        if (StoreEpoch != epoch)
        {
            ChannelId = null;
            SessionId = null;
            Messages.Clear();
            Mentions.Clear();
        }
        StoreEpoch = epoch;
        snapshot = status["headCursor"]!.GetValue<string>();
        userPage = channelPage = sessionPage = historyPage = mentionsPage = null;
        Users.Clear(); Channels.Clear(); Sessions.Clear();
        await LoadUsersCoreAsync();
        if (ActiveUserId is Guid existing && Users.All(x => x.Id != existing.ToString()))
        {
            try { Users.Add(User(await Api.GetAsync($"users/{existing}", lifetime.Token))); }
            catch (MessagingApiException ex) when (ex.Status == 404) { ActiveUserId = null; }
        }
        await LoadChannelsCoreAsync();
        initializingSnapshot = true;
        try { if (ChannelId is not null) await SelectChannelCoreAsync(ChannelId); }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested && viewCancellation?.IsCancellationRequested == true)
        {
            // A navigation can replace the selected history while bootstrap owns the gate.
            // Catalog H is already loaded; still install replay after H before releasing it.
        }
        finally { initializingSnapshot = false; }
        if (ActiveUserId is Guid caller)
        {
            var subscription = await Api.CreateSubscriptionAsync(caller, snapshot, lifetime.Token);
            subscriptionId = subscription["id"]!.GetValue<string>();
            subscriptionOwner = caller;
            StartConsumer(subscriptionId, subscription["startCursor"]!.GetValue<string>());
        }
        else ConnectionState = "Select or create a user";
        await SaveSettingsAsync();
        Notify();
    }

    private async Task SelectUserCoreAsync(Guid id)
    {
        if (id == ActiveUserId) return;
        ActiveUserId = id;
        ShowingMentions = false;
        Mentions.Clear();
        await ReloadCoreAsync();
    }

    private void StartConsumer(string id, string cursor)
    {
        streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = streamCancellation.Token;
        var activeGeneration = ++generation;
        var consumer = new SubscriptionConsumer(Api, id, cursor);
        streamTask = Task.Run(async () =>
        {
            try
            {
                await consumer.RunAsync((e, ct) => dispatch(() =>
                {
                    if (generation == activeGeneration) ApplyEvent(e);
                    return Task.CompletedTask;
                }), state => _ = dispatch(() =>
                {
                    if (generation == activeGeneration && ConnectionState != state) { ConnectionState = state; Notify(); }
                    return Task.CompletedTask;
                }), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception e)
            {
                await dispatch(() =>
                {
                    if (generation != activeGeneration || lifetime.IsCancellationRequested) return Task.CompletedTask;
                    streamTask = null;
                    LastError = e.Message;
                    ConnectionState = e is MessagingApiException { RequiresResynchronization: true } ? "Resynchronizing" : "Unavailable";
                    Notify();
                    // Recovery must not be awaited by this stream task: a transition may
                    // already hold the gate while awaiting the old stream's completion.
                    if (e is MessagingApiException { RequiresResynchronization: true }) _ = RecoverAsync(activeGeneration);
                    return Task.CompletedTask;
                });
            }
        }, token);
    }

    private async Task RecoverAsync(long failedGeneration)
    {
        try
        {
            await TransitionAsync(async () =>
            {
                if (generation == failedGeneration) await ReloadCoreAsync();
            });
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { LastError = error.Message; ConnectionState = "Unavailable"; Notify(); }
    }

    private async Task StopConsumerAsync()
    {
        generation++;
        var oldCancellation = streamCancellation;
        var oldTask = streamTask;
        var oldId = subscriptionId;
        var oldOwner = subscriptionOwner;
        streamCancellation = null; streamTask = null; subscriptionId = null; subscriptionOwner = null;
        oldCancellation?.Cancel();
        if (oldTask is not null)
        {
            try { await oldTask; }
            catch (OperationCanceledException) { }
        }
        oldCancellation?.Dispose();
        if (oldId is not null && oldOwner is Guid owner && client is not null)
        {
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await Api.CloseSubscriptionAsync(owner, oldId, closeTimeout.Token); }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or MessagingApiException)
            {
                // A disconnected descriptor can remain saved with zero live connections, as specified.
                LastError = "Previous stream was stopped; its saved subscription could not be closed: " + e.Message;
            }
        }
    }

    private async Task LoadUsersCoreAsync(string? prefix = null)
    {
        if (prefix is not null) { userPrefix = prefix; userPage = null; Users.Clear(); }
        var page = await Api.GetAsync("users?limit=50&asOf=" + Uri.EscapeDataString(snapshot)
            + (userPrefix.Length == 0 ? "" : "&handlePrefix=" + Uri.EscapeDataString(userPrefix))
            + (userPage is null ? "" : "&pageToken=" + Uri.EscapeDataString(userPage)), lifetime.Token);
        foreach (var item in Objects(page["items"])) Merge(Users, User(item));
        userPage = page["nextPageToken"]?.GetValue<string>();
        Notify();
    }

    private async Task LoadChannelsCoreAsync()
    {
        var page = await Api.GetAsync("channels?limit=50&asOf=" + Uri.EscapeDataString(snapshot)
            + (channelPage is null ? "" : "&pageToken=" + Uri.EscapeDataString(channelPage)), lifetime.Token);
        foreach (var item in Objects(page["items"])) Merge(Channels, Channel(item));
        channelPage = page["nextPageToken"]?.GetValue<string>();
        Notify();
    }

    private async Task SelectChannelCoreAsync(string id)
    {
        ResetViewCancellation();
        var oldSession = ChannelId == id ? SessionId : null;
        ChannelId = id; SessionId = null; ShowingMentions = false;
        Sessions.Clear(); Messages.Clear(); sessionPage = historyPage = null;
        if (Channels.All(x => x.Id != id)) Merge(Channels, Channel(await Api.GetAsync($"channels/{id}", viewCancellation!.Token)));
        await LoadSessionsCoreAsync();
        viewCancellation!.Token.ThrowIfCancellationRequested();
        if (oldSession is not null) await SelectSessionCoreAsync(oldSession);
        Notify();
    }

    private async Task LoadSessionsCoreAsync()
    {
        if (ChannelId is null) return;
        var selected = ChannelId;
        var token = viewCancellation?.Token ?? lifetime.Token;
        var page = await Api.GetAsync($"channels/{selected}/sessions?limit=50"
            + (initializingSnapshot ? "&asOf=" + Uri.EscapeDataString(snapshot) : "")
            + (sessionPage is null ? "" : "&pageToken=" + Uri.EscapeDataString(sessionPage)), token);
        if (selected != ChannelId || token.IsCancellationRequested) return;
        foreach (var item in Objects(page["items"])) Merge(Sessions, Session(item));
        sessionPage = page["nextPageToken"]?.GetValue<string>();
        Notify();
    }

    private async Task SelectSessionCoreAsync(string id)
    {
        ResetViewCancellation();
        var selected = Sessions.FirstOrDefault(x => x.Id == id)?.Data
            ?? await Api.GetAsync($"sessions/{id}", viewCancellation!.Token);
        if (selected["channelId"]?.GetValue<string>() != ChannelId)
            throw new InvalidOperationException("That session belongs to a different channel. Choose its channel first.");
        Merge(Sessions, Session((JsonObject)selected.DeepClone()));
        SessionId = id; ShowingMentions = false; Messages.Clear(); historyPage = null; historySnapshot = initializingSnapshot ? snapshot : null;
        Notify();
        await LoadHistoryCoreAsync();
    }

    private void ResetViewCancellation()
    {
        viewCancellation?.Cancel(); viewCancellation?.Dispose();
        viewCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
    }

    private async Task LoadHistoryCoreAsync()
    {
        if (SessionId is null) return;
        var selected = SessionId;
        var token = viewCancellation?.Token ?? lifetime.Token;
        var page = await Api.GetAsync($"sessions/{selected}/messages?view=roots&limit=50"
            + (historyPage is null ? "" : "&pageToken=" + Uri.EscapeDataString(historyPage))
            + (historySnapshot is null ? "" : "&asOf=" + Uri.EscapeDataString(historySnapshot)), token);
        if (selected != SessionId || token.IsCancellationRequested) return;
        historySnapshot = page["snapshotCursor"]!.GetValue<string>();
        historyPage = page["nextPageToken"]?.GetValue<string>();
        foreach (var message in Objects(page["items"])) MergeMessage(Messages, message, keepNewest: false);
        Notify();
    }

    private async Task ShowMentionsCoreAsync(bool more = false)
    {
        if (ActiveUserId is not Guid user) return;
        if (!more) ResetViewCancellation();
        var token = viewCancellation?.Token ?? lifetime.Token;
        ShowingMentions = true;
        if (!more) { Mentions.Clear(); mentionsPage = null; mentionsSnapshot = null; }
        var page = await Api.GetAsync($"users/{user}/mentions?limit=50"
            + (mentionsPage is null ? "" : "&pageToken=" + Uri.EscapeDataString(mentionsPage))
            + (mentionsSnapshot is null ? "" : "&asOf=" + Uri.EscapeDataString(mentionsSnapshot)), token);
        if (ActiveUserId != user || !ShowingMentions || token.IsCancellationRequested) return;
        mentionsSnapshot = page["snapshotCursor"]!.GetValue<string>();
        mentionsPage = page["nextPageToken"]?.GetValue<string>();
        foreach (var message in Objects(page["items"])) MergeMessage(Mentions, message, keepNewest: false);
        Notify();
    }

    public Task<JsonObject> GetThreadAsync(string id, string? token = null, string? asOf = null, CancellationToken cancellationToken = default) =>
        Api.GetAsync($"messages/{id}/thread?limit=50" + (token is null ? "" : "&pageToken=" + Uri.EscapeDataString(token))
            + (asOf is null ? "" : "&asOf=" + Uri.EscapeDataString(asOf)), cancellationToken);

    public async Task<(IReadOnlyList<NavigationItem> Items, string? NextPageToken)> GetChannelSessionsPageAsync(string channelId, string? pageToken, CancellationToken cancellationToken)
    {
        var page = await Api.GetAsync($"channels/{channelId}/sessions?limit=50"
            + (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken)), cancellationToken);
        return (Objects(page["items"]).Select(Session).ToList(), page["nextPageToken"]?.GetValue<string>());
    }

    private static bool CapabilityEnabled(JsonObject? capabilities, string name) =>
        capabilities?[name] is JsonValue value && value.TryGetValue<bool>(out var enabled) && enabled;

    public Task<JsonObject> GetPublicProfilesAsync(string path, string? pageToken, string? asOf, CancellationToken cancellationToken) =>
        Api.GetAsync(path + "?limit=25" + (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken))
            + (asOf is null ? "" : "&asOf=" + Uri.EscapeDataString(asOf)), cancellationToken);

    public async Task<JsonObject> SubmitPublicProfileCommandAsync(FrozenCommand command, CancellationToken cancellationToken)
    {
        var requestEpoch = StoreEpoch;
        var receipt = await Api.SendWithRecoveryAsync(command, cancellationToken: cancellationToken);
        if (requestEpoch != StoreEpoch || receipt["storeEpoch"]?.GetValue<string>() is string resultEpoch && resultEpoch != StoreEpoch)
            throw new MessagingApiException(409, "store_changed", "The store changed while this command was in flight. Reload before reconciling its receipt.");
        return receipt;
    }

    public async Task<JsonObject> SubmitAsync(FrozenCommand command, CancellationToken cancellationToken = default)
    {
        var receipt = await SubmitPublicProfileCommandAsync(command, cancellationToken);
        if (receipt["dataObjects"] is JsonObject objects)
        {
            if (objects["user"] is JsonObject user) Merge(Users, User((JsonObject)user.DeepClone()));
            if (objects["channel"] is JsonObject channel) Merge(Channels, Channel((JsonObject)channel.DeepClone()));
            if (objects["session"] is JsonObject session && session["channelId"]?.GetValue<string>() == ChannelId)
                Merge(Sessions, Session((JsonObject)session.DeepClone()));
            if (objects["message"] is JsonObject message) ApplyMessage((JsonObject)message.DeepClone());
        }
        Notify();
        return receipt;
    }

    private async Task NavigateReceiptCoreAsync(JsonObject receipt)
    {
        if (receipt["dataObjects"]?["channel"] is not JsonObject channel || receipt["dataObjects"]?["session"] is not JsonObject session) return;
        await SelectChannelCoreAsync(channel["id"]!.GetValue<string>());
        await SelectSessionCoreAsync(session["id"]!.GetValue<string>());
    }

    private void ApplyEvent(JsonObject e)
    {
        if (e["dataObject"] is not JsonObject value) return;
        switch (e["eventType"]?.GetValue<string>())
        {
            case "user.created": Merge(Users, User((JsonObject)value.DeepClone())); break;
            case "channel.created": Merge(Channels, Channel((JsonObject)value.DeepClone())); break;
            case "session.created":
                if (value["channelId"]?.GetValue<string>() == ChannelId) Merge(Sessions, Session((JsonObject)value.DeepClone()));
                break;
            case "message.created": ApplyMessage((JsonObject)value.DeepClone()); break;
        }
        Notify();
    }

    private void ApplyMessage(JsonObject value)
    {
        if (value["sessionId"]?.GetValue<string>() == SessionId && value["parentMessageId"] is null)
            MergeMessage(Messages, value);
        if (ActiveUserId is Guid user && value["targetUserIds"] is JsonArray targets
            && targets.Any(x => x?.GetValue<string>() == user.ToString())) MergeMessage(Mentions, (JsonObject)value.DeepClone());
        MessageArrived?.Invoke(value);
        // Replies never become detached roots. Threads are queried when opened; their cards expose Refresh.
    }

    private static void Merge(ObservableCollection<NavigationItem> target, NavigationItem item)
    {
        var existing = target.FirstOrDefault(x => x.Id == item.Id);
        if (existing is not null) target[target.IndexOf(existing)] = item; else target.Add(item);
        // A bounded window of navigation; the Next action continues the authoritative paged query.
        while (target.Count > 200) target.RemoveAt(0);
    }
    private static void MergeMessage(ObservableCollection<JsonObject> target, JsonObject item, bool keepNewest = true)
    {
        var id = item["id"]!.GetValue<string>();
        var old = target.FirstOrDefault(x => x["id"]?.GetValue<string>() == id);
        if (old is not null) target.Remove(old);
        var sequence = long.Parse(item["sequence"]!.GetValue<string>());
        int index = 0;
        while (index < target.Count && long.Parse(target[index]["sequence"]!.GetValue<string>()) < sequence) index++;
        target.Insert(index, item);
        while (target.Count > 200) target.RemoveAt(keepNewest ? 0 : target.Count - 1);
    }
    public static IEnumerable<JsonObject> Objects(JsonNode? value) => value is JsonArray array
        ? array.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()) : [];

    private async Task SaveSettingsAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var temporary = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, new JsonObject { ["userId"] = ActiveUserId?.ToString(), ["storeEpoch"] = StoreEpoch, ["apiBase"] = Api.BaseAddress.ToString() }.ToJsonString(), lifetime.Token);
        File.Move(temporary, settingsPath, true);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        viewCancellation?.Cancel();
        await transition.WaitAsync();
        try
        {
            await StopConsumerAsync();
            viewCancellation?.Dispose();
            client?.Dispose();
        }
        finally { transition.Release(); }
        lifetime.Dispose();
    }
}
