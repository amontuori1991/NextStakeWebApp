using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Models;

namespace NextStakeWebApp.Data
{
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options) { }

        public DbSet<FavoriteMatch> FavoriteMatches => Set<FavoriteMatch>();
        public DbSet<Analysis> Analyses { get; set; } = default!;
        public DbSet<PushSubscription> PushSubscriptions { get; set; } = default!;
        public DbSet<LiveMatchState> LiveMatchStates { get; set; } = default!;
        public DbSet<CallCounter> CallCounter { get; set; } = default!;

        // ✅ NUOVO: Schedine
        public DbSet<BetSlip> BetSlips { get; set; } = default!;
        public DbSet<BetSelection> BetSelections { get; set; } = default!;
        public DbSet<BetComment> BetComments { get; set; } = default!;
        public DbSet<UserFollow> UserFollows { get; set; } = default!;

        public DbSet<BetSlipLike> BetSlipLikes { get; set; } = default!;
        public DbSet<BetSlipSave> BetSlipSaves { get; set; } = default!;

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<BetSlipLike>()
    .HasIndex(x => new { x.BetSlipId, x.UserId })
    .IsUnique();

            builder.Entity<BetSlipSave>()
                .HasIndex(x => new { x.SourceBetSlipId, x.SavedByUserId })
                .IsUnique();


            // schema
            builder.HasDefaultSchema("public");

            builder.Entity<LiveMatchState>()
                .HasKey(x => x.MatchId);

            builder.Entity<UserFollow>(e =>
            {
                e.ToTable("UserFollows");
                e.HasKey(x => new { x.FollowerUserId, x.FollowedUserId });
                e.HasIndex(x => x.FollowerUserId);
                e.HasIndex(x => x.FollowedUserId);
            });

            builder.Entity<CallCounter>(entity =>
            {
                entity.ToTable("callcounter");
                entity.HasKey(e => new { e.Date, e.Origin });

                entity.Property(e => e.Date)
                      .HasColumnName("date")
                      .HasColumnType("date");

                entity.Property(e => e.Origin)
                      .HasColumnName("origin");

                entity.Property(e => e.Counter)
                      .HasColumnName("counter");
            });

            builder.Entity<FavoriteMatch>()
                   .HasIndex(x => new { x.UserId, x.MatchId })
                   .IsUnique();

            builder.Entity<PushSubscription>(entity =>
            {
                entity.ToTable("PushSubscriptions");

                entity.HasIndex(e => e.Endpoint).IsUnique();

                entity.HasOne(e => e.User)
                      .WithMany()
                      .HasForeignKey(e => e.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // =========================
            // ✅ NUOVO: configurazione schedine
            // =========================

            builder.Entity<BetSlip>(e =>
            {
                e.ToTable("BetSlips");
                e.HasIndex(x => x.UserId);

                e.Property(x => x.Type).HasMaxLength(20);

                e.HasMany(x => x.Selections)
                 .WithOne(s => s.BetSlip!)
                 .HasForeignKey(s => s.BetSlipId)
                 .OnDelete(DeleteBehavior.Cascade);

                e.HasMany(x => x.Comments)
                 .WithOne(c => c.BetSlip!)
                 .HasForeignKey(c => c.BetSlipId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<BetSelection>(e =>
            {
                e.ToTable("BetSelections");
                e.HasIndex(x => new { x.BetSlipId, x.MatchId });
                e.Property(x => x.Pick).HasMaxLength(120);
                e.Property(x => x.Note).HasMaxLength(250);
            });

            builder.Entity<BetComment>(e =>
            {
                e.ToTable("BetComments");
                e.HasIndex(x => x.BetSlipId);
                e.Property(x => x.Text).HasMaxLength(600);
            });
        }
    }
}
