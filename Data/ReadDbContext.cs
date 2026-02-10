using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Models;

namespace NextStakeWebApp.Data
{
    public class ReadDbContext : DbContext
    {
        public ReadDbContext(DbContextOptions<ReadDbContext> options) : base(options) { }
        public DbSet<ExchangeTodayRow> ExchangeTodayRows => Set<ExchangeTodayRow>();
        public DbSet<PredictionDbRow> PredictionsCache { get; set; } = default!;


        public DbSet<MatchCore> Matches => Set<MatchCore>();
        public DbSet<NextMatch> NextMatches => Set<NextMatch>();
        public DbSet<League> Leagues => Set<League>();
        public DbSet<Team> Teams => Set<Team>();
        public DbSet<Standing> Standings => Set<Standing>();
        public DbSet<Odds> Odds { get; set; } = null!;

        // Rimane un DbSet anche se keyless, per poter fare query LINQ
        public DbSet<Analysis> Analyses => Set<Analysis>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<MatchCore>().ToTable("matches", t => t.ExcludeFromMigrations());
            modelBuilder.Entity<NextMatch>().ToTable("nextmatch", t => t.ExcludeFromMigrations());
            modelBuilder.Entity<League>().ToTable("leagues", t => t.ExcludeFromMigrations());
            modelBuilder.Entity<Team>().ToTable("teams", t => t.ExcludeFromMigrations());
            modelBuilder.Entity<Standing>().ToTable("standings", t => t.ExcludeFromMigrations());
            modelBuilder.Entity<BestPickRow>().HasNoKey();
            modelBuilder.Entity<NextStakeWebApp.Models.ExchangeOtherTodayRow>().HasNoKey();
            modelBuilder.Entity<ExchangeTodayRow>(eb =>
            {
                eb.HasNoKey();
                eb.ToView(null); // query-only (FromSqlRaw)
            });


            // 🔹 Mapping tabella odds (Postgres: "odds", tutta minuscola)
            modelBuilder.Entity<Odds>(entity =>
            {
                entity.HasNoKey();                           // tabella senza PK
                entity.ToTable("odds", t => t.ExcludeFromMigrations());

                // Mapping colonne (opzionale ma pulito)
                entity.Property(o => o.Id).HasColumnName("id");
                entity.Property(o => o.Bookmaker).HasColumnName("bookmaker");
                entity.Property(o => o.Betid).HasColumnName("betid");
                entity.Property(o => o.Description).HasColumnName("description");
                entity.Property(o => o.Value).HasColumnName("value");
                entity.Property(o => o.Odd).HasColumnName("odd");
                entity.Property(o => o.Dateupd).HasColumnName("dateupd");
            });

            // 🔹 Analyses è una tabella senza PK: la mappiamo come keyless
            modelBuilder.Entity<Analysis>(eb =>
            {
                eb.HasNoKey();
                eb.ToTable("analyses", t => t.ExcludeFromMigrations());

                // colonne
                eb.Property(a => a.ViewName).HasColumnName("viewname");
                eb.Property(a => a.Description).HasColumnName("description");
                eb.Property(a => a.ViewValue).HasColumnName("viewvalue");

                // la tabella NON ha la colonna Id -> ignorala per evitare il 42703
                eb.Ignore(a => a.Id);
            });
        }
    }
}
