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

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<LiveMatchState>()
       .HasKey(x => x.MatchId);
            builder.Entity<CallCounter>(entity =>
            {
                entity.ToTable("callcounter"); // nome esatto della tabella

                // PK composta: (date, origin) come nel tuo ON CONFLICT
                entity.HasKey(e => new { e.Date, e.Origin });

                entity.Property(e => e.Date)
                      .HasColumnName("date")
                      .HasColumnType("date");

                entity.Property(e => e.Origin)
                      .HasColumnName("origin");

                entity.Property(e => e.Counter)
                      .HasColumnName("counter");
            });

            // << AGGIUNGI QUESTA RIGA >>
            builder.HasDefaultSchema("public");

            builder.Entity<FavoriteMatch>()
                   .HasIndex(x => new { x.UserId, x.MatchId })
                   .IsUnique();
            builder.Entity<PushSubscription>(entity =>
            {
                entity.ToTable("PushSubscriptions");

                entity.HasIndex(e => e.Endpoint)
                      .IsUnique();

                entity.HasOne(e => e.User)
                      .WithMany()
                      .HasForeignKey(e => e.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

        }
    }
}
