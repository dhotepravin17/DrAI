using SmartQueue.Api.Domain;

namespace SmartQueue.Api.Infrastructure;

public static class ReferenceData
{
    public static readonly CityCode[] SeedCities =
    [
        new() { Code = "NGP", Name = "Nagpur", State = "Maharashtra" },
        new() { Code = "MUM", Name = "Mumbai", State = "Maharashtra" },
        new() { Code = "PUN", Name = "Pune", State = "Maharashtra" },
        new() { Code = "CDP", Name = "Chandrapur", State = "Maharashtra" },
        new() { Code = "AMR", Name = "Amravati", State = "Maharashtra" },
        new() { Code = "WRD", Name = "Wardha", State = "Maharashtra" },
        new() { Code = "NSK", Name = "Nashik", State = "Maharashtra" },
        new() { Code = "AUR", Name = "Chhatrapati Sambhajinagar", State = "Maharashtra" },
    ];

    public static readonly string[] MedicalAuthorities =
    [
        "National Medical Commission",
        "Maharashtra Medical Council",
        "Andhra Pradesh Medical Council",
        "Bihar Medical Council",
        "Chhattisgarh Medical Council",
        "Delhi Medical Council",
        "Goa Medical Council",
        "Gujarat Medical Council",
        "Haryana Medical Council",
        "Karnataka Medical Council",
        "Kerala (Travancore-Cochin) Medical Council",
        "Madhya Pradesh Medical Council",
        "Punjab Medical Council",
        "Rajasthan Medical Council",
        "Tamil Nadu Medical Council",
        "Telangana State Medical Council",
        "Uttar Pradesh Medical Council",
        "West Bengal Medical Council",
        "Other State Medical Council",
    ];

    public static readonly string[] Specialties =
    [
        "General Physician",
        "Paediatrician",
        "Gynaecologist",
        "Dermatologist",
        "ENT Specialist",
        "Ophthalmologist",
        "Orthopaedic Surgeon",
        "Cardiologist",
        "Dentist",
        "Psychiatrist",
        "General Surgeon",
        "Pulmonologist",
        "Diabetologist",
        "Other",
    ];
}

/// <summary>Versioned legal texts. Final wording is pending legal review (PRD OQ-02).</summary>
public static class LegalTexts
{
    public const string DoctorDeclarationVersion = "DOC-DECL-1.0";

    public const string DoctorDeclarationText =
        "I confirm that the Medical Registration ID and professional information provided by me are accurate and belong to me. " +
        "I will not use another person's registration details, misrepresent my professional identity, or misuse another person's medical credentials. " +
        "I understand that the platform may verify the information provided and may restrict or suspend my account if material information is false or misused.";

    public const string PrivacyNoticeVersion = "PRIV-1.0";
    public const string TermsVersion = "TERMS-1.0";
}
