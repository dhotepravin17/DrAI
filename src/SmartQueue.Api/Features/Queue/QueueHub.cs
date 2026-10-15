using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Common;
using SmartQueue.Api.Features.Family;

namespace SmartQueue.Api.Features.Queue;

/// <summary>
/// Pushes "queue changed" signals for a session. Messages carry no patient data; clients re-read their
/// authorized view through the API (PRD §36). Clients must keep working by polling if the connection drops.
/// </summary>
[Authorize]
public sealed class QueueHub(ClinicAccess clinicAccess, PatientAccess patientAccess) : Hub
{
    public static string Group(Guid sessionId) => $"session:{sessionId}";

    public async Task JoinSession(Guid sessionId)
    {
        var userId = Context.User!.GetUserId();
        var allowed = await clinicAccess.CanOperateSessionAsync(userId, sessionId, Context.ConnectionAborted)
                      || await (await patientAccess.VisibleAppointmentsAsync(userId, Context.ConnectionAborted))
                          .AnyAsync(a => a.QueueSessionId == sessionId, Context.ConnectionAborted);
        if (!allowed) throw new HubException("You cannot follow these appointments.");
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(sessionId));
    }

    public Task LeaveSession(Guid sessionId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(sessionId));
}

public sealed class SignalRQueueEvents(IHubContext<QueueHub> hub) : IQueueEvents
{
    public Task SessionChangedAsync(Guid sessionId) =>
        hub.Clients.Group(QueueHub.Group(sessionId)).SendAsync("queueChanged", new { sessionId });
}
