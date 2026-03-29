using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    [ObservableProperty]
    private string activeSessionLabel = "Ready to monitor meeting";

    [ObservableProperty]
    private string statusBanner = "Load a roster file once, then use Refresh to keep the attendance board current.";

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

    public MainWindowViewModel(BackendApiClient apiClient)
    {
        _apiClient = apiClient;
        SummaryCards = new ObservableCollection<SummaryMetricViewModel>();
        Participants = new ObservableCollection<ParticipantItemViewModel>();
        ConfidenceBuckets = new ObservableCollection<ConfidenceBucketViewModel>();
        RecentEvents = new ObservableCollection<ActivityEventViewModel>();
        ReviewQueue = new ObservableCollection<ReviewQueueItemViewModel>();
        RosterOptions = new ObservableCollection<RosterOptionViewModel>();
    }

    public string HeaderTitle { get; } = "Attendance dashboard";

    public string HeaderSubtitle { get; } = "Simple live workflow: load roster, refresh the meeting board, then review only the people who are not fully verified.";

    public string LiveCoverageLabel { get; private set; } = "No live rostered participants yet";

    public string QueueStatus { get; private set; } = "No review items yet";

    public ObservableCollection<SummaryMetricViewModel> SummaryCards { get; }

    public ObservableCollection<ParticipantItemViewModel> Participants { get; }

    public ObservableCollection<ConfidenceBucketViewModel> ConfidenceBuckets { get; }

    public ObservableCollection<ActivityEventViewModel> RecentEvents { get; }

    public ObservableCollection<ReviewQueueItemViewModel> ReviewQueue { get; }

    public ObservableCollection<RosterOptionViewModel> RosterOptions { get; }

    public bool CanSaveAlias => SelectedReviewItem is not null && SelectedRosterOption is not null && !string.IsNullOrWhiteSpace(SelectedAliasText) && !IsBusy;

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

    [RelayCommand]
    private async Task InitializeAsync()
    {
        await SafeExecuteAsync(async () =>
        {
            var healthy = await _apiClient.IsHealthyAsync();
            StatusBanner = healthy
                ? "Backend connected. Load the roster file to begin live attendance tracking."
                : "Backend is not reachable. Start the API first, then refresh.";

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
            StatusBanner = $"Roster imported from {Path.GetFileName(RosterFilePath)}. Live board refreshed next.";
            await RefreshAsync();
        });
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await SafeExecuteAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(MeetingId))
            {
                throw new InvalidOperationException("Enter a meeting ID before refreshing.");
            }

            var board = await _apiClient.GetBoardAsync(MeetingId);
            ApplyBoard(board);
            ActiveSessionLabel = $"Meeting {MeetingId} · updated {DateTime.Now:h:mm tt}";
            StatusBanner = $"Showing {Participants.Count} tracked people and {ReviewQueue.Count} review items.";
        });
    }

    [RelayCommand]
    private async Task SeedDemoAsync()
    {
        await SafeExecuteAsync(async () =>
        {
            await _apiClient.SeedDemoAsync(MeetingId);
            StatusBanner = "Demo join/leave activity added so you can validate the board quickly.";
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
            StatusBanner = $"Saved alias '{SelectedAliasText}' to {SelectedRosterOption.DisplayName}.";
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
    }

    private void ApplyBoard(AttendanceBoard board)
    {
        var presentCount = board.People.Count(person => person.AttendanceState == AttendanceState.Present);
        var verifiedCount = board.People.Count(person => person.Confidence is MatchConfidence.Verified or MatchConfidence.AliasVerified);
        var attentionCount = board.People.Count(person => person.Confidence is MatchConfidence.NameOnly or MatchConfidence.Possible)
            + board.UnmatchedParticipants.Count;

        LiveCoverageLabel = presentCount == 0
            ? "No rostered attendees currently marked present"
            : $"{presentCount} rostered attendees currently present";
        QueueStatus = attentionCount == 0 ? "Nothing needs review" : $"{attentionCount} items need review";
        OnPropertyChanged(nameof(LiveCoverageLabel));
        OnPropertyChanged(nameof(QueueStatus));

        ReplaceWith(SummaryCards, new[]
        {
            new SummaryMetricViewModel("Roster total", board.People.Count.ToString(), "Loaded expected attendees from the selected roster baseline.", VerifiedBrush),
            new SummaryMetricViewModel("Live in meeting", presentCount.ToString(), "People currently marked present on the live board.", NameOnlyBrush),
            new SummaryMetricViewModel("Verified now", verifiedCount.ToString(), "Attendance-ready matches with strong identity evidence.", VerifiedBrush),
            new SummaryMetricViewModel("Needs attention", attentionCount.ToString(), "Name-only, possible, and unmatched people needing operator review.", attentionCount == 0 ? VerifiedBrush : ReviewBrush),
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
