namespace NextStakeWebApp.Models
{
    public class ExchangePredictionRow
    {
        public long MatchId { get; set; }
        public int? Banca1Affidabilita { get; set; }   // "Banca 1 - Affidabilità %"
        public int? BancaXAffidabilita { get; set; }   // "Banca X - Affidabilità %"
        public int? Banca2Affidabilita { get; set; }   // "Banca 2 - Affidabilità %"
        public string? BancataConsigliata { get; set; } // "Bancata consigliata"
        public string? BancaRisultato1 { get; set; }    // "Banca Risultato 1"
        public string? BancaRisultato2 { get; set; }    // "Banca Risultato 2"
        public string? BancaRisultato3 { get; set; }    // "Banca Risultato 3"
    }
}
