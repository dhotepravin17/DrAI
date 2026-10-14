using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Common;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Family;

public enum PersonKind
{
    Self,
    FamilyProfile,
    LinkedAccount,
}

public sealed record Person(Patient Patient, PersonKind Kind, Relationship Relationship, Guid? LinkId);

/// <summary>
/// Who a signed-in account may book for and whose visits it may see (PRD FR-04, FR-17, §12.3):
/// <list type="bullet">
/// <item>Own record and linked adults (who accepted an invitation): all visits.</item>
/// <item>Family profiles of minors: all visits.</item>
/// <item>Family profiles of adults: only visits this account booked, plus visits before their 18th birthday.</item>
/// </list>
/// </summary>
public sealed class PatientAccess(AppDbContext db, TimeProvider clock)
{
    public async Task<List<Person>> PeopleAsync(Guid userId, CancellationToken ct)
    {
        var own = await db.Patients.AsNoTracking()
            .Where(p => p.ArchivedAt == null && (p.UserId == userId || (p.ManagedByUserId == userId && p.UserId == null)))
            .OrderBy(p => p.UserId == userId ? 0 : 1).ThenBy(p => p.CreatedAt)
            .ToListAsync(ct);

        var links = await (from l in db.PatientLinks.AsNoTracking()
                           where l.ViewerUserId == userId && l.Status == InvitationStatus.Active && l.PatientId != null
                           join p in db.Patients on l.PatientId equals p.Id
                           select new { Link = l, Patient = p }).ToListAsync(ct);

        return own.Select(p => new Person(p, p.UserId == userId ? PersonKind.Self : PersonKind.FamilyProfile, p.Relationship, null))
            .Concat(links.Select(x => new Person(x.Patient, PersonKind.LinkedAccount, x.Link.Relationship, x.Link.Id)))
            .ToList();
    }

    public async Task<Patient> RequireBookableAsync(Guid userId, Guid patientId, CancellationToken ct) =>
        (await PeopleAsync(userId, ct)).FirstOrDefault(p => p.Patient.Id == patientId)?.Patient
        ?? throw ApiException.NotFound("You cannot book for this person. Add them as a family member first.");

    /// <summary>Appointments this account may see. Applied in SQL, never filtered in the browser.</summary>
    public async Task<IQueryable<Appointment>> VisibleAppointmentsAsync(Guid userId, CancellationToken ct)
    {
        var people = await PeopleAsync(userId, ct);
        var today = ClinicTime.Today(clock);

        var fullAccess = people
            .Where(p => p.Kind != PersonKind.FamilyProfile || p.Patient.IsMinorOn(today))
            .Select(p => p.Patient.Id).ToList();

        Expression<Func<Appointment, bool>> predicate = a => fullAccess.Contains(a.PatientId);
        foreach (var adult in people.Where(p => p.Kind == PersonKind.FamilyProfile && !p.Patient.IsMinorOn(today)))
        {
            var id = adult.Patient.Id;
            var eighteenth = adult.Patient.DateOfBirth?.AddYears(18) ?? DateOnly.MinValue;
            predicate = predicate.OrElse(a => a.PatientId == id && (a.BookedByUserId == userId || a.Date < eighteenth));
        }
        return db.Appointments.AsNoTracking().Where(predicate);
    }

    public async Task<bool> CanSeeAppointmentAsync(Guid userId, Guid appointmentId, CancellationToken ct) =>
        await (await VisibleAppointmentsAsync(userId, ct)).AnyAsync(a => a.Id == appointmentId, ct);
}

internal static class PredicateExtensions
{
    public static Expression<Func<T, bool>> OrElse<T>(this Expression<Func<T, bool>> left, Expression<Func<T, bool>> right)
    {
        var parameter = left.Parameters[0];
        var rightBody = new Rebind(right.Parameters[0], parameter).Visit(right.Body);
        return Expression.Lambda<Func<T, bool>>(Expression.OrElse(left.Body, rightBody), parameter);
    }

    private sealed class Rebind(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
