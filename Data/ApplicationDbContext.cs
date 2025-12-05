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

        public DbSet<LiveMatchState> LiveMatchStates { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<LiveMatchState>()
       .HasKey(x => x.MatchId);

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
