using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Features.Analytics;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Doctors;

/// <summary>FR-06 search and FR-07 public profile. Only verified doctors are listed; private verification data is never returned.</summary>
public static partial class DirectoryEndpoints
{
    public const int PageSize = 20;

    public sealed record ClinicDto(string Name, string Address, string? PublicPhone);
    public sealed record DoctorCard(
        string DoctorCode, string FullName, string Specialty, string? Qualifications,
        string CityCode, string CityName, ClinicDto? Clinic, bool IsVerified, bool AcceptingBookings);
    public sealed record SearchResponse(IReadOnlyList<DoctorCard> Items, int Page, int PageSize, int Total);

    public static RouteGroupBuilder MapDirectoryEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/doctors").WithTags("Doctor directory");
        group.MapGet("/search", Search).RequireRateLimiting(RateLimitPolicies.Search);
        group.MapGet("/{doctorCode}", GetProfile);
        return api;
    }

    private static async Task<IResult> Search(
        AppDbContext db, ProductAnalytics analytics, string? q, string? city, string? specialty, int? page, CancellationToken ct)
    {
        var pageNumber = Math.Max(1, page ?? 1);
        var query = Project(db);

        if (!string.IsNullOrWhiteSpace(city))
        {
            var cityCode = city.Trim().ToUpperInvariant();
            query = query.Where(x => x.CityCode == cityCode);
        }
        if (!string.IsNullOrWhiteSpace(specialty))
        {
            var s = specialty.Trim();
            query = query.Where(x => x.Specialty == s);
        }

        var term = q?.Trim() ?? "";
        if (term.Length > 100) term = term[..100];
        if (term.Length > 0)
        {
            var nameTerm = DoctorPrefix().Replace(term, "");
            if (nameTerm.Length == 0) nameTerm = term;
            query = query.Where(x =>
                x.DoctorCode == term ||
                x.FullName.Contains(nameTerm) ||
                x.Specialty.Contains(term) ||
                x.CityName.Contains(term));
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(x => x.DoctorCode == term)
            .ThenBy(x => x.FullName)
            .Skip((pageNumber - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync(ct);

        var queryType = term.Length == 0 ? (city is not null ? "city" : specialty is not null ? "specialty" : "browse")
            : rows.Any(r => r.DoctorCode == term) ? "id" : "name";
        analytics.Track(ProductEventNames.DoctorSearched, new { queryType, resultCount = total }, queryType, total);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new SearchResponse(rows.Select(ToCard).ToList(), pageNumber, PageSize, total));
    }

    private static async Task<IResult> GetProfile(string doctorCode, AppDbContext db, CancellationToken ct)
    {
        if (!DoctorCodeFormat.LooksLikeDoctorCode(doctorCode)) return Results.NotFound();
        var row = await Project(db).FirstOrDefaultAsync(x => x.DoctorCode == doctorCode, ct);
        return row is null ? Results.NotFound() : Results.Ok(ToCard(row));
    }

    private sealed class Row
    {
        public required string DoctorCode { get; init; }
        public required string FullName { get; init; }
        public required string Specialty { get; init; }
        public string? Qualifications { get; init; }
        public required string CityCode { get; init; }
        public required string CityName { get; init; }
        public string? ClinicName { get; init; }
        public string? ClinicAddress { get; init; }
        public string? ClinicPhone { get; init; }
    }

    private static IQueryable<Row> Project(AppDbContext db) =>
        from d in db.Doctors.AsNoTracking()
        where d.Status == DoctorRegistrationStatus.Verified && d.DoctorCode != null
        join c in db.CityCodes on d.PrimaryCityCode equals c.Code
        let clinic = d.ClinicAssignments.Where(a => a.IsPrimary).Select(a => a.Clinic).FirstOrDefault()
        select new Row
        {
            DoctorCode = d.DoctorCode!,
            FullName = d.FullName,
            Specialty = d.Specialty,
            Qualifications = d.Qualifications,
            CityCode = d.PrimaryCityCode,
            CityName = c.Name,
            ClinicName = clinic!.Name,
            ClinicAddress = clinic.AddressLine,
            ClinicPhone = clinic.PublicPhone,
        };

    private static DoctorCard ToCard(Row r) => new(
        r.DoctorCode, r.FullName, r.Specialty, r.Qualifications, r.CityCode, r.CityName,
        r.ClinicName is null ? null : new ClinicDto(r.ClinicName, r.ClinicAddress ?? "", r.ClinicPhone),
        IsVerified: true, AcceptingBookings: true);

    [GeneratedRegex(@"^dr\.?\s+", RegexOptions.IgnoreCase)]
    private static partial Regex DoctorPrefix();
}
