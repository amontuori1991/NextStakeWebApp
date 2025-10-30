namespace NextStakeWebApp.Models
{
    public enum SubscriptionPlan
    {
        TRL = 0, // Trial 15 giorni
        ADM = 1, // Amministratore, senza scadenza
        M1 = 10, // Mensile
        M2 = 20, // Bimestrale
        M3 = 30, // Trimestrale
        M6 = 60, // Semestrale
        Y1 = 100 // Annuale
    }
}