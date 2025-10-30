using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NextStakeWebApp.Models
{
    [Table("teams")] // tutto minuscolo su Neon
    public class Team
    {
        [Key]
        [Column("id")]
        public long Id { get; set; }

        [Column("name")]
        public string? Name { get; set; }

        [Column("code")]
        public string? Code { get; set; }

        [Column("country")]
        public string? Country { get; set; }

        [Column("founded")]
        public int? Founded { get; set; }

        [Column("national")]
        public bool? National { get; set; }

        [Column("logo")]
        public string? Logo { get; set; }
    }
}
