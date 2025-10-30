using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace NextStakeWebApp.Models
{
    [Table("analyses")]
    public class Analysis
    {
        public int Id { get; set; }

        [Column("viewname")]
        public string ViewName { get; set; } = "";

        [Column("description")]
        public string Description { get; set; } = "";

        [Column("viewvalue")]
        public string ViewValue { get; set; } = "";
    }
}