using Avalonia.Media;

namespace ZoomCheck.App.ViewModels;

public sealed class SummaryMetricViewModel
{
    public SummaryMetricViewModel(string label, string value, string detail, IBrush accentBrush)
    {
        Label = label;
        Value = value;
        Detail = detail;
        AccentBrush = accentBrush;
    }

    public string Label { get; }

    public string Value { get; }

    public string Detail { get; }

    public IBrush AccentBrush { get; }
}

public sealed class ParticipantItemViewModel
{
    public ParticipantItemViewModel(
        string displayName,
        string rosterContext,
        string zoomName,
        string matchContext,
        string confidenceLabel,
        string confidenceDetail,
        IBrush accentBrush,
        string joinTime,
        string duration)
    {
        DisplayName = displayName;
        RosterContext = rosterContext;
        ZoomName = zoomName;
        MatchContext = matchContext;
        ConfidenceLabel = confidenceLabel;
        ConfidenceDetail = confidenceDetail;
        AccentBrush = accentBrush;
        JoinTime = joinTime;
        Duration = duration;
    }

    public string DisplayName { get; }

    public string RosterContext { get; }

    public string ZoomName { get; }

    public string MatchContext { get; }

    public string ConfidenceLabel { get; }

    public string ConfidenceDetail { get; }

    public IBrush AccentBrush { get; }

    public string JoinTime { get; }

    public string Duration { get; }
}

public sealed class ReviewQueueItemViewModel
{
    public ReviewQueueItemViewModel(
        string participantName,
        string suggestion,
        string confidenceLabel,
        string detail,
        IBrush accentBrush,
        bool isUnmatched,
        string aliasText)
    {
        ParticipantName = participantName;
        Suggestion = suggestion;
        ConfidenceLabel = confidenceLabel;
        Detail = detail;
        AccentBrush = accentBrush;
        IsUnmatched = isUnmatched;
        AliasText = aliasText;
    }

    public string ParticipantName { get; }

    public string Suggestion { get; }

    public string ConfidenceLabel { get; }

    public string Detail { get; }

    public IBrush AccentBrush { get; }

    public bool IsUnmatched { get; }

    public string AliasText { get; }
}

public sealed class RosterOptionViewModel
{
    public RosterOptionViewModel(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }

    public string Id { get; }

    public string DisplayName { get; }
}

public sealed class ConfidenceBucketViewModel
{
    public ConfidenceBucketViewModel(string label, string count, string detail, IBrush accentBrush)
    {
        Label = label;
        Count = count;
        Detail = detail;
        AccentBrush = accentBrush;
    }

    public string Label { get; }

    public string Count { get; }

    public string Detail { get; }

    public IBrush AccentBrush { get; }
}

public sealed class ActivityEventViewModel
{
    public ActivityEventViewModel(string title, string detail, string timestamp, IBrush accentBrush)
    {
        Title = title;
        Detail = detail;
        Timestamp = timestamp;
        AccentBrush = accentBrush;
    }

    public string Title { get; }

    public string Detail { get; }

    public string Timestamp { get; }

    public IBrush AccentBrush { get; }
}
