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

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // << AGGIUNGI QUESTA RIGA >>
            builder.HasDefaultSchema("public");

            builder.Entity<FavoriteMatch>()
                   .HasIndex(x => new { x.UserId, x.MatchId })
                   .IsUnique();
        }
    }
}
