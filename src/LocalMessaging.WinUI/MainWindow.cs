using System.Text.Json.Nodes;
using LocalMessaging.Client;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace LocalMessaging.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly ShellViewModel model;
    private readonly Grid root = new() { Padding = new Thickness(24, 20, 24, 16), RowSpacing = 16, ColumnSpacing = 20 };
    private readonly ComboBox profiles = new() { MinWidth = 200, PlaceholderText = "Select user" };
    private readonly StackPanel treePanel = new() { Spacing = 10 };
    private readonly TextBlock channelsEmpty = new() { Text = "No channels yet.", Foreground = TextBrush("TextFillColorSecondaryBrush"), Margin = new Thickness(0, 12, 0, 0) };
    private readonly Dictionary<string, List<NavigationItem>> treeSessions = new();
    private readonly Dictionary<string, string?> treeSessionPages = new();
    private readonly HashSet<string> treeExpanded = new();
    private readonly HashSet<string> treeLoading = new();
    private string? treeSignature;
    private readonly StackPanel messagePanel = new() { Spacing = 16 };
    private readonly TextBlock title = new() { Style = TextStyle("TitleTextBlockStyle") };
    private readonly TextBlock subtitle = new() { Style = TextStyle("CaptionTextBlockStyle"), Foreground = TextBrush("TextFillColorSecondaryBrush") };
    private readonly TextBlock connection = new() { Style = TextStyle("CaptionTextBlockStyle"), Foreground = TextBrush("TextFillColorTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center };
    private readonly InfoBar notice = new() { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Informational };
    private readonly TextBox editor = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 220, MaxLength = 16384, PlaceholderText = "Write a message. Type @ to mention a user." };
    private readonly StackPanel mentionSuggestions = new() { Orientation = Orientation.Horizontal, Spacing = 6, Visibility = Visibility.Collapsed };
    private CancellationTokenSource? mentionLookupCancellation;
    private readonly TextBlock draftContext = new() { Style = TextStyle("CaptionTextBlockStyle"), Foreground = TextBrush("TextFillColorTertiaryBrush") };
    private readonly Image draftPreview = new() { MaxWidth = 260, MaxHeight = 140, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    private readonly Button send = new() { Content = "Send", MinWidth = 100 };
    private readonly Button older = new() { Content = "Load older messages", HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button nextUsers = new() { Content = "More profiles" };
    private readonly Button nextChannels = new() { Content = "More channels", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Dictionary<string, ComposerDraft> drafts = new();
    private readonly Dictionary<string, BitmapImage> imageCache = new();
    private readonly SemaphoreSlim imageSlots = new(4);
    private readonly CancellationTokenSource closing = new();
    private CancellationTokenSource renderImages = new();
    private CancellationTokenSource threadImages = new();
    private CancellationTokenSource previewCancellation = new();
    private CancellationTokenSource? threadLifetime;
    private readonly SemaphoreSlim threadPageSlot = new(1, 1);
    private byte[]? previewBytes;
    private readonly SemaphoreSlim dialogSlot = new(1);
    private bool synchronizing;
    private bool started;
    private bool sending;
    private bool draftCapacityAvailable = true;
    private Button? attachImage;
    private Button? clearContext;
    private CreationDraft? userCreation, channelCreation, sessionCreation;
    private string? draftKey;
    private string? renderedEpoch;
    private ComposerDraft draft = new();
    private ContentDialog? threadDialog;
    private StackPanel? threadPanel;
    private TextBox? _threadEditor;
    private TextBlock? _threadTargetLabel;
    private TextBlock? _threadStatus;
    private Button? _sendReply;
    private JsonObject? _replyParent;
    private Guid? _threadCaller;
    private FrozenCommand? _pendingReply;
    private bool _isSendingReply;
    private bool _indicatorRefreshRunning;
    private ThreadProjection thread = new();

    public MainWindow(NativeOptions options)
    {
        Title = "Local Messaging";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 850));
        model = new ShellViewModel(DispatchAsync, options);
        model.Changed += Render;
        model.MessageArrived += MessageArrived;
        model.AcknowledgementArrived += messageId =>
        {
            if (thread.Messages.ContainsKey(messageId)) RenderThread();
        };
        BuildLayout();
        Content = root;
        Activated += async (_, _) =>
        {
            if (started) return;
            started = true;
            await RunAsync(model.ConnectAsync);
        };
        Closed += async (_, _) =>
        {
            closing.Cancel();
            renderImages.Cancel(); threadImages.Cancel(); previewCancellation.Cancel(); threadLifetime?.Cancel();
            mentionLookupCancellation?.Cancel();
            try { await model.DisposeAsync(); } catch (Exception e) { System.Diagnostics.Debug.WriteLine(e.Message); }
            closing.Dispose();
        };
    }

    private void BuildLayout()
    {
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accent) && accent is Style accentStyle) send.Style = accentStyle;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "Local Messaging", Style = TextStyle("SubtitleTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center });
        Add(header, profiles, 0, 1);
        Add(header, GlyphButton("\uE710", "User", CreateUserAsync, "Create user"), 0, 2);
        Add(header, nextUsers, 0, 3);
        Add(header, IconButton("\uE72C", "Reconnect", async () => { SaveDraft(); await model.ConnectAsync(); RestoreDraft(); }), 0, 4);
        root.Children.Add(header);
        Add(root, notice, 1, 0);
        var workspace = new Grid { ColumnSpacing = 24 };
        workspace.ColumnDefinitions.Add(new() { Width = new GridLength(290) });
        workspace.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        Add(root, workspace, 2, 0);
        InitializePublicProfileButtons();
        var treeColumn = new Grid { RowSpacing = 12 };
        treeColumn.RowDefinitions.Add(new() { Height = GridLength.Auto });
        treeColumn.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        treeColumn.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var treeHeader = new Grid { ColumnSpacing = 8 };
        treeHeader.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        treeHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        treeHeader.Children.Add(new TextBlock { Text = "Channels", Style = TextStyle("BodyStrongTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center });
        Add(treeHeader, GlyphButton("\uE710", "Channel", CreateChannelAsync, "Create channel"), 0, 1);
        Add(treeColumn, treeHeader, 0, 0);
        var treeHost = new Grid();
        Add(treeHost, new ScrollViewer { Content = treePanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, 0, 0);
        Add(treeHost, channelsEmpty, 0, 0);
        Add(treeColumn, treeHost, 1, 0);
        Add(treeColumn, nextChannels, 2, 0);
        Add(workspace, treeColumn, 0, 0);
        var conversation = new Grid { RowSpacing = 16 };
        conversation.RowDefinitions.Add(new() { Height = GridLength.Auto });
        conversation.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        conversation.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new Grid { ColumnSpacing = 16 };
        heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var headingTitles = new StackPanel { Spacing = 4 };
        headingTitles.Children.Add(title); headingTitles.Children.Add(subtitle);
        Add(heading, headingTitles, 0, 0);
        var rosterRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        rosterRow.Children.Add(_channelParticipants);
        rosterRow.Children.Add(_sessionParticipants);
        Add(heading, rosterRow, 0, 1);
        Add(conversation, heading, 0, 0);
        var timeline = new StackPanel { Spacing = 16, Padding = new Thickness(0, 4, 12, 8) };
        timeline.Children.Add(older); timeline.Children.Add(messagePanel);
        Add(conversation, new ScrollViewer { Content = timeline, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, 1, 0);
        var composer = new StackPanel { Spacing = 10 };
        composer.Children.Add(draftContext);
        composer.Children.Add(draftPreview);
        composer.Children.Add(mentionSuggestions);
        composer.Children.Add(editor);
        AutomationProperties.SetName(mentionSuggestions, "Mention suggestions");
        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var secondary = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        attachImage = GlyphButton("\uE723", "Attach", PickImageAsync, "Attach image");
        secondary.Children.Add(attachImage);
        clearContext = GlyphButton("\uE74D", "Clear", () =>
        {
            if (!AdmitComposerMutation()) return Task.CompletedTask;
            draft.Image = null; draft.ImageName = null; draft.Edited();
            UpdateDraft(); return Task.CompletedTask;
        }, "Clear image");
        secondary.Children.Add(clearContext);
        Add(actions, secondary, 0, 0);
        Add(actions, send, 0, 1);
        composer.Children.Add(actions);
        Add(conversation, composer, 2, 0);
        var contentLayer = new Border
        {
            Background = ThemeBrush("LayerFillColorDefaultBrush", ColorHelper.FromArgb(180, 255, 255, 255)),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush", ColorHelper.FromArgb(30, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(24, 20, 24, 20),
            Child = conversation
        };
        Add(workspace, contentLayer, 0, 1);
        Add(root, connection, 3, 0);
        AutomationProperties.SetName(profiles, "Selected user profile");
        AutomationProperties.SetName(treePanel, "Channels and sessions");
        AutomationProperties.SetAutomationId(editor, "MainComposer");
        AutomationProperties.SetName(editor, "Plain text message or image caption");

        profiles.SelectionChanged += async (_, _) =>
        {
            if (synchronizing || profiles.SelectedItem is not NavigationItem user) return;
            SaveDraft();
            await RunAsync(async () => { await model.SelectUserAsync(Guid.Parse(user.Id)); RestoreDraft(); });
        };
        nextUsers.Click += async (_, _) => await RunAsync(() => model.LoadUsersAsync());
        nextChannels.Click += async (_, _) => await RunAsync(model.LoadChannelsAsync);
        older.Click += async (_, _) => await RunAsync(() => model.ShowingMentions ? model.ShowMentionsAsync(true) : model.LoadHistoryAsync());
        send.Click += async (_, _) => await RunAsync(SendAsync);
        editor.TextChanged += (_, _) =>
        {
            if (synchronizing) return;
            if (!AdmitComposerMutation()) return;
            draft.Text = editor.Text;
            draft.Edited(); // Changing a draft is an explicit new command; an unchanged retry keeps its ID.
            UpdateDraft();
            _ = UpdateMentionSuggestionsAsync();
        };
        editor.SelectionChanged += (_, _) =>
        {
            if (!synchronizing && started) _ = UpdateMentionSuggestionsAsync();
        };
    }

    private static void Add(Grid parent, FrameworkElement child, int row, int column)
    {
        Grid.SetRow(child, row); Grid.SetColumn(child, column); parent.Children.Add(child);
    }
    private Button Button(string label, Func<Task> action)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) => await RunAsync(action);
        return button;
    }

    private static FontFamily SymbolFont() =>
        Application.Current.Resources.TryGetValue("SymbolThemeFontFamily", out var value) && value is FontFamily family
            ? family : new FontFamily("Segoe Fluent Icons");
    private static TextBlock Icon(string glyph) => new()
    {
        Text = glyph, FontFamily = SymbolFont(),
        IsTextScaleFactorEnabled = false, VerticalAlignment = VerticalAlignment.Center
    };
    private static StackPanel IconLabel(string glyph, string label)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(Icon(glyph));
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return content;
    }
    private Button GlyphButton(string glyph, string label, Func<Task> action, string? name = null, bool subtle = false)
    {
        var button = new Button { Content = IconLabel(glyph, label) };
        if (subtle && TextStyle("SubtleButtonStyle") is Style style) button.Style = style;
        AutomationProperties.SetName(button, name ?? label);
        ToolTipService.SetToolTip(button, name ?? label);
        button.Click += async (_, _) => await RunAsync(action);
        return button;
    }
    private Button IconButton(string glyph, string name, Func<Task> action, bool subtle = true)
    {
        var button = new Button { Content = Icon(glyph) };
        if (subtle && TextStyle("SubtleButtonStyle") is Style style) button.Style = style;
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += async (_, _) => await RunAsync(action);
        return button;
    }

    private void BuildTree()
    {
        if (model.ChannelId is string activeChannel)
        {
            treeExpanded.Add(activeChannel);
            if (model.Sessions.Count > 0 || !treeSessions.ContainsKey(activeChannel))
            {
                if (!treeSessions.TryGetValue(activeChannel, out var activeList))
                    treeSessions[activeChannel] = activeList = new List<NavigationItem>();
                foreach (var s in model.Sessions) if (activeList.All(x => x.Id != s.Id)) activeList.Add(s);
            }
        }
        var signature = string.Join(";", model.Channels.Select(c => c.Id)) + "|" + model.ChannelId + "|" + model.SessionId
            + "|" + string.Join(",", treeExpanded.OrderBy(x => x, StringComparer.Ordinal))
            + "|" + string.Join(",", treeSessions.Keys.OrderBy(x => x, StringComparer.Ordinal)
                .Select(k => k + ":" + string.Join(".", treeSessions[k].Select(s => s.Id)) + ":" + (treeSessionPages.TryGetValue(k, out var p) && p is not null ? 1 : 0)));
        if (signature == treeSignature) return;
        treeSignature = signature;
        treePanel.Children.Clear();
        foreach (var channel in model.Channels) treePanel.Children.Add(ChannelExpander(channel));
    }

    private Expander ChannelExpander(NavigationItem channel)
    {
        var header = new TextBlock
        {
            Text = channel.Title,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = model.ChannelId == channel.Id ? FontWeights.SemiBold : FontWeights.Normal
        };
        ToolTipService.SetToolTip(header, channel.ToString());
        var expander = new Expander
        {
            Header = header,
            IsExpanded = treeExpanded.Contains(channel.Id),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(expander, channel.ToString());
        var content = new StackPanel { Spacing = 8, Margin = new Thickness(24, 4, 0, 4) };
        expander.Content = content;
        if (!treeSessions.TryGetValue(channel.Id, out var sessions))
        {
            content.Children.Add(new TextBlock
            {
                Text = treeLoading.Contains(channel.Id) ? "Loading sessions…" : "Sessions load when the channel is opened.",
                Style = TextStyle("CaptionTextBlockStyle"),
                Foreground = TextBrush("TextFillColorTertiaryBrush")
            });
        }
        else
        {
            if (sessions.Count == 0)
                content.Children.Add(new TextBlock { Text = "No sessions yet.", Style = TextStyle("CaptionTextBlockStyle"), Foreground = TextBrush("TextFillColorSecondaryBrush") });
            foreach (var session in sessions)
            {
                var row = new Button
                {
                    Content = session.Title,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 2)
                };
                AutomationProperties.SetName(row, session.ToString());
                ToolTipService.SetToolTip(row, session.ToString());
                if (model.SessionId == session.Id)
                {
                    row.FontWeight = FontWeights.SemiBold;
                    row.Background = ThemeBrush("SubtleFillColorSecondaryBrush", ColorHelper.FromArgb(16, 0, 0, 0));
                }
                var sessionId = session.Id;
                row.Click += async (_, _) => await RunAsync(async () =>
                {
                    SaveDraft();
                    if (model.ChannelId != channel.Id) await model.SelectChannelAsync(channel.Id);
                    if (model.SessionId != sessionId) await model.SelectSessionAsync(sessionId);
                    RestoreDraft();
                });
                content.Children.Add(row);
            }
            if (treeSessionPages.TryGetValue(channel.Id, out var more) && more is not null)
                content.Children.Add(Button("More sessions", () => LoadTreeSessionsAsync(channel.Id, true)));
            content.Children.Add(GlyphButton("\uE710", "Session", () => CreateSessionAsync(channel.Id), "Create session"));
        }
        expander.Expanding += async (_, _) =>
        {
            if (synchronizing) return;
            treeExpanded.Add(channel.Id);
            await RunAsync(async () =>
            {
                if (model.ChannelId != channel.Id) { SaveDraft(); await model.SelectChannelAsync(channel.Id); RestoreDraft(); }
                if (!treeSessions.TryGetValue(channel.Id, out var current) || current.Count == 0)
                    await LoadTreeSessionsAsync(channel.Id, false);
            });
        };
        expander.Collapsed += (_, _) => { if (!synchronizing) treeExpanded.Remove(channel.Id); };
        return expander;
    }

    private async Task LoadTreeSessionsAsync(string channelId, bool advance)
    {
        if (treeLoading.Contains(channelId)) return;
        if (!advance && treeSessions.ContainsKey(channelId)) return;
        treeLoading.Add(channelId);
        try
        {
            var (items, next) = await model.GetChannelSessionsPageAsync(channelId, advance ? treeSessionPages.GetValueOrDefault(channelId) : null, closing.Token);
            if (!treeSessions.TryGetValue(channelId, out var list)) treeSessions[channelId] = list = new List<NavigationItem>();
            foreach (var item in items) if (list.All(x => x.Id != item.Id)) list.Add(item);
            treeSessionPages[channelId] = next;
        }
        finally { treeLoading.Remove(channelId); }
        Render();
    }

    private void Render()
    {
        if (closing.IsCancellationRequested) return;
        UpdatePublicProfileButtons();
        if (renderedEpoch != model.StoreEpoch)
        {
            imageCache.Clear();
            treeSessions.Clear(); treeSessionPages.Clear(); treeExpanded.Clear(); treeLoading.Clear(); treeSignature = null;
            if (renderedEpoch is not null)
            {
                drafts.Clear(); draft = new(); draftKey = null;
                userCreation = channelCreation = sessionCreation = null;
                threadLifetime?.Cancel(); threadDialog?.Hide();
                ShowNotice("The store changed. Profiles and history are being reloaded; drafts from the previous history were cleared.", InfoBarSeverity.Informational);
            }
            renderedEpoch = model.StoreEpoch;
        }
        renderImages.Cancel(); renderImages.Dispose();
        renderImages = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        synchronizing = true;
        try
        {
            profiles.ItemsSource = model.Users;
            profiles.SelectedItem = model.Users.FirstOrDefault(x => x.Id == model.ActiveUserId?.ToString());
            BuildTree();
            connection.Text = model.ConnectionState + (model.ActiveUserId is Guid id ? " · " + model.UserLabel(id.ToString()) : "");
            title.Text = model.ShowingMentions ? "Mentions" : model.SessionTitle;
            subtitle.Text = model.ShowingMentions ? "Public messages targeting your profile" : model.ChannelTitle;
            nextUsers.Visibility = model.HasMoreUsers ? Visibility.Visible : Visibility.Collapsed;
            channelsEmpty.Visibility = model.Channels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            nextChannels.Visibility = model.HasMoreChannels ? Visibility.Visible : Visibility.Collapsed;
            older.Visibility = (model.ShowingMentions ? model.HasMoreMentions : model.HasMoreHistory) ? Visibility.Visible : Visibility.Collapsed;
            messagePanel.Children.Clear();
            var collection = model.ShowingMentions ? model.Mentions : model.Messages;
            if (collection.Count == 0)
                messagePanel.Children.Add(new TextBlock { Text = model.ShowingMentions ? "No mentions loaded." : model.SessionId is null ? "Choose a session, or create a channel to begin." : "No messages yet. Start the conversation.", Foreground = TextBrush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 16) });
            foreach (var message in collection) messagePanel.Children.Add(MessageCard(message, false));
            if (model.LastError is string error) ShowNotice(error, InfoBarSeverity.Warning);
            if (draftKey != CurrentDraftKey()) RestoreDraft();
            UpdateDraft();
            UpdateThreadComposer();
        }
        finally { synchronizing = false; }
    }

    private UIElement MessageCard(JsonObject message, bool insideThread)
    {
        var body = new StackPanel { Spacing = 6 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new TextBlock
        {
            Text = model.UserLabel(message["authorUserId"]?.GetValue<string>()),
            FontSize = 13, FontWeight = FontWeights.Normal,
            Foreground = TextBrush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var stamp = DateTimeOffset.TryParse(message["createdAtUtc"]?.GetValue<string>(), out var time) ? time.ToLocalTime().ToString("g") : "";
        header.Children.Add(new TextBlock { Text = "·  " + stamp, Style = TextStyle("CaptionTextBlockStyle"), Foreground = TextBrush("TextFillColorTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(header);
        var content = message["content"]!.AsObject();
        var text = content["type"]?.GetValue<string>() == "image" ? content["caption"]?.GetValue<string>() : content["text"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(text))
            body.Children.Add(new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                FontSize = 15, LineHeight = 23, MaxWidth = 640,
                Margin = new Thickness(0, 2, 0, 4), HorizontalAlignment = HorizontalAlignment.Left
            });
        if (content["image"] is JsonObject image)
        {
            var imageControl = new Image { MaxWidth = 560, MaxHeight = 360, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) };
            AutomationProperties.SetName(imageControl, string.IsNullOrWhiteSpace(text) ? "Image message" : text);
            body.Children.Add(imageControl);
            _ = LoadImageAsync(imageControl, image, insideThread ? threadImages.Token : renderImages.Token);
        }
        if (message["targetUserIds"] is JsonArray targets && targets.Count > 0)
            body.Children.Add(new TextBlock { Text = "→ " + string.Join(", ", targets.Select(t => model.UserLabel(t?.GetValue<string>()))), Foreground = TextBrush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap, Style = TextStyle("CaptionTextBlockStyle"), Margin = new Thickness(0, 0, 0, 2) });
        var actions = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var messageIdText = message["id"]!.GetValue<string>();
        var rootId = message["threadRootMessageId"]!.GetValue<string>();
        var depth = message["depth"]?.GetValue<int>() ?? 1;
        var (replyCount, acknowledgementCount, latestHandle) = model.MessageIndicators(message);
        var isNew = model.HasNewReply(messageIdText);
        var label = replyCount > 0 ? $"Reply ({replyCount})" : "Reply";
        if (isNew) label += " \u00b7 New reply";
        var name = isNew ? $"Reply, {replyCount} replies, New reply" : replyCount > 0 ? $"Reply, {replyCount} replies" : "Reply";
        var canReply = depth < 3 && model.ActiveUserId is not null && !model.IsTransitioning;
        var reply = GlyphButton("\uE97A", label, () =>
        {
            if (insideThread)
            {
                if (canReply) SelectReplyParent(message);
                return Task.CompletedTask;
            }
            return ShowThreadAsync(rootId, canReply ? message : null);
        }, canReply ? name : !insideThread ? $"Open thread, {replyCount} replies" : "Maximum thread depth (3) reached", subtle: !isNew);
        if (isNew && TextStyle("AccentButtonStyle") is Style accent) reply.Style = accent;
        reply.IsEnabled = !insideThread || canReply;
        AutomationProperties.SetAutomationId(reply, $"Reply-{(insideThread ? "Thread" : "Timeline")}-{messageIdText}");
        actions.Children.Add(reply);
        if (model.SupportsProfileAcknowledgements && Guid.TryParse(messageIdText, out var messageId))
            Add(actions, CreateAcknowledgementsButton(messageId, acknowledgementCount, latestHandle, insideThread), 0, 1);
        body.Children.Add(actions);
        return new Border
        {
            Padding = new Thickness(20, 16, 20, 14), CornerRadius = new CornerRadius(8),
            Background = ThemeBrush("CardBackgroundFillColorDefaultBrush", ColorHelper.FromArgb(12, 80, 130, 180)),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush", ColorHelper.FromArgb(35, 100, 130, 160)), BorderThickness = new Thickness(1),
            Margin = new Thickness(insideThread ? (depth - 1) * 28 : 0, 0, 0, 0), Child = body
        };
    }

    private static Brush ThemeBrush(string key, Windows.UI.Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is SolidColorBrush brush
            ? brush : new SolidColorBrush(fallback);
    private static Style? TextStyle(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Style style ? style : null;
    private static Brush? TextBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is SolidColorBrush brush ? brush : null;

    private async Task LoadImageAsync(Image target, JsonObject descriptor, CancellationToken token)
    {
        var id = descriptor["id"]!.GetValue<string>();
        try
        {
            if (imageCache.TryGetValue(id, out var cached)) { target.Source = cached; return; }
            await imageSlots.WaitAsync(token);
            try
            {
                var bytes = await model.Api.GetImageAsync(descriptor["contentPath"]!.GetValue<string>(), token);
                using var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                }
                stream.Seek(0);
                var bitmap = new BitmapImage { DecodePixelWidth = 512, DecodePixelHeight = 512 };
                await bitmap.SetSourceAsync(stream);
                token.ThrowIfCancellationRequested();
                target.Source = bitmap;
                if (imageCache.Count >= 16) imageCache.Remove(imageCache.Keys.First());
                imageCache[id] = bitmap;
            }
            finally { imageSlots.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!closing.IsCancellationRequested) ShowNotice("Image could not be loaded: " + e.Message, InfoBarSeverity.Warning); }
    }

    private async Task ShowThreadAsync(string rootId, JsonObject? parent)
    {
        if (threadDialog is not null || !await dialogSlot.WaitAsync(0, closing.Token)) return;
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        threadLifetime = lifetime;
        try
        {
            thread = new ThreadProjection();
            thread.Open(rootId);
            _threadCaller = model.ActiveUserId;
            threadPanel = new StackPanel { Spacing = 10 };
            // Reserve room for the dialog header and footer; only the message list should shrink.
            var container = new Grid { RowSpacing = 10, MinWidth = 480, MaxHeight = Math.Max(220, root.XamlRoot.Size.Height - 176) };
            container.RowDefinitions.Add(new() { Height = GridLength.Auto });
            container.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            container.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var refresh = IconButton("\uE72C", "Refresh thread", () => LoadThreadPageAsync(true));
            var more = Button("More replies", () => thread.NextPageToken is null ? Task.CompletedTask : LoadThreadPageAsync());
            AutomationProperties.SetAutomationId(refresh, "RefreshThread");
            AutomationProperties.SetAutomationId(more, "LoadMoreReplies");
            actions.Children.Add(refresh);
            actions.Children.Add(more);
            container.Children.Add(actions);
            Add(container, new ScrollViewer { Content = threadPanel, MaxHeight = 390, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 1, 0);

            var composer = new StackPanel { Spacing = 6, Visibility = Visibility.Collapsed };
            _threadTargetLabel = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
            _threadEditor = new TextBox { Header = "Thread reply", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, MaxHeight = 108, MaxLength = 16384 };
            ScrollViewer.SetVerticalScrollBarVisibility(_threadEditor, ScrollBarVisibility.Auto);
            _threadEditor.TextChanged += (_, _) =>
            {
                if (_isSendingReply) return;
                _pendingReply = null;
                UpdateThreadComposer();
            };
            _threadStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            _sendReply = new Button { Content = "Send reply", HorizontalAlignment = HorizontalAlignment.Right };
            _sendReply.Click += async (_, _) => await SendReplyAsync();
            AutomationProperties.SetAutomationId(_threadTargetLabel, "ThreadReplyTarget");
            AutomationProperties.SetLabeledBy(_threadEditor, _threadTargetLabel);
            AutomationProperties.SetAutomationId(_threadEditor, "ThreadComposer");
            AutomationProperties.SetAutomationId(_sendReply, "SendThreadReply");
            AutomationProperties.SetLiveSetting(_threadStatus, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            composer.Children.Add(_threadTargetLabel);
            composer.Children.Add(_threadEditor);
            composer.Children.Add(_threadStatus);
            composer.Children.Add(_sendReply);
            Add(container, composer, 2, 0);

            var dialog = new ContentDialog { Title = "Thread", XamlRoot = root.XamlRoot, CloseButtonText = "Close", Content = container };
            AutomationProperties.SetAutomationId(dialog, "MessageThread");
            threadDialog = dialog;
            dialog.Closing += (_, args) => args.Cancel = _isSendingReply && !lifetime.IsCancellationRequested;
            dialog.Opened += (_, _) =>
            {
                var wasUnread = model.HasNewReply(rootId);
                model.SetOpenThread(rootId);
                if (wasUnread) RenderThread();
                // Select after the editor is attached so its panel can be revealed and focused.
                if (parent is not null) SelectReplyParent(parent);
            };
            await LoadThreadPageAsync();
            lifetime.Token.ThrowIfCancellationRequested();
            await dialog.ShowAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            model.SetOpenThread(null);
            lifetime.Cancel(); lifetime.Dispose(); threadLifetime = null;
            threadDialog = null; threadPanel = null;
            _threadEditor = null; _threadTargetLabel = null; _threadStatus = null; _sendReply = null;
            _threadCaller = null; _replyParent = null; _pendingReply = null;
            threadImages.Cancel();
            thread = new ThreadProjection(); dialogSlot.Release();
        }
    }

    private void SelectReplyParent(JsonObject parent)
    {
        if (_isSendingReply || _threadEditor is null || _threadTargetLabel is null || model.IsTransitioning
            || _threadCaller is null || _threadCaller != model.ActiveUserId) return;
        if (_replyParent?["id"]?.GetValue<string>() != parent["id"]?.GetValue<string>())
        {
            _replyParent = parent;
            _pendingReply = null;
            var content = parent["content"]!.AsObject();
            var excerpt = content["text"]?.GetValue<string>() ?? content["caption"]?.GetValue<string>();
            var author = model.UserLabel(parent["authorUserId"]?.GetValue<string>());
            _threadTargetLabel.Text = $"Replying to {author}: {(string.IsNullOrWhiteSpace(excerpt) ? "Image message" : excerpt)}";
            SetThreadStatus(null);
        }
        if (_threadEditor.Parent is StackPanel composer) composer.Visibility = Visibility.Visible;
        UpdateThreadComposer();
        _threadEditor.Focus(FocusState.Programmatic);
    }

    private void UpdateThreadComposer()
    {
        if (_threadEditor is null || _sendReply is null) return;
        var canReply = !_isSendingReply && _replyParent is not null && !model.IsTransitioning && _threadCaller == model.ActiveUserId;
        _threadEditor.IsReadOnly = !canReply;
        _sendReply.IsEnabled = canReply && !string.IsNullOrWhiteSpace(_threadEditor.Text);
        _sendReply.Content = _isSendingReply ? "Sending reply..." : _pendingReply is null ? "Send reply" : "Retry reply";
    }

    private async Task RefreshIndicatorsAsync()
    {
        if (_indicatorRefreshRunning || closing.IsCancellationRequested) return;
        _indicatorRefreshRunning = true;
        try
        {
            await model.ReloadAsync();
            if (threadDialog is not null && thread.RootId is not null) await LoadThreadPageAsync(true);
        }
        catch (Exception error) when (!closing.IsCancellationRequested)
        {
            ShowNotice("Live indicators were not refreshed; showing the bounded recent activity until the next reconnect. " + error.Message, InfoBarSeverity.Warning);
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
        finally { _indicatorRefreshRunning = false; }
    }

    private async Task SendReplyAsync()
    {
        if (_isSendingReply || model.IsTransitioning || _threadCaller is not Guid caller || caller != model.ActiveUserId
            || _replyParent is not JsonObject parent || _threadEditor is not TextBox input || string.IsNullOrWhiteSpace(input.Text)) return;
        _pendingReply ??= FrozenCommand.Create(caller, new JsonObject
        {
            ["type"] = "message", ["channel"] = new JsonObject { ["mode"] = "existing", ["id"] = parent["channelId"]!.GetValue<string>() },
            ["session"] = new JsonObject { ["mode"] = "existing", ["id"] = parent["sessionId"]!.GetValue<string>() },
            ["content"] = new JsonObject { ["type"] = "text", ["text"] = input.Text },
            ["parentMessageId"] = parent["id"]!.GetValue<string>()
        });
        _isSendingReply = true;
        UpdateThreadComposer();
        SetThreadStatus(null);
        try
        {
            await model.SubmitAsync(_pendingReply, closing.Token);
            if (ReferenceEquals(_threadEditor, input))
            {
                _pendingReply = null;
                input.Text = "";
                SetThreadStatus("Reply sent.");
            }
        }
        catch (Exception error) when (!closing.IsCancellationRequested)
        {
            if (ReferenceEquals(_threadEditor, input))
                SetThreadStatus($"Reply not confirmed: {error.Message} Retry unchanged to keep the request ID.");
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
        finally
        {
            _isSendingReply = false;
            UpdateThreadComposer();
            if (ReferenceEquals(_threadEditor, input)) input.Focus(FocusState.Programmatic);
        }
    }

    private void SetThreadStatus(string? text)
    {
        if (_threadStatus is null) return;
        _threadStatus.Text = text ?? "";
        _threadStatus.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task LoadThreadPageAsync(bool reset = false)
    {
        var token = threadLifetime?.Token ?? closing.Token;
        await threadPageSlot.WaitAsync(token);
        var projection = thread;
        try
        {
            if (projection.RootId is null) return;
            projection.BeginPage(reset);
            var result = await model.GetThreadAsync(projection.RootId, projection.NextPageToken, projection.SnapshotCursor, token);
            token.ThrowIfCancellationRequested();
            projection.CompletePage(result);
            if (ReferenceEquals(projection, thread)) RenderThread();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { projection.CancelPage(); }
        catch { projection.CancelPage(); throw; }
        finally { threadPageSlot.Release(); }
    }

    private void RenderThread()
    {
        if (threadPanel is null || thread.RootId is null) return;
        threadImages.Cancel(); threadImages.Dispose();
        threadImages = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        threadPanel.Children.Clear();
        void AddChildren(string id, int level)
        {
            if (level > 3 || !thread.Messages.TryGetValue(id, out var current)) return;
            threadPanel.Children.Add(MessageCard(current, true));
            foreach (var child in thread.Messages.Values.Where(m => m["parentMessageId"]?.GetValue<string>() == id).OrderBy(m => long.Parse(m["sequence"]!.GetValue<string>())))
                AddChildren(child["id"]!.GetValue<string>(), level + 1);
        }
        AddChildren(thread.RootId, 1);
        if (thread.NeedsRefresh)
            ShowNotice("Additional replies are available. Refresh this thread to load their ancestor context.", InfoBarSeverity.Informational);
    }

    private void MessageArrived(JsonObject message)
    {
        if (threadDialog is not null && thread.Observe(message)) RenderThread();
    }

    private async Task CreateUserAsync()
    {
        var saved = userCreation ??= new CreationDraft(null, null, model.StoreEpoch);
        if (saved.Sending) return;
        if (!await CreationFormAsync("Create user", saved, ("handle", "Handle", 32, false, ""), ("displayName", "Display name", 80, false, ""))) return;
        var selectedAtStart = model.ActiveUserId;
        var receipt = await SubmitCreationAsync(saved, new JsonObject
        {
            ["type"] = "user", ["handle"] = saved.Fields["handle"], ["displayName"] = saved.Fields["displayName"]
        }, "Create user");
        if (ReferenceEquals(userCreation, saved)) userCreation = null;
        if (model.ActiveUserId == selectedAtStart)
        {
            SaveDraft();
            await model.SelectUserAsync(Guid.Parse(receipt["dataObjects"]!["user"]!["id"]!.GetValue<string>()));
            RestoreDraft();
        }
        ShowNotice("User created.", InfoBarSeverity.Success);
    }

    private async Task CreateChannelAsync()
    {
        var saved = channelCreation ??= new CreationDraft(RequireUser(), null, model.StoreEpoch);
        if (saved.Sending) return;
        if (!await CreationFormAsync("Create channel", saved, ("name", "Channel name", 120, false, ""),
            ("description", "Description (optional)", 1000, true, ""), ("initialSessionTitle", "Initial session title", 120, false, "General"))) return;
        var result = await SubmitCreationAsync(saved, new JsonObject
        {
            ["type"] = "channel", ["name"] = saved.Fields["name"], ["description"] = saved.Fields["description"], ["initialSessionTitle"] = saved.Fields["initialSessionTitle"]
        }, "Create channel");
        if (ReferenceEquals(channelCreation, saved)) channelCreation = null;
        if (model.ActiveUserId == saved.Caller) { SaveDraft(); await model.NavigateReceiptAsync(result); RestoreDraft(); }
        ShowNotice("Channel and initial session created.", InfoBarSeverity.Success);
    }

    private async Task CreateSessionAsync(string? channelId = null)
    {
        var saved = sessionCreation ??= new CreationDraft(RequireUser(), channelId ?? model.ChannelId ?? throw new InvalidOperationException("Choose a channel first."), model.StoreEpoch);
        if (saved.Sending) return;
        if (!await CreationFormAsync("Create session", saved, ("title", "Session title", 120, false, ""))) return;
        var result = await SubmitCreationAsync(saved, new JsonObject
        {
            ["type"] = "session", ["channel"] = new JsonObject { ["mode"] = "existing", ["id"] = saved.ChannelId }, ["title"] = saved.Fields["title"]
        }, "Create session");
        if (ReferenceEquals(sessionCreation, saved)) sessionCreation = null;
        if (model.ActiveUserId == saved.Caller) { SaveDraft(); await model.NavigateReceiptAsync(result); RestoreDraft(); }
        ShowNotice("Session created.", InfoBarSeverity.Success);
    }

    private async Task<bool> CreationFormAsync(string heading, CreationDraft saved,
        params (string Key, string Label, int Maximum, bool Multiline, string Default)[] fields)
    {
        await dialogSlot.WaitAsync(closing.Token);
        try
        {
            var panel = new StackPanel { Spacing = 12, MinWidth = 370 };
            var context = saved.Caller is Guid caller ? "As " + model.UserLabel(caller.ToString()) : "Local profile · no password required";
            if (saved.ChannelId is not null) context += " · " + (model.Channels.FirstOrDefault(c => c.Id == saved.ChannelId)?.Title ?? saved.ChannelId);
            if (saved.Pending is not null) context += "\nUnconfirmed — retry reuses request " + saved.Pending.RequestId + ".";
            panel.Children.Add(new TextBlock { Text = context, Style = TextStyle("CaptionTextBlockStyle"), Foreground = TextBrush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap, MaxWidth = 430 });
            var controls = fields.Select(field => new TextBox
            {
                Header = field.Label, Text = saved.Fields.GetValueOrDefault(field.Key, field.Default), MaxLength = field.Maximum,
                AcceptsReturn = field.Multiline, IsReadOnly = saved.Pending is not null
            }).ToArray();
            foreach (var control in controls) panel.Children.Add(control);
            var dialog = new ContentDialog
            {
                Title = heading, Content = panel, XamlRoot = root.XamlRoot,
                PrimaryButtonText = saved.Pending is null ? "Create" : "Retry original request", CloseButtonText = "Keep draft", DefaultButton = ContentDialogButton.Primary
            };
            var result = await dialog.ShowAsync();
            for (var i = 0; i < fields.Length; i++) saved.Fields[fields[i].Key] = controls[i].Text;
            return result == ContentDialogResult.Primary;
        }
        finally { dialogSlot.Release(); }
    }

    private async Task<JsonObject> SubmitCreationAsync(CreationDraft saved, JsonObject dataObject, string action)
    {
        if (saved.Epoch != model.StoreEpoch) throw new InvalidOperationException("The store changed. Open a new creation form against the current store.");
        saved.Pending ??= FrozenCommand.Create(saved.Caller, dataObject);
        saved.Sending = true;
        try { return await model.SubmitAsync(saved.Pending, closing.Token); }
        catch (MessagingApiException error) when (error.Status is 400 or 422 || error.Code is "handle_already_exists" or "user_not_found" or "channel_not_found")
        {
            saved.Pending = null; // A definitive validation rejection can be corrected; retain every form value.
            throw new InvalidOperationException(error.Message + " Reopen " + action + " to correct the saved form.", error);
        }
        catch (Exception error) when (error is not OperationCanceledException || !closing.IsCancellationRequested)
        {
            throw new InvalidOperationException(error.Message + " Reopen " + action + " to retry the saved request " + saved.Pending.RequestId + " with its original owner and values.", error);
        }
        finally { saved.Sending = false; }
    }

    private Guid RequireUser() => model.ActiveUserId ?? throw new InvalidOperationException("Select or create a user first.");

    private async Task PickImageAsync()
    {
        if (!AdmitComposerMutation()) return;
        RequireUser();
        if (model.SessionId is null) throw new InvalidOperationException("Choose a session first.");
        var originalDraft = draft;
        var originalEpoch = model.StoreEpoch;
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".png"); picker.FileTypeFilter.Add(".jpg"); picker.FileTypeFilter.Add(".jpeg");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var properties = await file.GetBasicPropertiesAsync();
        if (properties.Size > 8 * 1024 * 1024) throw new InvalidOperationException("The image must be at most 8 MiB.");
        var bytes = await ImageFileReader.ReadAsync(file.Path, closing.Token);
        if (sending || originalEpoch != model.StoreEpoch) return;
        originalDraft.Image = bytes;
        originalDraft.ImageName = file.Name;
        originalDraft.Edited();
        UpdateDraft();
    }

    private async Task SendAsync()
    {
        if (sending) return;
        if (!draftCapacityAvailable) throw new InvalidOperationException("Resolve one of the 16 saved pending drafts before starting another.");
        if (!AdmitComposerMutation()) return;
        var caller = RequireUser();
        var channelId = model.ChannelId ?? throw new InvalidOperationException("Choose a channel.");
        var sessionId = model.SessionId ?? throw new InvalidOperationException("Choose a session.");
        SaveDraft();
        var original = draft;
        var originalKey = draftKey;
        if (original.Image is not null && original.Text.Length > 4096)
            throw new InvalidOperationException("An image caption can contain at most 4,096 UTF-16 characters. Shorten the caption or remove the image.");
        original.Pending ??= FrozenCommand.Create(caller, new JsonObject
        {
            ["type"] = "message", ["channel"] = new JsonObject { ["mode"] = "existing", ["id"] = channelId },
            ["session"] = new JsonObject { ["mode"] = "existing", ["id"] = sessionId },
            ["content"] = original.Image is null ? new JsonObject { ["type"] = "text", ["text"] = original.Text } : new JsonObject { ["type"] = "image", ["caption"] = original.Text },
            ["targetUserIds"] = new JsonArray(original.Targets.Keys.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray())
        }, original.Image);
        var command = original.Pending;
        var revision = original.Revision;
        sending = true;
        UpdateDraft();
        ShowNotice("Sending as " + model.UserLabel(command.CallerUserId?.ToString()) + "…", InfoBarSeverity.Informational);
        try
        {
            await model.SubmitAsync(command, closing.Token);
            if (original.Revision == revision && ReferenceEquals(original.Pending, command))
            {
                if (originalKey is not null) drafts.Remove(originalKey);
                if (draftKey == originalKey) { draft = new(); RestoreDraft(); }
            }
            ShowNotice("Sent.", InfoBarSeverity.Success);
        }
        catch (Exception)
        {
            ShowNotice("Send was not confirmed. Retry retains the original user, request ID, and image bytes. Editing creates a new request.", InfoBarSeverity.Warning);
            throw;
        }
        finally { sending = false; UpdateDraft(); }
    }

    private string? CurrentDraftKey() => model.ComposerScope;
    private bool AdmitComposerMutation()
    {
        if (sending || !draftCapacityAvailable || model.IsTransitioning) return false;
        if (model.CanCompose(draftKey)) return true;
        SaveDraft(); RestoreDraft();
        ShowNotice("Select a user and session, then review that conversation's draft before sending.", InfoBarSeverity.Informational);
        return false;
    }
    private void SaveDraft()
    {
        draft.Text = editor.Text;
        if (draftKey is null || !draftCapacityAvailable) return;
        if (drafts.ContainsKey(draftKey) || ReserveDraftSlot()) drafts[draftKey] = draft;
    }
    private bool ReserveDraftSlot()
    {
        if (drafts.Count < 16) return true;
        var removable = drafts.FirstOrDefault(x => x.Value.Pending is null).Key;
        if (removable is not null) { drafts.Remove(removable); return true; }
        ShowNotice("Sixteen unconfirmed drafts are saved. Retry a saved draft before composing in another session.", InfoBarSeverity.Warning);
        return false;
    }
    private void RestoreDraft()
    {
        draftKey = CurrentDraftKey();
        if (draftKey is not null && drafts.TryGetValue(draftKey, out var saved))
        {
            draft = saved; draftCapacityAvailable = true;
        }
        else
        {
            draft = new ComposerDraft();
            draftCapacityAvailable = draftKey is null || ReserveDraftSlot();
            if (draftKey is not null && draftCapacityAvailable) drafts[draftKey] = draft;
        }
        UpdateDraft();
    }
    private void UpdateDraft()
    {
        if (closing.IsCancellationRequested) return;
        var old = synchronizing; synchronizing = true;
        editor.Text = draft.Text;
        if (!ReferenceEquals(previewBytes, draft.Image))
        {
            previewBytes = draft.Image;
            draftPreview.Source = null;
            draftPreview.Visibility = draft.Image is null ? Visibility.Collapsed : Visibility.Visible;
            previewCancellation.Cancel(); previewCancellation.Dispose();
            previewCancellation = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
            if (draft.Image is byte[] bytes) _ = LoadPreviewAsync(bytes, previewCancellation.Token);
        }
        // Never silently truncate existing text when the user attaches an image.
        editor.MaxLength = 16384;
        var hasContext = draft.Image is not null;
        editor.IsEnabled = model.CanCompose(draftKey) && !sending && draftCapacityAvailable;
        if (attachImage is not null) attachImage.IsEnabled = editor.IsEnabled;
        if (clearContext is not null)
        {
            clearContext.IsEnabled = editor.IsEnabled && hasContext;
            clearContext.Visibility = hasContext ? Visibility.Visible : Visibility.Collapsed;
        }
        send.IsEnabled = editor.IsEnabled;
        send.Content = sending ? "Sending…" : draft.Pending is null ? "Send" : "Retry send";
        draftContext.Visibility = hasContext ? Visibility.Visible : Visibility.Collapsed;
        draftContext.Text = hasContext ? draft.ImageName ?? "Attached image" : "";
        if (!editor.IsEnabled || string.IsNullOrEmpty(draft.Text)) HideMentionSuggestions();
        synchronizing = old;
    }

    private static bool TryGetActiveMentionToken(string text, int caret, out int tokenStart, out int tokenEnd, out string prefix)
    {
        tokenStart = tokenEnd = 0; prefix = "";
        if (string.IsNullOrEmpty(text)) return false;
        if (caret <= 0 || caret > text.Length) caret = text.Length;
        const string delimiters = " \t\n\r([{<'\"";
        var i = caret - 1;
        while (i >= 0 && IsHandleChar(text[i])) i--;
        if (i < 0 || text[i] != '@') return false;
        if (i > 0 && !delimiters.Contains(text[i - 1], StringComparison.Ordinal)) return false;
        var end = caret;
        while (end < text.Length && IsHandleChar(text[end])) end++;
        tokenStart = i; tokenEnd = end;
        prefix = text[(i + 1)..caret];
        return prefix.Length <= 32;
    }

    private static bool IsHandleChar(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_';

    private void HideMentionSuggestions()
    {
        mentionLookupCancellation?.Cancel();
        mentionSuggestions.Children.Clear();
        mentionSuggestions.Visibility = Visibility.Collapsed;
    }

    private async Task UpdateMentionSuggestionsAsync()
    {
        if (!started || !editor.IsEnabled || !TryGetActiveMentionToken(editor.Text, editor.SelectionStart, out var start, out var end, out var prefix))
        {
            HideMentionSuggestions();
            return;
        }
        var local = model.Users
            .Select(u => (Handle: u.Data["handle"]?.GetValue<string>() ?? u.Subtitle.TrimStart('@'), DisplayName: u.Title))
            .Where(u => u.Handle.Length > 0 && u.Handle.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .ToList();
        RenderMentionChips(local, start, end);
        mentionLookupCancellation?.Cancel(); mentionLookupCancellation?.Dispose();
        mentionLookupCancellation = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        var token = mentionLookupCancellation.Token;
        try
        {
            await Task.Delay(140, token);
            var result = await model.Api.GetAsync("users?limit=8" + (prefix.Length == 0 ? "" : "&handlePrefix=" + Uri.EscapeDataString(prefix)), token);
            if (token.IsCancellationRequested) return;
            if (!TryGetActiveMentionToken(editor.Text, editor.SelectionStart, out var curStart, out var curEnd, out var curPrefix)
                || !string.Equals(curPrefix, prefix, StringComparison.Ordinal)) return;
            var remote = ShellViewModel.Objects(result["items"])
                .Select(u => (Handle: u["handle"]!.GetValue<string>(), DisplayName: u["displayName"]!.GetValue<string>()))
                .ToList();
            RenderMentionChips(remote, curStart, curEnd);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void RenderMentionChips(IReadOnlyList<(string Handle, string DisplayName)> candidates, int tokenStart, int tokenEnd)
    {
        mentionSuggestions.Children.Clear();
        if (candidates.Count == 0)
        {
            mentionSuggestions.Visibility = Visibility.Collapsed;
            return;
        }
        foreach (var candidate in candidates)
        {
            var handle = candidate.Handle;
            var label = "@" + handle;
            var chip = new Button
            {
                Content = string.Equals(candidate.DisplayName, handle, StringComparison.OrdinalIgnoreCase)
                    ? label
                    : $"{label}  ·  {candidate.DisplayName}",
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 12
            };
            AutomationProperties.SetName(chip, $"Mention @{handle} ({candidate.DisplayName})");
            chip.Click += (_, _) => InsertMention(handle, tokenStart, tokenEnd);
            mentionSuggestions.Children.Add(chip);
        }
        mentionSuggestions.Visibility = Visibility.Visible;
    }

    private void InsertMention(string handle, int tokenStart, int tokenEnd)
    {
        if (!AdmitComposerMutation()) return;
        var text = editor.Text;
        if (tokenStart < 0 || tokenEnd > text.Length || tokenStart >= tokenEnd || text[tokenStart] != '@') return;
        var needsSpace = tokenEnd >= text.Length || !char.IsWhiteSpace(text[tokenEnd]);
        var replacement = "@" + handle + (needsSpace ? " " : "");
        var updated = text[..tokenStart] + replacement + text[tokenEnd..];
        var newCaret = tokenStart + replacement.Length;
        synchronizing = true;
        try
        {
            editor.Text = updated;
            editor.SelectionStart = newCaret;
            editor.SelectionLength = 0;
        }
        finally { synchronizing = false; }
        draft.Text = updated;
        draft.Edited();
        HideMentionSuggestions();
        UpdateDraft();
        editor.Focus(FocusState.Programmatic);
    }

    private async Task LoadPreviewAsync(byte[] bytes, CancellationToken token)
    {
        var admitted = false;
        try
        {
            await imageSlots.WaitAsync(token); admitted = true;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes); await writer.StoreAsync(); await writer.FlushAsync();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = 256, DecodePixelHeight = 256 };
            await bitmap.SetSourceAsync(stream).AsTask(token);
            token.ThrowIfCancellationRequested();
            if (ReferenceEquals(previewBytes, bytes)) draftPreview.Source = bitmap;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) { ShowNotice("Preview unavailable; the server will validate the selected image: " + e.Message, InfoBarSeverity.Warning); }
        finally { if (admitted) imageSlots.Release(); }
    }

    private Task DispatchAsync(Func<Task> action)
    {
        if (DispatcherQueue.HasThreadAccess) return action();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try { await action(); completion.SetResult(); }
            catch (Exception e) { completion.SetException(e); }
        })) completion.SetCanceled();
        return completion.Task;
    }
    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
        catch (MessagingApiException e)
        {
            var fields = e.Details?["errors"]?.ToJsonString();
            ShowNotice(e.Message + (fields is null ? "" : " " + fields), InfoBarSeverity.Error);
        }
        catch (Exception e) { ShowNotice(e.Message, InfoBarSeverity.Error); }
    }
    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        if (closing.IsCancellationRequested) return;
        notice.Message = message; notice.Severity = severity; notice.IsOpen = true;
    }
    private sealed class ComposerDraft
    {
        public string Text { get; set; } = "";
        public byte[]? Image { get; set; }
        public string? ImageName { get; set; }
        public Dictionary<string, string> Targets { get; } = new();
        public FrozenCommand? Pending { get; set; }
        public long Revision { get; private set; }
        public void Edited() { Revision++; Pending = null; }
    }
    private sealed class CreationDraft(Guid? caller, string? channelId, string? epoch)
    {
        public Guid? Caller { get; } = caller;
        public string? ChannelId { get; } = channelId;
        public string? Epoch { get; } = epoch;
        public Dictionary<string, string> Fields { get; } = new();
        public FrozenCommand? Pending { get; set; }
        public bool Sending { get; set; }
    }
}
