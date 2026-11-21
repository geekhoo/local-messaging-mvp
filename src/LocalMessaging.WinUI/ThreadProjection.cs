using System.Globalization;
using System.Text.Json.Nodes;

namespace LocalMessaging.WinUI;

/// <summary>A bounded thread projection that preserves live events arriving during a snapshot query.</summary>
public sealed class ThreadProjection
{
    public const int MaximumMessages = 250;
    private readonly Dictionary<string, JsonObject> messages = new();
    private readonly Dictionary<string, JsonObject> duringLoad = new();
    public IReadOnlyDictionary<string, JsonObject> Messages => messages;
    public string? RootId { get; private set; }
    public string? NextPageToken { get; private set; }
    public string? SnapshotCursor { get; private set; }
    public bool IsLoading { get; private set; }
    public bool NeedsRefresh { get; private set; }

    public void Open(string rootId)
    {
        RootId = rootId;
        messages.Clear(); duringLoad.Clear();
        NextPageToken = SnapshotCursor = null;
        IsLoading = false; NeedsRefresh = false;
    }

    public void BeginPage(bool reset = false)
    {
        if (IsLoading) throw new InvalidOperationException("A thread page is already loading.");
        if (RootId is null) throw new InvalidOperationException("Open a thread before loading its pages.");
        if (reset) { messages.Clear(); NextPageToken = SnapshotCursor = null; NeedsRefresh = false; }
        duringLoad.Clear(); IsLoading = true;
    }

    public void CompletePage(JsonObject page)
    {
        if (!IsLoading) throw new InvalidOperationException("No thread page is being loaded.");
        var root = page["root"]?.AsObject() ?? throw new InvalidDataException("Missing thread root.");
        if (root["id"]?.GetValue<string>() != RootId) throw new InvalidDataException("The thread response returned a different root.");
        var pageItems = Objects(page["contextMessages"]).Concat(Objects(page["items"])).Prepend(root).ToArray();
        if (messages.Count + pageItems.Count(item => !messages.ContainsKey(item["id"]!.GetValue<string>())) > MaximumMessages)
            messages.Clear(); // A bounded page window retains the page's complete ancestor context.
        foreach (var item in pageItems)
        {
            var snapshotItem = (JsonObject)item.DeepClone();
            ShellViewModel.StampSnapshot(snapshotItem, page["snapshotCursor"]?.GetValue<string>());
            Add(snapshotItem);
        }
        NextPageToken = page["nextPageToken"]?.GetValue<string>();
        SnapshotCursor = page["snapshotCursor"]?.GetValue<string>();
        IsLoading = false;
        foreach (var item in duringLoad.Values.OrderBy(Sequence)) Add(item);
        duringLoad.Clear();
    }

    public void CancelPage()
    {
        IsLoading = false;
        foreach (var item in duringLoad.Values.OrderBy(Sequence)) Add(item);
        duringLoad.Clear();
    }

    public bool Observe(JsonObject message)
    {
        if (RootId != message["threadRootMessageId"]?.GetValue<string>()) return false;
        if (IsLoading)
        {
            var id = message["id"]!.GetValue<string>();
            if (duringLoad.Count < MaximumMessages || duringLoad.ContainsKey(id)) duringLoad[id] = (JsonObject)message.DeepClone();
            else NeedsRefresh = true;
            return false;
        }
        Add(message);
        return true;
    }

    private void Add(JsonObject message)
    {
        var id = message["id"]!.GetValue<string>();
        if (messages.Count >= MaximumMessages && !messages.ContainsKey(id)) { NeedsRefresh = true; return; }
        if (messages.TryGetValue(id, out var existing) && existing["_nativeSnapshotSequence"] is not null
            && message["_nativeSnapshotSequence"] is null) return;
        var parent = message["parentMessageId"]?.GetValue<string>();
        if (parent is not null && !messages.ContainsKey(parent)) NeedsRefresh = true;
        messages[id] = (JsonObject)message.DeepClone();
    }

    private static IEnumerable<JsonObject> Objects(JsonNode? value) => value is JsonArray array ? array.OfType<JsonObject>() : [];
    private static long Sequence(JsonObject message) => long.Parse(message["sequence"]!.GetValue<string>(), CultureInfo.InvariantCulture);
}
