using System.Text.Json.Nodes;
using LocalMessaging.Client;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace LocalMessaging.WinUI;

public sealed partial class MainWindow
{
    private readonly Button _channelParticipants = new();
    private readonly Button _sessionParticipants = new();
    private readonly Dictionary<(string Epoch, Guid Caller, PublicScope Scope, Guid Id), PendingPublicAction> _pendingPublicActions = new();
    private Flyout? _publicProfilesFlyout;

    private enum PublicScope { Message, Channel, Session }

    private sealed record PendingPublicAction(bool? Joined, FrozenCommand Command)
    {
        public bool IsSending { get; set; }
    }

    private void InitializePublicProfileButtons()
    {
        _channelParticipants.Visibility = _sessionParticipants.Visibility = Visibility.Collapsed;
        _channelParticipants.Content = IconLabel("\uE716", "Channel");
        _sessionParticipants.Content = IconLabel("\uE77B", "Session");
        AutomationProperties.SetAutomationId(_channelParticipants, "ChannelParticipants");
        AutomationProperties.SetAutomationId(_sessionParticipants, "SessionParticipants");
        AutomationProperties.SetName(_channelParticipants, "Channel participants");
        AutomationProperties.SetName(_sessionParticipants, "Session participants");
        ToolTipService.SetToolTip(_channelParticipants, "Channel participants");
        ToolTipService.SetToolTip(_sessionParticipants, "Session participants");
        _channelParticipants.Click += async (_, _) => await RunAsync(() => Guid.TryParse(model.ChannelId, out var id)
            ? ShowPublicProfilesAsync(_channelParticipants, PublicScope.Channel, id, model.ChannelLabel) : Task.CompletedTask);
        _sessionParticipants.Click += async (_, _) => await RunAsync(() => Guid.TryParse(model.SessionId, out var id)
            ? ShowPublicProfilesAsync(_sessionParticipants, PublicScope.Session, id, model.SessionLabel) : Task.CompletedTask);
    }

    private void UpdatePublicProfileButtons()
    {
        if (model.IsTransitioning || renderedEpoch != model.StoreEpoch) _publicProfilesFlyout?.Hide();
        if (renderedEpoch is not null && renderedEpoch != model.StoreEpoch) _pendingPublicActions.Clear();
        _channelParticipants.Visibility = model.SupportsParticipantRosters && model.ChannelId is not null ? Visibility.Visible : Visibility.Collapsed;
        _sessionParticipants.Visibility = model.SupportsParticipantRosters && model.SessionId is not null ? Visibility.Visible : Visibility.Collapsed;
        _channelParticipants.IsEnabled = _sessionParticipants.IsEnabled = !model.IsTransitioning;
    }

    private Button CreateAcknowledgementsButton(Guid messageId)
    {
        var button = new Button { Content = IconLabel("\uE73E", "Ack"), IsEnabled = !model.IsTransitioning };
        if (TextStyle("SubtleButtonStyle") is Style style) button.Style = style;
        AutomationProperties.SetAutomationId(button, $"ProfileAcknowledgements-{messageId}");
        AutomationProperties.SetName(button, "Profile acknowledgements");
        ToolTipService.SetToolTip(button, "Profile acknowledgements");
        button.Click += async (_, _) => await RunAsync(() => ShowPublicProfilesAsync(button, PublicScope.Message, messageId, null));
        return button;
    }

    private async Task ShowPublicProfilesAsync(Button anchor, PublicScope scope, Guid id, string? scopeLabel)
    {
        var isMessage = scope == PublicScope.Message;
        bool HasCapability() => isMessage ? model.SupportsProfileAcknowledgements : model.SupportsParticipantRosters;
        if (model.IsTransitioning || !HasCapability() || model.StoreEpoch is not string epoch) return;

        var caller = model.ActiveUserId;
        var idPrefix = $"PublicProfiles-{scope}-{id}-";
        var path = scope switch
        {
            PublicScope.Message => $"messages/{id}/acknowledgements",
            PublicScope.Channel => $"channels/{id}/participants",
            _ => $"sessions/{id}/participants"
        };
        var panel = new StackPanel { Spacing = 8, Width = 320 };
        var flyout = new Flyout { Content = panel };
        var title = isMessage ? "Profile acknowledgements" : $"{scope} participants";
        var titleRow = new Grid { ColumnSpacing = 8 };
        titleRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        titleRow.Children.Add(new TextBlock { Text = title, Style = TextStyle("BodyStrongTextBlockStyle"), TextWrapping = TextWrapping.Wrap });
        var info = new Button { Content = Icon("\uE946"), Padding = new Thickness(8, 4, 8, 4), VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(info, "About " + title);
        ToolTipService.SetToolTip(info, scope switch
        {
            PublicScope.Message => "Public profile acknowledgements are not verified read receipts.",
            PublicScope.Channel => "Joined profiles, not live presence. Session participation also counts here. Leaving here only removes direct channel participation.",
            _ => "Joined profiles, not live presence. Joining also counts toward the channel roster. Leaving preserves direct channel participation."
        });
        Add(titleRow, info, 0, 1);
        panel.Children.Add(titleRow);
        if (scopeLabel is not null) panel.Children.Add(new TextBlock { Text = scopeLabel, TextWrapping = TextWrapping.Wrap, Style = TextStyle("CaptionTextBlockStyle"), Foreground = TextBrush("TextFillColorSecondaryBrush") });
        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = TextStyle("CaptionTextBlockStyle"), Foreground = TextBrush("TextFillColorTertiaryBrush") };
        AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Polite);
        panel.Children.Add(actionRow);
        panel.Children.Add(feedback);
        var list = new ListView { MaxHeight = 250, SelectionMode = ListViewSelectionMode.None };
        AutomationProperties.SetAutomationId(list, idPrefix + "List");
        AutomationProperties.SetName(list, title);
        panel.Children.Add(list);
        var pager = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(pager);
        var pageStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = TextStyle("CaptionTextBlockStyle"), Foreground = TextBrush("TextFillColorTertiaryBrush") };
        AutomationProperties.SetLiveSetting(pageStatus, AutomationLiveSetting.Polite);
        panel.Children.Add(pageStatus);

        using var waitingToShow = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        // The flyout owns read cancellation. Submitted commands retain their own lifetime and request ID.
        var readLifetime = CancellationTokenSource.CreateLinkedTokenSource(waitingToShow.Token);
        var readToken = readLifetime.Token;
        string? snapshot = null, nextPage = null;
        var isLoading = false;
        var isWriting = false;
        var pageNumber = 0;
        bool IsCurrent() => !readToken.IsCancellationRequested && !model.IsTransitioning && HasCapability()
            && model.StoreEpoch == epoch && model.ActiveUserId == caller;
        Button ActionButton(string label, string actionId, Func<Task> action)
        {
            var button = Button(label, action);
            AutomationProperties.SetAutomationId(button, idPrefix + actionId);
            return button;
        }
        var joinLabel = isMessage ? "Acknowledge" : "Join";
        var leaveLabel = "Leave";
        var key = (epoch, caller.GetValueOrDefault(), scope, id);
        // Declared up front because the click lambdas below capture these through the
        // local functions; definite assignment requires them assigned before delegate creation.
        Button refresh = null!, next = null!, join = null!, leave = null!;
        refresh = IconButton("\uE72C", "Refresh", () => LoadPageAsync(false));
        AutomationProperties.SetAutomationId(refresh, idPrefix + "Refresh");
        next = IconButton("\uE76C", "Next page", () => LoadPageAsync(true));
        AutomationProperties.SetAutomationId(next, idPrefix + "Next");
        pager.Children.Add(refresh);
        pager.Children.Add(next);
        join = ActionButton(joinLabel, isMessage ? "Acknowledge" : "Join", () => WriteAsync(isMessage ? null : true));
        leave = ActionButton(leaveLabel, "Leave", () => WriteAsync(false));
        actionRow.Children.Add(join);
        if (!isMessage) actionRow.Children.Add(leave);
        if (caller is null) feedback.Text = "Select a profile first.";
        else feedback.Text = "As " + model.UserLabel(caller.ToString());

        void UpdateActions()
        {
            _pendingPublicActions.TryGetValue(key, out var pending);
            var canWrite = IsCurrent() && caller.HasValue && !isLoading && !isWriting && pending?.IsSending != true;
            join.IsEnabled = canWrite && (pending is null || pending.Joined != false);
            leave.IsEnabled = canWrite && (pending is null || pending.Joined == false);
            join.Content = pending is not null && pending.Joined != false ? "Retry " + joinLabel.ToLowerInvariant() : joinLabel;
            leave.Content = pending?.Joined == false ? "Retry " + leaveLabel.ToLowerInvariant() : leaveLabel;
            refresh.IsEnabled = IsCurrent() && !isLoading && !isWriting;
            next.IsEnabled = refresh.IsEnabled && nextPage is not null;
        }

        async Task LoadPageAsync(bool advance)
        {
            if (!IsCurrent() || isLoading || advance && nextPage is null) return;
            isLoading = true;
            UpdateActions();
            pageStatus.Text = "Loading...";
            try
            {
                var page = await model.GetPublicProfilesAsync(path, advance ? nextPage : null, advance ? snapshot : null, readToken);
                if (!IsCurrent()) return;
                var cursor = page["snapshotCursor"]!.GetValue<string>();
                if (!cursor.StartsWith(epoch + ":", StringComparison.OrdinalIgnoreCase) || (advance && cursor != snapshot))
                    throw new InvalidDataException("The roster snapshot changed. Reconnect or refresh before paging.");
                var rows = ShellViewModel.Objects(page["items"]).Select(item =>
                {
                    var profile = item["profile"]!.AsObject();
                    var displayName = profile["displayName"]!.GetValue<string>();
                    var handle = "@" + profile["handle"]!.GetValue<string>();
                    var meta = isMessage
                        ? handle + " · " + DateTimeOffset.FromUnixTimeMilliseconds(item["acknowledgedUtcMs"]!.GetValue<long>()).ToLocalTime().ToString("g")
                        : handle;
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    row.Children.Add(new TextBlock { Text = displayName, Style = TextStyle("BodyStrongTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center });
                    row.Children.Add(new TextBlock { Text = meta, Style = TextStyle("CaptionTextBlockStyle"), Foreground = TextBrush("TextFillColorSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center });
                    return row;
                }).ToArray();
                list.ItemsSource = rows;
                snapshot = cursor;
                nextPage = page["nextPageToken"]?.GetValue<string>();
                pageNumber = advance ? pageNumber + 1 : 1;
                pageStatus.Text = rows.Length == 0 ? (isMessage ? "No acknowledgements yet." : "No joined profiles.")
                    : $"Page {pageNumber} · {rows.Length} {(rows.Length == 1 ? "profile" : "profiles")}";
            }
            catch (OperationCanceledException) when (readToken.IsCancellationRequested) { }
            catch (Exception error) { if (IsCurrent()) pageStatus.Text = "Could not load this page: " + error.Message; }
            finally { isLoading = false; if (IsCurrent()) UpdateActions(); }
        }

        async Task WriteAsync(bool? joined)
        {
            if (!IsCurrent() || caller is not Guid owner || isWriting || isLoading) return;
            if (!_pendingPublicActions.TryGetValue(key, out var pending))
            {
                if (_pendingPublicActions.Count >= 16)
                {
                    feedback.Text = "Retry an unconfirmed profile action before starting another.";
                    return;
                }
                var data = isMessage
                    ? new JsonObject { ["type"] = "acknowledgement", ["messageId"] = id.ToString() }
                    : new JsonObject { ["type"] = "participation", [scope == PublicScope.Channel ? "channelId" : "sessionId"] = id.ToString(), ["joined"] = joined == true };
                pending = new PendingPublicAction(joined, FrozenCommand.Create(owner, data));
                _pendingPublicActions.Add(key, pending);
            }
            if (pending.IsSending || pending.Joined != joined) return;
            isWriting = pending.IsSending = true;
            feedback.Text = "Waiting for the local store to confirm the action...";
            UpdateActions();
            try
            {
                await model.SubmitPublicProfileCommandAsync(pending.Command, closing.Token);
                _pendingPublicActions.Remove(key);
                var result = isMessage ? "Profile acknowledgement saved." : joined == true ? $"{scope} participation saved." : scope == PublicScope.Channel
                    ? "Direct channel participation removed. Session participation is unchanged." : "Session participation removed. Direct channel participation is unchanged.";
                if (IsCurrent())
                {
                    feedback.Text = result;
                    nextPage = null; // The old continuation cannot show the committed change.
                    await LoadPageAsync(false);
                }
                else ShowNotice(result, InfoBarSeverity.Success);
            }
            catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
            catch (MessagingApiException error) when (error.Status is 400 or 404 or 422)
            {
                _pendingPublicActions.Remove(key);
                if (IsCurrent()) feedback.Text = error.Message;
                else ShowNotice(error.Message, InfoBarSeverity.Error);
            }
            catch (Exception error)
            {
                var warning = "The action was not confirmed. " + error.Message;
                if (model.StoreEpoch == epoch) warning += " Retry the same action as the original profile to reuse its request ID.";
                if (IsCurrent()) feedback.Text = warning;
                else ShowNotice(warning, InfoBarSeverity.Warning);
            }
            finally
            {
                isWriting = pending.IsSending = false;
                if (IsCurrent()) UpdateActions();
            }
        }

        void OnAnchorUnloaded(object sender, RoutedEventArgs args) => flyout.Hide();
        _publicProfilesFlyout?.Hide();
        _publicProfilesFlyout = flyout;
        anchor.Unloaded += OnAnchorUnloaded;
        flyout.Closed += (_, _) =>
        {
            anchor.Unloaded -= OnAnchorUnloaded;
            readLifetime.Cancel();
            readLifetime.Dispose();
            if (ReferenceEquals(_publicProfilesFlyout, flyout)) _publicProfilesFlyout = null;
        };
        flyout.ShowAt(anchor);
        await LoadPageAsync(false);
    }
}
