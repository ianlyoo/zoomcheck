using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZoomCheck.App.Models;
using ZoomCheck.App.Services;
using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;

namespace ZoomCheck.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private static readonly IBrush VerifiedBrush = CreateBrush("#35C689");
    private static readonly IBrush AliasBrush = CreateBrush("#6FD3A4");
    private static readonly IBrush NameOnlyBrush = CreateBrush("#67A6FF");
    private static readonly IBrush ReviewBrush = CreateBrush("#F3B84F");
    private static readonly IBrush UnmatchedBrush = CreateBrush("#F36F7F");
    private readonly BackendApiClient _apiClient;
    private readonly BackendBootstrapper _bootstrapper;
    private readonly IParticipantPanelWatcher _participantPanelWatcher;
    private CancellationTokenSource? _autoRefreshCts;

    [ObservableProperty]
    private string activeSessionLabel = "Session not started";

    [ObservableProperty]
    private string statusBanner = "Detect a live meeting or enter a meeting ID, then load the roster and refresh the board.";

    [ObservableProperty]
    private string backendUrl = "http://127.0.0.1:5078/";

    [ObservableProperty]
    private string meetingId = "demo-meeting";

    [ObservableProperty]
    private string rosterFilePath = DetectDefaultRosterPath();

    [ObservableProperty]
    private string selectedAliasText = string.Empty;

    [ObservableProperty]
    private ReviewQueueItemViewModel? selectedReviewItem;

    [ObservableProperty]
    private RosterOptionViewModel? selectedRosterOption;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool autoRefreshEnabled;

    [ObservableProperty]
    private int selectedAutoRefreshIntervalSeconds = 5;

    [ObservableProperty]
    private LiveMeetingOptionViewModel? selectedLiveMeeting;

    [ObservableProperty]
    private string participantPanelStatusText = "Zoom participant panel not attached";

    [ObservableProperty]
    private bool isParticipantPanelAttached;

    [ObservableProperty]
    private bool isParticipantPanelMonitoring;

    [ObservableProperty]
    private int visiblePanelParticipantCount;

    public MainWindowViewModel()
        : this(
            new BackendApiClient(new System.Net.Http.HttpClient { BaseAddress = new Uri("http://127.0.0.1:5078/") }),
            new BackendBootstrapper(new System.Net.Http.HttpClient { BaseAddress = new Uri("http://127.0.0.1:5078/") }, new Models.AppRuntimeOptions()),
            new NoOpParticipantPanelWatcher())
    {
    }

    public MainWindowViewModel(BackendApiClient apiClient, BackendBootstrapper bootstrapper, IParticipantPanelWatcher participantPanelWatcher)
    {
        _apiClient = apiClient;
        _bootstrapper = bootstrapper;
        _participantPanelWatcher = participantPanelWatcher;
        SummaryCards = new ObservableCollection<SummaryMetricViewModel>();
        Participants = new ObservableCollection<ParticipantItemViewModel>();
        ConfidenceBuckets = new ObservableCollection<ConfidenceBucketViewModel>();
        RecentEvents = new ObservableCollection<ActivityEventViewModel>();
        ReviewQueue = new ObservableCollection<ReviewQueueItemViewModel>();
        RosterOptions = new ObservableCollection<RosterOptionViewModel>();
        LiveMeetings = new ObservableCollection<LiveMeetingOptionViewModel>();

        _participantPanelWatcher.StatusChanged += OnParticipantPanelStatusChanged;
    }

    public string HeaderTitle { get; } = "Attendance workspace";

    public string HeaderSubtitle { get; } = "Keep the Zoom participant panel open on the host or co-host PC. ZoomCheck watches that panel, compares names to the roster, and keeps the review queue current.";

    public string LiveCoverageLabel { get; private set; } = "Waiting for your first meeting refresh";

    public string QueueStatus { get; private set; } = "Nothing flagged yet";

    public ObservableCollection<SummaryMetricViewModel> SummaryCards { get; }

    public ObservableCollection<ParticipantItemViewModel> Participants { get; }

    public ObservableCollection<ConfidenceBucketViewModel> ConfidenceBuckets { get; }

    public ObservableCollection<ActivityEventViewModel> RecentEvents { get; }

    public ObservableCollection<ReviewQueueItemViewModel> ReviewQueue { get; }

    public ObservableCollection<RosterOptionViewModel> RosterOptions { get; }

    public ObservableCollection<LiveMeetingOptionViewModel> LiveMeetings { get; }

    public IReadOnlyList<int> AutoRefreshIntervalOptions { get; } = new[] { 3, 5 };

    public bool HasParticipants => Participants.Count > 0;

    public bool ShowParticipantEmptyState => !HasParticipants;

    public bool HasReviewQueue => ReviewQueue.Count > 0;

    public bool ShowReviewQueueEmptyState => !HasReviewQueue;

    public bool HasRecentEvents => RecentEvents.Count > 0;

    public bool ShowRecentEventsEmptyState => !HasRecentEvents;

    public bool CanSaveAlias => SelectedReviewItem is not null && SelectedRosterOption is not null && !string.IsNullOrWhiteSpace(SelectedAliasText) && !IsBusy;

    public bool HasLiveMeetingCandidates => LiveMeetings.Count > 0;

    public string LiveMeetingStatus => !HasLiveMeetingCandidates
        ? "No live Zoom meeting has been detected yet."
        : LiveMeetings.Count == 1
            ? $"1 live Zoom meeting detected. ZoomCheck can attach to it automatically."
            : $"{LiveMeetings.Count} live Zoom meetings detected. Choose the one you want to track.";

    public string ParticipantPanelSummary => !IsParticipantPanelAttached
        ? "Attach the visible Zoom participant panel first."
        : IsParticipantPanelMonitoring
            ? $"Watching the participant panel now · {VisiblePanelParticipantCount} visible name(s) in the latest scan."
            : $"Panel attached · {VisiblePanelParticipantCount} visible name(s) in the last scan.";

    public string AutoRefreshStatus => AutoRefreshEnabled
        ? $"Auto refresh is on every {SelectedAutoRefreshIntervalSeconds} seconds while this window stays open."
        : "Auto refresh is off. Turn it on to keep checking the live board automatically.";

    partial void OnSelectedReviewItemChanged(ReviewQueueItemViewModel? value)
    {
        SelectedAliasText = value?.AliasText ?? string.Empty;
        OnPropertyChanged(nameof(CanSaveAlias));
    }

    partial void OnSelectedRosterOptionChanged(RosterOptionViewModel? value)
    {
        OnPropertyChanged(nameof(CanSaveAlias));
    }

    partial void OnSelectedAliasTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanSaveAlias));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSaveAlias));
    }

    partial void OnAutoRefreshEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(AutoRefreshStatus));
        _ = RestartAutoRefreshLoopAsync();
    }

    partial void OnSelectedAutoRefreshIntervalSecondsChanged(int value)
    {
        OnPropertyChanged(nameof(AutoRefreshStatus));
        if (AutoRefreshEnabled)
        {
            _ = RestartAutoRefreshLoopAsync();
        }
    }

    partial void OnSelectedLiveMeetingChanged(LiveMeetingOptionViewModel? value)
    {
        if (value is not null)
        {
            MeetingId = value.MeetingId;
            OnPropertyChanged(nameof(LiveMeetingStatus));
        }
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        await SafeExecuteAsync(async () =>
        {
            StatusBanner = "Starting ZoomCheck local service...";
            var bootstrap = await _bootstrapper.EnsureBackendAvailableAsync();
            if (!bootstrap.Success)
            {
                StatusBanner = bootstrap.Message;
                return;
            }

            var healthy = await _apiClient.IsHealthyAsync();
            BackendUrl = _apiClient.BackendBaseUrl;
            StatusBanner = healthy
                ? "Ready. Open or join Zoom, confirm the meeting ID shown in Zoom, load the roster once, then press Refresh."
                : "ZoomCheck is still connecting to its local helper service. Wait a moment or reopen the app, then press Refresh.";

            if (healthy)
            {
                await LoadRosterOptionsAsync();
                if (RosterOptions.Count > 0)
                {
                    await RefreshAsync();
                }
            }
        });
    }

    [RelayCommand]
    private async Task LoadRosterAsync()
    {
        await SafeExecuteAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(RosterFilePath) || !File.Exists(RosterFilePath))
            {
                throw new InvalidOperationException("Choose a valid roster Excel file first.");
            }

            await _apiClient.ImportRosterAsync(RosterFilePath);
            await LoadRosterOptionsAsync();
            ActiveSessionLabel = $"Roster loaded · {RosterOptions.Count} people ready";
            StatusBanner = $"Roster loaded from {Path.GetFileName(RosterFilePath)} with {RosterOptions.Count} people. Attach the Zoom participant panel, then start a scan or turn on watching.";
        });
    }

    [RelayCommand]
    private async Task DetectLiveMeetingAsync()
    {
        await SafeExecuteAsync(async () =>
        {
            var result = await _participantPanelWatcher.AttachAsync();
            StatusBanner = result.Message;
            if (result.Success)
            {
                ActiveSessionLabel = "Participant panel attached";
            }
        });
    }

    [RelayCommand]
    private async Task StartWatchingPanelAsync()
    {
        await SafeExecuteAsync(async () =>
        {
            var result = await _participantPanelWatcher.StartMonitoringAsync(MeetingId);
            StatusBanner = result.Message;
            if (result.Success && !AutoRefreshEnabled)
            {
                AutoRefreshEnabled = true;
            }
            await RefreshBoardCoreAsync(updateStatusBanner: false);
        });
    }

    [RelayCommand]
    private async Task StopWatchingPanelAsync()
    {
        await SafeExecuteAsync(async () =>
        {
            await _participantPanelWatcher.StopMonitoringAsync();
            StatusBanner = "Participant panel watching stopped. You can still scan manually or refresh the board.";
        });
    }

    [RelayCommand]
    private async Task ScanParticipantPanelAsync()
    {
        await SafeExecuteAsync(async () =>
        {
            var result = await _participantPanelWatcher.ScanOnceAsync(MeetingId);
            StatusBanner = result.Message;
            if (result.Success)
            {
                await RefreshBoardCoreAsync(updateStatusBanner: false);
            }
        });
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await SafeExecuteAsync(async () =>
        {
            await RefreshBoardCoreAsync(updateStatusBanner: true);
        });
    }

    [RelayCommand]
    private async Task SeedDemoAsync()
    {
        await SafeExecuteAsync(async () =>
        {
            await _apiClient.SeedDemoAsync(MeetingId);
            StatusBanner = "Demo join/leave activity added so you can practice the class workflow before a live session.";
            await RefreshAsync();
        });
    }

    [RelayCommand]
    private async Task SaveAliasAsync()
    {
        await SafeExecuteAsync(async () =>
        {
            if (SelectedReviewItem is null || SelectedRosterOption is null)
            {
                throw new InvalidOperationException("Select a review item and a roster person first.");
            }

            await _apiClient.SaveAliasAsync(SelectedAliasText, SelectedRosterOption.Id, $"Operator confirmed alias for {SelectedReviewItem.ParticipantName}");
            StatusBanner = $"Saved alias '{SelectedAliasText}' to {SelectedRosterOption.DisplayName}. ZoomCheck will use that match again on the next refresh.";
            await RefreshAsync();
        });
    }

    public async Task<string> GetExportCsvAsync()
    {
        if (string.IsNullOrWhiteSpace(MeetingId))
        {
            throw new InvalidOperationException("Enter a meeting ID before exporting.");
        }

        return await _apiClient.ExportCsvAsync(MeetingId);
    }

    private async Task LoadRosterOptionsAsync()
    {
        var roster = await _apiClient.GetRosterAsync();
        ReplaceWith(RosterOptions, roster.Select(person => new RosterOptionViewModel(person.Id, $"{person.Sequence}. {person.Name} · {person.Organization}")));
        SelectedRosterOption ??= RosterOptions.FirstOrDefault();
        NotifyDashboardStateProperties();
    }

    private void ApplyBoard(AttendanceBoard board)
    {
        var presentCount = board.People.Count(person => person.AttendanceState == AttendanceState.Present);
        var verifiedCount = board.People.Count(person => person.Confidence is MatchConfidence.Verified or MatchConfidence.AliasVerified);
        var attentionCount = board.People.Count(person => person.Confidence is MatchConfidence.NameOnly or MatchConfidence.Possible)
            + board.UnmatchedParticipants.Count;

        LiveCoverageLabel = presentCount == 0
            ? "Nobody has been marked present yet — open or join Zoom and press Refresh"
            : $"{presentCount} rostered attendee{(presentCount == 1 ? string.Empty : "s")} currently present";
        QueueStatus = attentionCount == 0
            ? "No flagged people right now"
            : attentionCount == 1
                ? "1 flagged person needs review"
                : $"{attentionCount} flagged people need review";
        OnPropertyChanged(nameof(LiveCoverageLabel));
        OnPropertyChanged(nameof(QueueStatus));

        ReplaceWith(SummaryCards, new[]
        {
            new SummaryMetricViewModel("Roster loaded", board.People.Count.ToString(), "Expected people from the class roster you loaded for this meeting.", VerifiedBrush),
            new SummaryMetricViewModel("Seen in Zoom", presentCount.ToString(), "People currently marked present after the latest refresh.", NameOnlyBrush),
            new SummaryMetricViewModel("Ready to count", verifiedCount.ToString(), "Strong matches you can usually trust without extra review.", VerifiedBrush),
            new SummaryMetricViewModel("Flagged to check", attentionCount.ToString(), "Name-only, review, and unmatched people that still need a human look.", attentionCount == 0 ? VerifiedBrush : ReviewBrush),
        });

        ReplaceWith(ConfidenceBuckets, Enum.GetValues<MatchConfidence>().Select(confidence =>
        {
            var count = board.ConfidenceCounts.TryGetValue(confidence, out var value) ? value : 0;
            return new ConfidenceBucketViewModel(ConfidenceToLabel(confidence), count.ToString(), ConfidenceDescription(confidence), ConfidenceBrush(confidence));
        }));

        ReplaceWith(Participants, board.People
            .Where(person => person.JoinCount > 0 || person.AttendanceState != AttendanceState.NotJoined)
            .Select(person => new ParticipantItemViewModel(
                person.Name,
                $"Roster · {person.Sequence} · {person.Organization}",
                person.Name,
                person.ConfidenceReason,
                ConfidenceToLabel(person.Confidence),
                AttendanceDescription(person),
                ConfidenceBrush(person.Confidence),
                person.LastJoinedAt?.ToLocalTime().ToString("M/d h:mm tt") ?? "Not joined",
                BuildDurationText(person)))
            .OrderByDescending(item => item.JoinTime)
            .ToArray());

        ReplaceWith(RecentEvents, board.RecentEvents.Select(evt => new ActivityEventViewModel(
            $"{EventTitle(evt)} · {evt.ParticipantName}",
            $"{ConfidenceToLabel(evt.Confidence)} · source {evt.Source}",
            evt.OccurredAt.ToLocalTime().ToString("M/d h:mm tt"),
            ConfidenceBrush(evt.Confidence))));

        var reviewPeople = board.People
            .Where(person => person.Confidence is MatchConfidence.NameOnly or MatchConfidence.Possible)
            .Select(person => new ReviewQueueItemViewModel(
                person.Name,
                $"Suggested roster match: {person.Name}",
                ConfidenceToLabel(person.Confidence),
                person.ConfidenceReason,
                ConfidenceBrush(person.Confidence),
                false,
                person.Name));

        var unmatchedPeople = board.UnmatchedParticipants.Select(person => new ReviewQueueItemViewModel(
            person.ParticipantName,
            "No roster match yet",
            "Unmatched",
            $"Seen {person.EventCount} time(s), last activity {person.LastSeenAt.ToLocalTime():M/d h:mm tt}",
            UnmatchedBrush,
            true,
            person.ParticipantName));

        ReplaceWith(ReviewQueue, reviewPeople.Concat(unmatchedPeople));
        if (SelectedReviewItem is null || !ReviewQueue.Contains(SelectedReviewItem))
        {
            SelectedReviewItem = ReviewQueue.FirstOrDefault();
        }

        NotifyDashboardStateProperties();
    }

    private async Task SafeExecuteAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            await action();
        }
        catch (Exception ex)
        {
            StatusBanner = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshBoardCoreAsync(bool updateStatusBanner)
    {
        if (string.IsNullOrWhiteSpace(MeetingId))
        {
            throw new InvalidOperationException("Enter the meeting ID from Zoom before refreshing.");
        }

        var board = await _apiClient.GetBoardAsync(MeetingId);
        ApplyBoard(board);
        ActiveSessionLabel = $"Meeting {MeetingId} · last checked {DateTime.Now:h:mm tt}";

        if (updateStatusBanner)
        {
            StatusBanner = BuildRefreshStatusMessage();
        }
    }

    private async Task RestartAutoRefreshLoopAsync()
    {
        StopAutoRefreshLoop();

        if (!AutoRefreshEnabled)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _autoRefreshCts = cts;

        try
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(SelectedAutoRefreshIntervalSeconds), cts.Token);

                if (cts.IsCancellationRequested)
                {
                    break;
                }

                if (IsBusy || string.IsNullOrWhiteSpace(MeetingId))
                {
                    continue;
                }

                try
                {
                    await RefreshBoardCoreAsync(updateStatusBanner: false);
                }
                catch
                {
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StopAutoRefreshLoop()
    {
        if (_autoRefreshCts is null)
        {
            return;
        }

        _autoRefreshCts.Cancel();
        _autoRefreshCts.Dispose();
        _autoRefreshCts = null;
    }

    private void OnParticipantPanelStatusChanged(object? sender, ParticipantPanelStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ParticipantPanelStatusText = status.Message;
            IsParticipantPanelAttached = status.IsAttached;
            IsParticipantPanelMonitoring = status.IsMonitoring;
            VisiblePanelParticipantCount = status.VisibleParticipantCount;
            OnPropertyChanged(nameof(ParticipantPanelSummary));
        });
    }

    private static string BuildDurationText(BoardPersonStatus person)
    {
        if (person.LastJoinedAt is null)
        {
            return "Awaiting first join";
        }

        var end = person.LastLeftAt ?? DateTimeOffset.UtcNow;
        var duration = end - person.LastJoinedAt.Value;
        return $"{Math.Max(duration.TotalMinutes, 0):0} min tracked · joins {person.JoinCount}";
    }

    private static string AttendanceDescription(BoardPersonStatus person)
    {
        return person.AttendanceState switch
        {
            AttendanceState.Present => "Currently present",
            AttendanceState.Left => "Previously joined, currently left",
            _ => "Not observed in meeting"
        };
    }

    private string BuildRefreshStatusMessage()
    {
        if (RosterOptions.Count > 0 && Participants.Count == 0)
        {
            return $"Roster is loaded with {RosterOptions.Count} people. Attach the Zoom participant panel and scan it to bring current attendees into the board.";
        }

        if (Participants.Count == 0)
        {
            return "No one has been pulled in yet. Keep the Zoom participant panel visible, scan or start watching, then refresh again.";
        }

        if (ReviewQueue.Count == 0)
        {
            return "Board updated. No one is flagged right now, so you can keep refreshing during class and export when you are done.";
        }

        return $"Board updated. Review the {ReviewQueue.Count} flagged {(ReviewQueue.Count == 1 ? "person" : "people")} on the right, then export when class ends.";
    }

    private void NotifyDashboardStateProperties()
    {
        OnPropertyChanged(nameof(HasParticipants));
        OnPropertyChanged(nameof(ShowParticipantEmptyState));
        OnPropertyChanged(nameof(HasReviewQueue));
        OnPropertyChanged(nameof(ShowReviewQueueEmptyState));
        OnPropertyChanged(nameof(HasRecentEvents));
        OnPropertyChanged(nameof(ShowRecentEventsEmptyState));
    }

    private static string EventTitle(ParticipantEvent participantEvent)
    {
        return participantEvent.EventType == ParticipantEventType.Joined ? "Join detected" : "Leave detected";
    }

    private static string ConfidenceToLabel(MatchConfidence confidence)
    {
        return confidence switch
        {
            MatchConfidence.AliasVerified => "Alias-verified",
            MatchConfidence.NameOnly => "Name-only",
            MatchConfidence.Possible => "Review",
            _ => confidence.ToString()
        };
    }

    private static string ConfidenceDescription(MatchConfidence confidence)
    {
        return confidence switch
        {
            MatchConfidence.Verified => "Email or another strong identity signal aligned with the roster.",
            MatchConfidence.AliasVerified => "An operator-saved alias confirms this participant quickly next time.",
            MatchConfidence.NameOnly => "Name alignment is usable but still deserves quick visual confirmation.",
            MatchConfidence.Possible => "Weak similarity only. Keep this in the review queue.",
            _ => "No safe roster match exists yet. Manual review is required."
        };
    }

    private static IBrush ConfidenceBrush(MatchConfidence confidence)
    {
        return confidence switch
        {
            MatchConfidence.Verified => VerifiedBrush,
            MatchConfidence.AliasVerified => AliasBrush,
            MatchConfidence.NameOnly => NameOnlyBrush,
            MatchConfidence.Possible => ReviewBrush,
            _ => UnmatchedBrush
        };
    }

    private static void ReplaceWith<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private static IBrush CreateBrush(string hexColor)
    {
        return new SolidColorBrush(Color.Parse(hexColor));
    }

    private static string DetectDefaultRosterPath()
    {
        try
        {
            var currentDirectory = Directory.GetCurrentDirectory();
            return Directory.EnumerateFiles(currentDirectory, "*.xlsx", SearchOption.TopDirectoryOnly).FirstOrDefault() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
