using System;
using System.Globalization;
using System.Windows.Input;
using EverLogger.App.Helpers;

namespace EverLogger.App.ViewModels;

/// <summary>
/// Severity level for non-blocking in-app notification popups.
/// </summary>
public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// View model representing a single non-blocking notification popup card.
/// </summary>
public sealed class AppNotification : ViewModelBase
{
    public Guid Id { get; } = Guid.NewGuid();

    public NotificationSeverity Severity { get; }

    public string Title { get; private set; }

    public string DisplayTitle => OccurrenceCount > 1 ? $"{Title} (×{OccurrenceCount})" : Title;

    public string Message { get; private set; }

    public string? SourcePort { get; }

    public string? CoalesceKey { get; }

    public int OccurrenceCount { get; private set; } = 1;

    public DateTime Timestamp { get; private set; }

    public string TimestampText => Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public bool AutoDismiss { get; }

    public DateTime? ExpiresAtUtc { get; private set; }

    public ICommand DismissCommand { get; }

    public AppNotification(
        NotificationSeverity severity,
        string title,
        string message,
        string? sourcePort,
        string? coalesceKey,
        bool autoDismiss,
        TimeSpan? autoDismissAfter,
        Action<AppNotification> dismissAction,
        DateTime? timestamp = null,
        DateTime? utcNow = null)
    {
        Severity = severity;
        Title = title ?? string.Empty;
        Message = message ?? string.Empty;
        SourcePort = sourcePort;
        CoalesceKey = coalesceKey;
        AutoDismiss = autoDismiss;
        Timestamp = timestamp ?? DateTime.Now;
        ExpiresAtUtc = autoDismiss && autoDismissAfter.HasValue
            ? (utcNow ?? DateTime.UtcNow) + autoDismissAfter.Value
            : null;
        ArgumentNullException.ThrowIfNull(dismissAction);
        DismissCommand = new RelayCommand(() => dismissAction(this));
    }

    /// <summary>
    /// Merges a repeated notification with the same coalesce key into this card.
    /// </summary>
    public void RecordOccurrence(
        string title,
        string message,
        DateTime timestamp,
        DateTime utcNow,
        TimeSpan? autoDismissAfter)
    {
        OccurrenceCount++;
        Title = title ?? string.Empty;
        Message = message ?? string.Empty;
        Timestamp = timestamp;
        if (AutoDismiss && autoDismissAfter.HasValue)
        {
            ExpiresAtUtc = utcNow + autoDismissAfter.Value;
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(OccurrenceCount));
        OnPropertyChanged(nameof(Timestamp));
        OnPropertyChanged(nameof(TimestampText));
    }
}
