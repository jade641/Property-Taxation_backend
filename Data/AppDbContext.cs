using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PropertyTax.API.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PropertyTax.API.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser, IdentityRole, string>
{
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<Taxpayer> Taxpayers => Set<Taxpayer>();
    public DbSet<TaxAssessment> TaxAssessments => Set<TaxAssessment>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<DataNotification> DataNotifications => Set<DataNotification>();
    public DbSet<PropertyDocument> PropertyDocuments => Set<PropertyDocument>();
    public DbSet<Province> Provinces => Set<Province>();
    public DbSet<CityMunicipality> CitiesMunicipalities => Set<CityMunicipality>();
    public DbSet<Barangay> Barangays => Set<Barangay>();
    public DbSet<MlModel> MlModels => Set<MlModel>();
    public DbSet<MlPrediction> MlPredictions => Set<MlPrediction>();
    public DbSet<MlAlert> MlAlerts => Set<MlAlert>();
    public DbSet<MlTrainingJob> MlTrainingJobs => Set<MlTrainingJob>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var entries = ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified || e.State == EntityState.Deleted)
            .Where(e => !(e.Entity is AuditLog) && !(e.Entity is DataNotification))
            .ToList();

        if (entries.Any())
        {
            // collect user ids to broadcast notifications to all users
            var userIds = await Users.Select(u => u.Id).ToListAsync(cancellationToken);

            var notifications = new List<DataNotification>();
            var now = DateTime.UtcNow;

            foreach (var e in entries)
            {
                var entityType = e.Entity.GetType();
                var entityName = entityType.Name;

                // try to find the primary key value if present
                var primary = e.Properties.FirstOrDefault(p => p.Metadata.IsPrimaryKey());
                var entityId = primary?.CurrentValue?.ToString() ?? string.Empty;
                var changeType = e.State.ToString();

                var title = $"{changeType} {entityName}";
                var message = string.IsNullOrEmpty(entityId)
                    ? $"{entityName} {changeType.ToLower()}"
                    : $"{entityName} {changeType.ToLower()} (Id: {entityId})";

                foreach (var uid in userIds)
                {
                    notifications.Add(new DataNotification
                    {
                        UserId = uid,
                        Title = title,
                        Message = message,
                        Type = changeType,
                        EntityName = entityName,
                        EntityId = entityId,
                        IsRead = false,
                        CreatedAtUtc = now
                    });
                }
            }

            if (notifications.Count > 0)
            {
                // add range - these will be saved as part of the same SaveChanges call
                DataNotifications.AddRange(notifications);
            }
        }

        return await base.SaveChangesAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.ToTable("Users");
            entity.HasIndex(user => user.NormalizedUserName)
                .IsUnique()
                .HasDatabaseName("UserNameIndex");
            entity.HasIndex(user => user.NormalizedEmail)
                .IsUnique()
                .HasDatabaseName("EmailIndex");
        });

        builder.Entity<IdentityRole>().ToTable("Roles");
        builder.Entity<IdentityUserRole<string>>().ToTable("UserRoles");
        builder.Entity<IdentityUserClaim<string>>().ToTable("UserClaims");
        builder.Entity<IdentityUserLogin<string>>().ToTable("UserLogins");
        builder.Entity<IdentityUserToken<string>>().ToTable("UserTokens");
        builder.Entity<IdentityRoleClaim<string>>().ToTable("RoleClaims");

        builder.Entity<Property>(entity =>
        {
            entity.HasIndex(x => x.Pin).IsUnique();
            entity.HasIndex(x => x.TaxDeclarationNumber).IsUnique();
            entity.Property(x => x.TaxDeclarationNumber).HasMaxLength(50);
            entity.Property(x => x.ZoningClassification).HasMaxLength(100);
            entity.Property(x => x.Remarks).HasMaxLength(500);
            entity.Property(x => x.MarketValue).HasPrecision(18, 2);
            entity.Property(x => x.TaxRate).HasPrecision(10, 4);
            entity.Property(x => x.AssessmentLevel).HasPrecision(10, 4);
            entity.Property(x => x.AreaSquareMeters).HasPrecision(18, 2);

            entity.HasOne(x => x.Taxpayer)
                .WithMany(x => x.Properties)
                .HasForeignKey(x => x.TaxpayerId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(x => x.BarangayLocation)
                .WithMany(x => x.Properties)
                .HasForeignKey(x => x.BarangayId)
                .OnDelete(DeleteBehavior.SetNull);
        });

            builder.Entity<Taxpayer>(entity =>
            {
                entity.ToTable("property_owners");
            });

            builder.Entity<MlModel>(entity =>
            {
                entity.ToTable("ml_models");
                entity.Property(x => x.Name).HasMaxLength(200);
                entity.Property(x => x.Version).HasMaxLength(50);
                entity.Property(x => x.ArtifactPath).HasMaxLength(1000);
                entity.Property(x => x.MetricsJson).HasColumnType("json");
                entity.Property(x => x.CreatedAt).HasColumnType("datetime");

                entity.HasMany(x => x.Predictions)
                    .WithOne(x => x.Model)
                    .HasForeignKey(x => x.ModelId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasMany(x => x.TrainingJobs)
                    .WithOne(x => x.Model)
                    .HasForeignKey(x => x.ModelId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<MlPrediction>(entity =>
            {
                entity.ToTable("ml_predictions");
                entity.Property(x => x.ExplanationJson).HasColumnType("json");
                entity.Property(x => x.CreatedAt).HasColumnType("datetime");
                entity.Property(x => x.Probability).HasPrecision(10, 4);

                entity.HasOne(x => x.Property)
                        .WithMany(x => x.MlPredictions)
                    .HasForeignKey(x => x.PropertyId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(x => x.Model)
                    .WithMany(x => x.Predictions)
                    .HasForeignKey(x => x.ModelId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(x => x.CreatedBy)
                    .WithMany()
                    .HasForeignKey(x => x.CreatedById)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            builder.Entity<MlTrainingJob>(entity =>
            {
                entity.ToTable("ml_training_jobs");
                entity.Property(x => x.ParamsJson).HasColumnType("json");
                entity.Property(x => x.Status).HasMaxLength(50);
                entity.Property(x => x.Logs).HasColumnType("longtext");

                entity.HasOne(x => x.Model)
                    .WithMany(x => x.TrainingJobs)
                    .HasForeignKey(x => x.ModelId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<MlAlert>(entity =>
            {
                entity.ToTable("ml_alerts");
                entity.Property(x => x.Title).HasMaxLength(200);
                entity.Property(x => x.Severity).HasMaxLength(20);
                entity.Property(x => x.Status).HasMaxLength(20);
                entity.Property(x => x.CreatedAt).HasColumnType("datetime");

                entity.HasOne(x => x.Property)
                    .WithMany()
                    .HasForeignKey(x => x.PropertyId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

        builder.Entity<Province>(entity =>
        {
            entity.ToTable("provinces");
            entity.HasIndex(x => x.PsgcCode).IsUnique();
            entity.Property(x => x.RegionCode).HasMaxLength(20);
            entity.Property(x => x.PsgcCode).HasMaxLength(20);
            entity.Property(x => x.Name).HasMaxLength(100);
        });

        builder.Entity<CityMunicipality>(entity =>
        {
            entity.ToTable("cities_municipalities");
            entity.HasIndex(x => x.PsgcCode).IsUnique();
            entity.HasIndex(x => new { x.ProvinceId, x.Name });
            entity.Property(x => x.PsgcCode).HasMaxLength(20);
            entity.Property(x => x.Name).HasMaxLength(100);
            entity.Property(x => x.LguType).HasMaxLength(30);

            entity.HasOne(x => x.Province)
                .WithMany(x => x.CitiesMunicipalities)
                .HasForeignKey(x => x.ProvinceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Barangay>(entity =>
        {
            entity.ToTable("barangays");
            entity.HasIndex(x => x.PsgcCode).IsUnique();
            entity.HasIndex(x => new { x.CityMunicipalityId, x.Name });
            entity.Property(x => x.PsgcCode).HasMaxLength(20);
            entity.Property(x => x.Name).HasMaxLength(100);

            entity.HasOne(x => x.CityMunicipality)
                .WithMany(x => x.Barangays)
                .HasForeignKey(x => x.CityMunicipalityId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TaxAssessment>(entity =>
        {
            entity.Property(x => x.MarketValue).HasPrecision(18, 2);
            entity.Property(x => x.AssessmentLevel).HasPrecision(10, 4);
            entity.Property(x => x.AssessedValue).HasPrecision(18, 2);
            entity.Property(x => x.TaxRate).HasPrecision(10, 4);
            entity.Property(x => x.TaxDue).HasPrecision(18, 2);

            entity.HasOne(x => x.Property)
                .WithMany(x => x.TaxAssessments)
                .HasForeignKey(x => x.PropertyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Payment>(entity =>
        {
            entity.HasIndex(x => x.OfficialReceiptNumber).IsUnique();
            entity.Property(x => x.AmountDue).HasPrecision(18, 2);
            entity.Property(x => x.AmountPaid).HasPrecision(18, 2);
            entity.Property(x => x.Penalty).HasPrecision(18, 2);
            entity.Property(x => x.ReferenceNumber).HasMaxLength(100);
            entity.Property(x => x.BankName).HasMaxLength(120);

            entity.HasOne(x => x.Property)
                .WithMany(x => x.Payments)
                .HasForeignKey(x => x.PropertyId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Taxpayer)
                .WithMany(x => x.Payments)
                .HasForeignKey(x => x.TaxpayerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

            builder.Entity<DataNotification>(entity =>
            {
                entity.ToTable("data_notifications");
                entity.Property(x => x.Title).HasMaxLength(200);
                entity.Property(x => x.Type).HasMaxLength(20);
                entity.Property(x => x.EntityName).HasMaxLength(100);
                entity.Property(x => x.EntityId).HasMaxLength(100);

                entity.HasIndex(x => x.CreatedAtUtc);
                entity.HasIndex(x => new { x.UserId, x.IsRead, x.CreatedAtUtc });

                entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            });

        builder.Entity<PropertyDocument>(entity =>
        {
            entity.Property(x => x.SizeInBytes).HasColumnType("bigint");

            entity.HasOne(x => x.Property)
                .WithMany(x => x.Documents)
                .HasForeignKey(x => x.PropertyId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}