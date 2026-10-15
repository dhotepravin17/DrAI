using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Domain;

namespace SmartQueue.Api.Infrastructure;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    /// <summary>ASP.NET Core Data Protection key ring (protects stored authenticator secrets); see Program.cs.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<User> Users => Set<User>();
    public DbSet<UserRoleAssignment> UserRoleAssignments => Set<UserRoleAssignment>();
    public DbSet<OtpChallenge> OtpChallenges => Set<OtpChallenge>();
    public DbSet<ConsentRecord> ConsentRecords => Set<ConsentRecord>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<Doctor> Doctors => Set<Doctor>();
    public DbSet<DoctorDeclaration> DoctorDeclarations => Set<DoctorDeclaration>();
    public DbSet<CityCode> CityCodes => Set<CityCode>();
    public DbSet<DoctorCodeSequence> DoctorCodeSequences => Set<DoctorCodeSequence>();
    public DbSet<Clinic> Clinics => Set<Clinic>();
    public DbSet<DoctorClinicAssignment> DoctorClinicAssignments => Set<DoctorClinicAssignment>();
    public DbSet<AdminAuditLog> AdminAuditLogs => Set<AdminAuditLog>();
    public DbSet<PatientLink> PatientLinks => Set<PatientLink>();
    public DbSet<StaffInvitation> StaffInvitations => Set<StaffInvitation>();
    public DbSet<SessionTemplate> SessionTemplates => Set<SessionTemplate>();
    public DbSet<ScheduleException> ScheduleExceptions => Set<ScheduleException>();
    public DbSet<Announcement> Announcements => Set<Announcement>();
    public DbSet<QueueSession> QueueSessions => Set<QueueSession>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<AppointmentEvent> AppointmentEvents => Set<AppointmentEvent>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<ProductEvent> ProductEvents => Set<ProductEvent>();

    public const string AppointmentReferenceSequence = "AppointmentReferenceNumbers";

    /// <summary>Statuses that count as an active appointment for duplicate-booking protection.</summary>
    public const string ActiveStatusFilter =
        "[Status] IN ('Booked','CheckedIn','Called','InConsultation','Missed','Requeued')";

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(40);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.Property(x => x.MobileNumber).HasMaxLength(13);
            e.Property(x => x.FullName).HasMaxLength(120);
            e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.TotpSecretProtected).HasMaxLength(500);
            e.HasIndex(x => x.MobileNumber);
            e.HasMany(x => x.RoleAssignments).WithOne().HasForeignKey(x => x.UserId);
        });

        b.Entity<UserRoleAssignment>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.Role });
        });

        b.Entity<OtpChallenge>(e =>
        {
            e.Property(x => x.MobileNumber).HasMaxLength(13);
            e.Property(x => x.CodeHash).HasMaxLength(64);
            e.HasIndex(x => new { x.MobileNumber, x.CreatedAt });
        });

        b.Entity<ConsentRecord>(e =>
        {
            e.Property(x => x.Version).HasMaxLength(40);
            e.Property(x => x.IpAddress).HasMaxLength(45);
            e.HasIndex(x => x.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        });

        b.Entity<Patient>(e =>
        {
            e.Property(x => x.FullName).HasMaxLength(120);
            e.Property(x => x.City).HasMaxLength(80);
            e.Property(x => x.ContactMobile).HasMaxLength(13);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.CreatedByUserId);
            e.HasIndex(x => x.ManagedByUserId);
            e.HasIndex(x => x.ContactMobile);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<CityCode>(e =>
        {
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.State).HasMaxLength(80);
            e.HasData(ReferenceData.SeedCities);
        });

        b.Entity<DoctorCodeSequence>(e =>
        {
            e.HasKey(x => x.CityCode);
            e.Property(x => x.CityCode).HasMaxLength(3).IsFixedLength();
            e.HasOne<CityCode>().WithOne().HasForeignKey<DoctorCodeSequence>(x => x.CityCode);
            e.HasData(ReferenceData.SeedCities.Select(c => new DoctorCodeSequence { CityCode = c.Code, LastValue = 0 }));
        });

        b.Entity<Doctor>(e =>
        {
            e.Property(x => x.FullName).HasMaxLength(120);
            e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.Specialty).HasMaxLength(80);
            e.Property(x => x.Qualifications).HasMaxLength(200);
            e.Property(x => x.MedicalRegistrationId).HasMaxLength(40);
            e.Property(x => x.MedicalRegistrationAuthority).HasMaxLength(80);
            e.Property(x => x.PrimaryCityCode).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.DoctorCode).HasMaxLength(12);
            e.Property(x => x.SubmissionReference).HasMaxLength(30);
            e.Property(x => x.VerificationReference).HasMaxLength(100);
            e.Property(x => x.StatusNote).HasMaxLength(500);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => x.UserId).IsUnique();
            e.HasIndex(x => x.DoctorCode).IsUnique().HasFilter("[DoctorCode] IS NOT NULL");
            e.HasIndex(x => new { x.MedicalRegistrationAuthority, x.MedicalRegistrationId });
            e.HasIndex(x => new { x.Status, x.FullName });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CityCode>().WithMany().HasForeignKey(x => x.PrimaryCityCode).OnDelete(DeleteBehavior.NoAction);
            e.HasMany(x => x.Declarations).WithOne().HasForeignKey(x => x.DoctorId);
            e.HasMany(x => x.ClinicAssignments).WithOne().HasForeignKey(x => x.DoctorId);
        });

        b.Entity<DoctorDeclaration>(e =>
        {
            e.Property(x => x.DeclarationVersion).HasMaxLength(40);
            e.Property(x => x.SubmissionReference).HasMaxLength(30);
            e.Property(x => x.IpAddress).HasMaxLength(45);
            e.Property(x => x.UserAgent).HasMaxLength(400);
        });

        b.Entity<Clinic>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.AddressLine).HasMaxLength(300);
            e.Property(x => x.CityCode).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.PublicPhone).HasMaxLength(20);
            e.HasOne<CityCode>().WithMany().HasForeignKey(x => x.CityCode).OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<DoctorClinicAssignment>(e =>
        {
            e.HasIndex(x => new { x.DoctorId, x.ClinicId }).IsUnique();
        });

        b.Entity<PatientLink>(e =>
        {
            e.Property(x => x.InviteeMobile).HasMaxLength(13);
            e.HasIndex(x => x.ViewerUserId);
            e.HasIndex(x => x.InviteeMobile);
            e.HasIndex(x => x.PatientId);
        });

        b.Entity<StaffInvitation>(e =>
        {
            e.Property(x => x.MobileNumber).HasMaxLength(13);
            e.Property(x => x.FullName).HasMaxLength(120);
            e.HasIndex(x => x.DoctorId);
            e.HasIndex(x => x.MobileNumber);
        });

        b.Entity<SessionTemplate>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(30);
            e.HasIndex(x => new { x.DoctorId, x.ClinicId, x.DayOfWeek });
        });

        b.Entity<ScheduleException>(e =>
        {
            e.Property(x => x.SessionName).HasMaxLength(30);
            e.Property(x => x.Reason).HasMaxLength(280);
            e.HasIndex(x => new { x.DoctorId, x.StartDate, x.EndDate });
        });

        b.Entity<Announcement>(e =>
        {
            e.Property(x => x.Message).HasMaxLength(280);
            e.HasIndex(x => new { x.DoctorId, x.StartsAt });
        });

        b.Entity<QueueSession>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(30);
            e.HasIndex(x => new { x.DoctorId, x.ClinicId, x.Date, x.Name }).IsUnique();
            e.HasIndex(x => new { x.Date, x.State });
        });

        b.HasSequence<long>(AppointmentReferenceSequence).StartsAt(1);

        b.Entity<Appointment>(e =>
        {
            e.Property(x => x.Reference).HasMaxLength(20);
            e.Property(x => x.SessionName).HasMaxLength(30);
            e.HasIndex(x => new { x.DoctorId, x.Date });
            e.Property(x => x.EmergencyNote).HasMaxLength(200);
            e.Property(x => x.FollowUpNote).HasMaxLength(500);
            e.HasIndex(x => x.Reference).IsUnique();
            e.HasIndex(x => new { x.QueueSessionId, x.TokenNumber }).IsUnique();
            e.HasIndex(x => new { x.QueueSessionId, x.PatientId }).IsUnique().HasFilter(ActiveStatusFilter);
            e.HasIndex(x => x.PatientId);
            e.HasIndex(x => x.BookedByUserId);
            e.HasIndex(x => new { x.DoctorId, x.CreatedAt });
            e.HasOne<QueueSession>().WithMany().HasForeignKey(x => x.QueueSessionId);
            e.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<Doctor>().WithMany().HasForeignKey(x => x.DoctorId).OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<AppointmentEvent>(e =>
        {
            e.Property(x => x.Type).HasMaxLength(40);
            e.Property(x => x.Reason).HasMaxLength(300);
            e.HasIndex(x => x.AppointmentId);
            e.HasIndex(x => x.QueueSessionId);
        });

        b.Entity<IdempotencyRecord>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(100);
            e.HasIndex(x => new { x.UserId, x.Key }).IsUnique();
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.Property(x => x.EventType).HasMaxLength(40);
            e.Property(x => x.DedupKey).HasMaxLength(150);
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.HasIndex(x => x.DedupKey).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
        });

        b.Entity<Notification>(e =>
        {
            e.Property(x => x.EventType).HasMaxLength(40);
            e.Property(x => x.Title).HasMaxLength(120);
            e.Property(x => x.Body).HasMaxLength(500);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.OutboxMessageId }).IsUnique();
        });

        b.Entity<NotificationLog>(e =>
        {
            e.Property(x => x.EventType).HasMaxLength(40);
            e.Property(x => x.Error).HasMaxLength(1000);
            e.Property(x => x.ProviderReference).HasMaxLength(200);
            e.HasIndex(x => new { x.Status, x.CreatedAt });
        });

        b.Entity<PushSubscription>(e =>
        {
            e.Property(x => x.Endpoint).HasMaxLength(1000);
            e.Property(x => x.P256dh).HasMaxLength(200);
            e.Property(x => x.Auth).HasMaxLength(100);
            e.HasIndex(x => x.UserId);
        });

        b.Entity<ProductEvent>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(60);
            e.Property(x => x.Dimension).HasMaxLength(60);
            e.Property(x => x.PropertiesJson).HasMaxLength(2000);
            e.HasIndex(x => new { x.Name, x.OccurredAt });
            e.HasIndex(x => x.OccurredAt);
        });

        b.Entity<AdminAuditLog>(e =>
        {
            e.Property(x => x.Action).HasMaxLength(60);
            e.Property(x => x.EntityType).HasMaxLength(60);
            e.Property(x => x.EntityId).HasMaxLength(60);
            e.Property(x => x.Purpose).HasMaxLength(200);
            e.Property(x => x.Details).HasMaxLength(2000);
            e.Property(x => x.IpAddress).HasMaxLength(45);
            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
        });
    }
}
