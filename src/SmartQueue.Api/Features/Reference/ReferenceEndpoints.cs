using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Reference;

public static class ReferenceEndpoints
{
    public sealed record CityDto(string Code, string Name, string State);
    public sealed record LegalVersions(string PrivacyNotice, string Terms, string DoctorDeclaration);
    public sealed record ReferenceResponse(
        IReadOnlyList<CityDto> Cities, IReadOnlyList<string> MedicalAuthorities,
        IReadOnlyList<string> Specialties, LegalVersions LegalVersions);
    public sealed record DeclarationResponse(string Version, string Text);

    public static RouteGroupBuilder MapReferenceEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/reference").WithTags("Reference");

        group.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var cities = await db.CityCodes.AsNoTracking().Where(c => c.IsActive)
                .OrderBy(c => c.Name).Select(c => new CityDto(c.Code, c.Name, c.State)).ToListAsync(ct);
            return Results.Ok(new ReferenceResponse(
                cities, ReferenceData.MedicalAuthorities, ReferenceData.Specialties,
                new LegalVersions(LegalTexts.PrivacyNoticeVersion, LegalTexts.TermsVersion, LegalTexts.DoctorDeclarationVersion)));
        });

        group.MapGet("/doctor-declaration", () =>
            Results.Ok(new DeclarationResponse(LegalTexts.DoctorDeclarationVersion, LegalTexts.DoctorDeclarationText)));

        return api;
    }
}
