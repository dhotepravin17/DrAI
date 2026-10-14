using static SmartQueue.Api.Domain.AppointmentStatus;

namespace SmartQueue.Api.Domain;

/// <summary>Allowed appointment transitions (PRD §29.2).</summary>
public static class AppointmentStateMachine
{
    private static readonly HashSet<(AppointmentStatus, AppointmentStatus)> Allowed =
    [
        (Booked, CheckedIn),
        (Booked, Called),
        (CheckedIn, Called),
        (Requeued, Called),
        (Called, InConsultation),
        (Called, Completed),
        (InConsultation, Completed),
        (Called, Missed),
        (Called, NoShow),
        (Missed, Requeued),
        (Missed, NoShow),
        (Booked, Cancelled),
        (CheckedIn, Cancelled),
        (Requeued, Cancelled),
        (Missed, Cancelled),
    ];

    public static bool CanTransition(AppointmentStatus from, AppointmentStatus to) => Allowed.Contains((from, to));
}

/// <summary>Service order (PRD §31.1): emergencies first in the order they were prioritized, then queue sequence.</summary>
public static class QueueOrdering
{
    public static List<Appointment> Waiting(IEnumerable<Appointment> appointments) =>
        appointments.Where(a => a.IsWaiting)
            .OrderBy(a => a.Priority == QueuePriority.Emergency ? 0 : 1)
            .ThenBy(a => a.Priority == QueuePriority.Emergency ? a.PrioritySequence : null)
            .ThenBy(a => a.QueueSequence)
            .ToList();

    /// <summary>Number of eligible patients ahead, or null if the appointment is not waiting.</summary>
    public static int? PatientsAhead(IReadOnlyList<Appointment> waiting, Guid appointmentId)
    {
        for (var i = 0; i < waiting.Count; i++)
            if (waiting[i].Id == appointmentId) return i;
        return null;
    }
}

public readonly record struct WaitEstimate(int MinMinutes, int MaxMinutes);

/// <summary>Approximate waiting time (PRD §31.3).</summary>
public static class WaitEstimator
{
    public const int MinimumSamples = 10;
    public const int SampleSize = 30;

    public static double AverageConsultMinutes(IReadOnlyCollection<double> recentDurations, int configuredAverage)
    {
        if (recentDurations.Count < MinimumSamples) return configuredAverage;
        var sorted = recentDurations.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    public static WaitEstimate Estimate(
        int patientsAhead, bool consultationInProgress, double avgConsultMinutes,
        DateTimeOffset now, DateTimeOffset sessionStartsAt, int delayMinutes)
    {
        var minutes = patientsAhead * avgConsultMinutes;
        if (consultationInProgress) minutes += avgConsultMinutes / 2;
        var untilStart = (sessionStartsAt.AddMinutes(delayMinutes) - now).TotalMinutes;
        if (untilStart > 0) minutes += untilStart;

        var spread = Math.Max(5, minutes * 0.15);
        var low = (int)Math.Max(0, Math.Floor((minutes - spread) / 5) * 5);
        var high = (int)Math.Ceiling((minutes + spread) / 5) * 5;
        return new WaitEstimate(low, Math.Max(high, low + 5));
    }
}
