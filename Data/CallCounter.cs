using System;

namespace NextStakeWebApp.Models
{
    public class CallCounter
    {
        public DateTime Date { get; set; }

        public string Origin { get; set; } = string.Empty;

        public int Counter { get; set; }
    }
}
