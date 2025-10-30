using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NextStakeWebApp.Models
{
    // Nota: in Postgres i nomi senza virgolette diventano minuscoli, quindi usiamo "leagues"
    [Table("leagues")]
    public class League
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("name")]
        public string? Name { get; set; }

        [Column("type")]
        public string? Type { get; set; }

        [Column("logo")]
        public string? Logo { get; set; }

        [Column("countryname")]
        public string? CountryName { get; set; }

        [Column("countrycode")]
        public string? CountryCode { get; set; }

        [Column("flag")]
        public string? Flag { get; set; }

        [Column("import")]
        public string? Import { get; set; }

        [Column("licenselist")]
        public string? LicenseList { get; set; }
    }
}
