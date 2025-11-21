using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Humanizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;
using NextStakeWebApp.Services;
using Npgsql;

namespace NextStakeWebApp.Pages.Match
{
    [Authorize] // basta essere autenticati
    public class DetailsModel : PageModel

    {
        private readonly ReadDbContext _read;
        private readonly ApplicationDbContext _write;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ITelegramService _telegram;
        private readonly IConfiguration _config;
        private readonly IOpenAIService _openai;
        private readonly IMatchBannerService _banner;


        public DetailsModel(
            ReadDbContext read,
            ApplicationDbContext write,
            UserManager<ApplicationUser> userManager,
            ITelegramService telegram,
            IConfiguration config,
            IOpenAIService openai,
            IMatchBannerService banner) // <--- aggiungi
        {
            _read = read;
            _write = write;
            _userManager = userManager;
            _telegram = telegram;
            _config = config;
            _openai = openai;
            _banner = banner; // <--- aggiungi
        }
        [TempData]
        public string? StatusMessage { get; set; }
        public VM Data { get; private set; } = new();

        public class VM
        {
            public long MatchId { get; set; }
            public int LeagueId { get; set; }
            public int Season { get; set; }

            public string LeagueName { get; set; } = "";
            public string? LeagueLogo { get; set; }
            public string? CountryName { get; set; }

            public long HomeId { get; set; }
            public long AwayId { get; set; }
            public string Home { get; set; } = "";
            public string Away { get; set; } = "";
            public string? HomeLogo { get; set; }
            public string? AwayLogo { get; set; }

            public DateTime KickoffUtc { get; set; }

            public PredictionRow? Prediction { get; set; }
            public ExchangePredictionRow? Exchange { get; set; }

            public bool IsFavorite { get; set; }
            public int RemainingMatches { get; set; }

            public MetricGroup? Goals { get; set; }
            public MetricGroup? Shots { get; set; }
            public MetricGroup? Fouls { get; set; }
            public MetricGroup? Cards { get; set; }
            public MetricGroup? Corners { get; set; }
            public MetricGroup? Offsides { get; set; }

            public List<FormRow> HomeForm { get; set; } = new();
            public List<FormRow> AwayForm { get; set; } = new();
            public List<TableStandingRow> Standings { get; set; } = new();

            public List<OddsRow> Odds { get; set; } = new();
            public List<int> BetIds { get; set; } = new();


        }
        public class TableStandingRow
        {
            public int Rank { get; set; }
            public long TeamId { get; set; }
            public string TeamName { get; set; } = "";
            public int Points { get; set; }
            public int Played { get; set; }
            public int Win { get; set; }
            public int Draw { get; set; }
            public int Lose { get; set; }
            public int GF { get; set; }
            public int GA { get; set; }
            public int Diff { get; set; }
        }
        public class OddsRow
        {
            public int Bookmaker { get; set; }       // es. 8 = Bet365 (per ora numero)
            public int BetId { get; set; }           // tipo quota (numeric)
            public string? Description { get; set; } // campo description
            public string? Value { get; set; }       // campo Value
            public decimal Odd { get; set; }         // campo odd (es. 6.50)
            public DateTime DateUpdated { get; set; }// campo dateupd
        }

        // Handler: genera con AI e invia su 'Idee'
        // Handler: genera con AI e invia su 'Idee' (con integrazione dati Prediction)


        public async Task<IActionResult> OnPostPreviewAiPickAsync(int id, string? analysisPayload)
        {
            if (!User.HasClaim("plan", "1"))
                return Forbid();

            // 1) Metadati match
            var dto = await (
                from mm in _read.Matches
                join lg in _read.Leagues on mm.LeagueId equals lg.Id
                join th in _read.Teams on mm.HomeId equals th.Id
                join ta in _read.Teams on mm.AwayId equals ta.Id
                where mm.Id == id
                select new
                {
                    MatchId = mm.Id,           // 👈 AGGIUNTO
                    LeagueName = lg.Name,
                    CountryCode = lg.CountryCode,
                    KickoffUtc = mm.Date,
                    Home = th.Name ?? "",
                    Away = ta.Name ?? "",
                    LeagueId = lg.Id,
                    Season = mm.Season,
                    HomeId = th.Id,
                    AwayId = ta.Id,
                    HomeLogo = th.Logo,
                    AwayLogo = ta.Logo
                }
            ).AsNoTracking().FirstOrDefaultAsync();


            if (dto is null) return NotFound();

            // 2) Dati proprietari + forma + classifica
            var p = await LoadPredictionAsync(id);
            var esitoProprietario = p?.Esito ?? "X";
            var homeForm = await GetLastFiveAsync(dto.HomeId, dto.LeagueId, dto.Season);
            var awayForm = await GetLastFiveAsync(dto.AwayId, dto.LeagueId, dto.Season);
            var standings = await GetStandingsAsync(dto.LeagueId, dto.Season);
            var remainingRounds = await GetRemainingMatchesAsync(dto.LeagueId, dto.Season);

            string ToFormSeq(List<FormRow> rows) => string.Join("", rows.Select(r => r.Result));
            var homeSeq = homeForm.Count > 0 ? ToFormSeq(homeForm) : "—";
            var awaySeq = awayForm.Count > 0 ? ToFormSeq(awayForm) : "—";

            string FormSeqToEmojis(string seq)
            {
                if (string.IsNullOrWhiteSpace(seq)) return "—";
                return string.Concat(seq.Select(c => c switch
                {
                    'W' => "🟩",
                    'D' => "⬜️",
                    'L' => "🟥",
                    _ => ""
                }));
            }

            var homeSeqEmo = FormSeqToEmojis(homeSeq);
            var awaySeqEmo = FormSeqToEmojis(awaySeq);

            int? homeRank = standings.FirstOrDefault(s => s.TeamId == dto.HomeId)?.Rank;
            int? awayRank = standings.FirstOrDefault(s => s.TeamId == dto.AwayId)?.Rank;
            // ESITO proprietario (da Prediction) e squadra favorita
            var esito = (p?.Esito ?? "").Trim().ToUpperInvariant();

            string favoriteTeam = esito switch
            {
                "1" => dto.Home,
                "1X" => dto.Home,
                "2" => dto.Away,
                "X2" => dto.Away,
                _ => "Nessuna (match aperto)"
            };


            // 3) Parse robusto del payload analitico (tollerante a chiavi/label)
            string analysisContext = BuildAnalysisContextForAi(analysisPayload, dto.Home, dto.Away);

            // 3-bis) Contesto di campionato (title race / salvezza / trappole)
            string leagueContext = BuildLeagueContextForAi(standings, dto.HomeId, dto.AwayId, remainingRounds);

            // 4) Contesto proprietario compatto
            string proprietaryContext = p is null
                ? $"(nessun pronostico interno disponibile)\nForma {dto.Home}: {homeSeq} | Rank: {(homeRank?.ToString() ?? "—")}\nForma {dto.Away}: {awaySeq} | Rank: {(awayRank?.ToString() ?? "—")}"
                : $"Esito: {p.Esito}\nGG/NG: {p.GG_NG}\nOver/Under: {p.OverUnderRange}\nMG Casa: {p.MultigoalCasa}\nMG Ospite: {p.MultigoalOspite}\nGoal simulati: {p.GoalSimulatoCasa}-{p.GoalSimulatoOspite} (Tot: {p.TotaleGoalSimulati})\nForma {dto.Home}: {homeSeq} | Rank: {(homeRank?.ToString() ?? "—")}\nForma {dto.Away}: {awaySeq} | Rank: {(awayRank?.ToString() ?? "—")}";

            // 4.bis) Ricava anche le linee numeriche indicative dai blocchi analitici JSON
            JsonObject? goalsObj = null, cornersObj = null, cardsObj = null, shotsObj = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(analysisPayload))
                {
                    var root = JsonNode.Parse(analysisPayload)!.AsObject();
                    goalsObj = root.Where(kv => string.Equals(kv.Key, "goals", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value as JsonObject).FirstOrDefault();
                    cornersObj = root.Where(kv => string.Equals(kv.Key, "corners", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value as JsonObject).FirstOrDefault();
                    cardsObj = root.Where(kv => string.Equals(kv.Key, "cards", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value as JsonObject).FirstOrDefault();
                    shotsObj = root.Where(kv => string.Equals(kv.Key, "shots", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value as JsonObject).FirstOrDefault();
                }
            }
            catch { /* ok */ }

            var totalLines = BuildIndicativeLines(goalsObj, cornersObj, cardsObj, shotsObj, p);
            var teamLinesCandidates = BuildTeamCandidates(goalsObj, cornersObj, cardsObj, shotsObj, p, dto.Home, dto.Away);
            var teamLines = BuildTeamLines(goalsObj, cornersObj, cardsObj, shotsObj, p);

            try
            {
                if (!string.IsNullOrWhiteSpace(analysisPayload))
                {
                    var root = JsonNode.Parse(analysisPayload)!.AsObject();
                    goalsObj = root.Where(kv => string.Equals(kv.Key, "goals", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value as JsonObject).FirstOrDefault();
                    cornersObj = root.Where(kv => string.Equals(kv.Key, "corners", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value as JsonObject).FirstOrDefault();
                    cardsObj = root.Where(kv => string.Equals(kv.Key, "cards", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value as JsonObject).FirstOrDefault();
                    shotsObj = root.Where(kv => string.Equals(kv.Key, "shots", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value as JsonObject).FirstOrDefault();
                }
            }
            catch { /* payload non essenziale per le linee */ }

            var lines = BuildIndicativeLines(goalsObj, cornersObj, cardsObj, shotsObj, p);

            // 5-bis) Calcolo pesato linee indicative (totali + singola squadra)
            var W = GetWeightsFromPrediction(p);
            var linesTotal = BuildIndicativeLinesWeighted(W, goals: null, shots: null, corners: null, cards: null, fouls: null, offsides: null);

            var goalsMg = await RunAnalysisAsync("NextMatchGoals_Analyses", dto.LeagueId, dto.Season, (int)id);
            var shotsMg = await RunAnalysisAsync("NextMatchShots_Analyses", dto.LeagueId, dto.Season, (int)id);
            var cornersMg = await RunAnalysisAsync("NextMatchCorners_Analyses", dto.LeagueId, dto.Season, (int)id);
            var cardsMg = await RunAnalysisAsync("NextMatchCards_Analyses", dto.LeagueId, dto.Season, (int)id);
            var foulsMg = await RunAnalysisAsync("NextMatchFouls_Analyses", dto.LeagueId, dto.Season, (int)id);
            var offsMg = await RunAnalysisAsync("NextMatchOffsides_Analyses", dto.LeagueId, dto.Season, (int)id);

            linesTotal = BuildIndicativeLinesWeighted(W, goalsMg, shotsMg, cornersMg, cardsMg, foulsMg, offsMg);
            var linesTeams = BuildTeamLinesWeighted(W, dto.Home, dto.Away, goalsMg, shotsMg, cornersMg);
            // SEGNALI sintetici per corner, cartellini, falli, fuorigioco, tiri
            var (aiSignalsBlock, tgSignalsBlock) = BuildSignalsBlocks(
                cornersMg,
                cardsMg,
                foulsMg,
                offsMg,
                shotsMg
            );

            // Candidati team-total (gol/corner/tiri ecc.) costruiti da JSON analitico
            var teamCandidatesText = BuildTeamCandidates(
                goalsObj,
                cornersObj,
                cardsObj,
                shotsObj,
                p,
                dto.Home,
                dto.Away
            );

            // 5) Prompt AI: niente numeri in output, analisi breve + mercati correlati
            var homeWDL = CountWdl(homeSeq);
            var awayWDL = CountWdl(awaySeq);

            // 5) Integra QUOTE (Odds) dentro il payload JSON che verrà passato all'AI
            // blocchi suggerimenti interni per mercati statistici (corner/tiri/cartellini...)
            string cornerSuggestionsInternal = "CORNERS_SUGGERITI_INTERNO: (non disponibile)";
            string shotsSuggestionsInternal = "SHOTS_SUGGERITI_INTERNO: (non disponibile)";
            string cardsSuggestionsInternal = "CARDS_SUGGERITI_INTERNO: (non disponibile)";
            // (se in futuro vorrai anche falli/fuorigioco, li riusiamo)

            try
            {
                // 5.a) Ricarico le odds dal DB per questo match
                var oddsForAi = await (
                    from o in _read.Odds
                    where o.Id == dto.MatchId      // 👈 STESSA LOGICA DI OnGetAsync
                    select new
                    {
                        o.Bookmaker,
                        o.Betid,
                        o.Description,
                        o.Value,
                        o.Odd,
                        o.Dateupd
                    }
                ).AsNoTracking().ToListAsync();

                // mappo in List<OddsRow> per riutilizzare la stessa struttura
                var oddsRowsForAi = oddsForAi
                    .Select(o => new OddsRow
                    {
                        Bookmaker = o.Bookmaker,
                        BetId = o.Betid,
                        Description = o.Description,
                        Value = o.Value,
                        Odd = (decimal)o.Odd,
                        DateUpdated = o.Dateupd
                    })
                    .ToList();

                // 🔹 Suggerimenti CORNER (usa logica vittorie/perse + quote reali)
                // 🔹 Suggerimenti CORNER (usa linee indicative + quote reali)
                cornerSuggestionsInternal = BuildCornerSuggestions(
                    lines,       // linee totali (gol/corner/cartellini/tiri)
                    teamLines,   // linee per squadra
                    oddsRowsForAi,
                    esitoProprietario,
                    dto.Home,
                    dto.Away
                );


                // 🔹 Suggerimenti TIRI (usa linee indicative + quote reali)
                // "lines" e "teamLines" li hai già calcolati poco sopra con:
                // var lines = BuildIndicativeLines(...);
                // var teamLines = BuildTeamLines(...);
                shotsSuggestionsInternal = BuildShotsSuggestions(
                    lines,
                    teamLines,
                    oddsRowsForAi,
                    dto.Home,
                    dto.Away
                );

                // 🔹 Suggerimenti CARTELLINI: per ora solo totali (nessun split squadra)
                cardsSuggestionsInternal = BuildCardsSuggestions(
                    lines,
                    oddsRowsForAi
                );




                // Se vuoi essere più preciso sul matchid usa:
                // where o.Id == dto.MatchId

                // 5.b) Parse del payload esistente (se vuoto, creo un root vuoto)
                JsonObject rootPayload;
                if (!string.IsNullOrWhiteSpace(analysisPayload))
                {
                    var parsed = JsonNode.Parse(analysisPayload);
                    rootPayload = parsed as JsonObject ?? new JsonObject();
                }
                else
                {
                    rootPayload = new JsonObject();
                }

                // 5.c) Costruisco l'array JSON delle odds
                var oddsArray = new JsonArray();

                foreach (var o in oddsForAi)
                {
                    var obj = new JsonObject
                    {
                        ["Bookmaker"] = o.Bookmaker,       // int (es. 8 = Bet365)
                        ["BetId"] = o.Betid,
                        ["Description"] = o.Description,
                        ["Value"] = o.Value,
                        ["Odd"] = o.Odd,
                        ["DateUpdated"] = o.Dateupd
                    };

                    oddsArray.Add(obj);
                }

                // 5.d) Inietto l'array "odds" nel payload
                rootPayload["odds"] = oddsArray;

                // 5.e) Aggiorno la stringa analysisPayload che viene passata al prompt
                analysisPayload = rootPayload.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = false
                });
            }
            catch
            {
                // Se qualcosa va storto, non bloccare la generazione: semplicemente niente odds nel payload
            }

            var prompt = $@"
Agisci come analista calcistico professionista. Devi generare un testo UNICO, discorsivo e fluido (NO sezioni, NO punti elenco), con linguaggio tecnico ma naturale, rivolto a un pubblico che già mastica scommesse e statistiche.

Hai a disposizione:
- Dati proprietari (esito, GG/NG, over/under, multigoal, simulazioni).
- Stato di forma (W/D/L), contesto classifica e motivazioni.
- Statistiche già parsate: tiri, corner, falli, cartellini, fuorigioco, gol/over.
- Linee indicative interne (totale match e per squadra).
- SUGGERIMENTI calcolati a backend (corner, tiri, cartellini).
- Le QUOTE reali (odds) con le linee di mercato (gol, corner, tiri, team totals).
- Tutte le medie statistiche disponibili per V/P/L.
- I dati completi del match.
- L’esito proprietario che determina in modo ASSOLUTO chi è favorita.

──────────────────────────────────────────────
REGOLE NON NEGOZIABILI
──────────────────────────────────────────────

0) NON inventare numeri di quote, handicap o linee. Le quote esistono solo se sono presenti nel blocco JSON ""odds"". Se non trovi una quota, NON la scrivi.
1) NON inventare linee Over/Under (X.5) che non siano:
   - ricavate in modo coerente dalle linee indicative interne OPPURE
   - presenti nelle quote reali in ""odds"" OPPURE
   - presenti esplicitamente nei suggerimenti interni (corner/tiri/cartellini).
2) NON cambiare mai direzione (Over/Under) rispetto ai suggerimenti interni calcolati a backend:
   - se un suggerimento interno dice Over 9.5 corner totali a quota 1.80,
     tu NON puoi proporre Under 9.5 corner,
     né Under su una linea equivalente/collegata (tipo 9.0 o 10.0) contro quell’indicazione.
3) Se i suggerimenti interni su corner/tiri/cartellini sono presenti, li devi considerare PRIORITARI:
   - non li puoi ignorare;
   - non puoi ribaltarne il senso;
   - non puoi proporre un mercato opposto sugli stessi corner/tiri/cartellini.
4) Il tuo compito NON è cercare nuove idee, ma rendere discorsive e coerenti
   le indicazioni statistiche e i suggerimenti interni calcolati dal backend.
5) Quando parli di favorita/sfavorita devi SEMPRE seguire l’ESITO PROPRIETARIO, non le tue deduzioni.
6) NON citare mai numeri grezzi di medie (tipo ""13.5 corner di media"", ""18 tiri"", ""5.44 corner""):
   - puoi parlare di volumi (alto/basso), intensità, pressione, dominanza, ecc.,
   - i numeri precisi devono comparire SOLO dentro linee Over/Under e/o quote, se esistono.
7) Se non trovi una quota o una linea coerente per un certo mercato (gol/corner/tiri):
   - puoi restare più generico nel testo discorsivo,
   - ma nelle tre righe finali GOL/CORNER/TIRI devi comunque proporre una linea X.5 sensata
     (basandoti sulle linee indicative interne) SENZA inventare la quota.
8) Se i dati interni indicano chiaramente un volume atteso MOLTO superiore alla linea del book,
   non puoi scrivere frasi che lo contraddicono (esempio: se la somma attesa di corner è ben sopra 12
   e il book offre Over 9.5, NON puoi dire che ""potrebbe rimanere sotto 9.5 corner"").
9) Se non sei sicuro fra due opzioni, segui SEMPRE la direzione implicita nei suggerimenti interni.
10) Tutto ciò che arriva dai blocchi:
   - CORNERS_SUGGERITI_INTERNO
   - SHOTS_SUGGERITI_INTERNO
   - CARDS_SUGGERITI_INTERNO
   viene considerato come una sorta di ""pre-pick"" vincolante: puoi solo riscriverlo in modo discorsivo, non modificarne la sostanza.
11) Se il blocco CARDS_SUGGERITI_INTERNO contiene la frase
    ""NON PROPORRE MERCATI SPECIFICI SUI CARTELLINI"",
    ti è VIETATO proporre Over/Under sui cartellini:
    - niente mercati cartellini nel testo discorsivo,
    - niente mercati cartellini nelle tre righe finali.
    Puoi solo fare un cenno generico al fatto che il match potrebbe essere
    più o meno nervoso, ma SENZA associare una linea o un Over/Under.

──────────────────────────────────────────────
CONTESTO MATCH
──────────────────────────────────────────────
Lega: {dto.LeagueName}
Partita: {dto.Home} vs {dto.Away}
Esito proprietario: {esitoProprietario}
Calcio d'inizio (locale): {dto.KickoffUtc:yyyy-MM-dd HH:mm}

FORMA (ultime 5)
- {dto.Home}: {homeSeqEmo} (W:{homeWDL.w} D:{homeWDL.d} L:{homeWDL.l})
- {dto.Away}: {awaySeqEmo} (W:{awayWDL.w} D:{awayWDL.d} L:{awayWDL.l})

CONTESTO CAMPIONATO:
{leagueContext}

DATI PROPRIETARI (riassunto):
{proprietaryContext}

LINEE INDICATIVE INTERNE (totali):
{linesTotal}

LINEE INDICATIVE PER SQUADRA (gol/corner/tiri):
{linesTeams}

──────────────────────────────────────────────
SUGGERIMENTI CALCOLATI A BACKEND (VINCOLANTI)
──────────────────────────────────────────────

Questi blocchi contengono i suggerimenti pre-calcolati dal backend.

NON puoi:
- cambiare Over in Under (o viceversa) sulle stesse linee;
- cambiare la linea (es. da 9.5 a 7.5) se espressamente indicata;
- ignorare completamente questi mercati.

Puoi solo trasformarli in un commento discorsivo e coerente.

SUGGERIMENTI CORNER (VINCOLANTI, NON MODIFICARE IL SENSO):
{cornerSuggestionsInternal}

SUGGERIMENTI TIRI (VINCOLANTI, NON MODIFICARE IL SENSO):
{shotsSuggestionsInternal}

SUGGERIMENTI CARTELLINI (VINCOLANTI, NON MODIFICARE IL SENSO):
{cardsSuggestionsInternal}

──────────────────────────────────────────────
STATISTICHE (CONTESTO, NO NUMERI GREZZI)
──────────────────────────────────────────────
Usa questo blocco per farti un’idea del modo in cui le squadre producono
gol, tiri, corner, falli, cartellini e fuorigioco. NON riportare i numeri così come sono, ma trasformali in concetti (ritmo alto/basso, pressione, pericolosità, ecc.):

{analysisContext}

SEGNALI SINTETICI (corner/cartellini/falli/fuorigioco/tiri) – SOLO PER IL TUO RAGIONAMENTO:
{aiSignalsBlock}

CANDIDATI TEAM TOTAL (gol/corner/tiri) – SOLO PER IL TUO RAGIONAMENTO, NON COPIARE ALLA LETTERA:
{teamCandidatesText}

──────────────────────────────────────────────
PAYLOAD JSON COMPLETO (STATISTICHE + ODDS)
NON citarlo nel testo, usalo solo per il ragionamento:
{analysisPayload}

──────────────────────────────────────────────
FORMATO DELLA RISPOSTA (OBBLIGATORIO)
──────────────────────────────────────────────

1️⃣ TESTO DISCORSIVO PRINCIPALE
Scrivi un unico blocco di 5–12 frasi, SENZA punti elenco, dove:

- Spieghi perché {dto.Home} o {dto.Away} è favorita in base all’ESITO PROPRIETARIO (non dedurre da altro).
- Colleghi forma, classifica e stile di gioco alle aspettative su:
  - andamento del match,
  - gol,
  - volume di tiri,
  - volume di corner.
- Cita almeno una volta {dto.Home} e almeno una volta {dto.Away} quando parli di favorita/sfavorita.
- Se i suggerimenti interni indicano un mercato forte (es. Over corner, Over tiri, ecc.),
  inserisci nel testo un riferimento coerente (es. ""è lecito attendersi una partita con molti corner a favore di..."").

2️⃣ TRE RIGHE FINALI STRUTTURATE
Alla FINE del testo discorsivo, su tre righe separate, DEVI aggiungere ESATTAMENTE questo schema:

GOL: [descrizione mercato sui gol con Over/Under X.5, specificando se totali o di squadra, e QUOTA se presente nelle ""odds""]
CORNER: [descrizione mercato sui corner con Over/Under X.5, specificando se totali o di squadra, e QUOTA se presente nelle ""odds""]
TIRI: [descrizione mercato sui tiri con Over/Under X.5, specificando se totali o di squadra, e QUOTA se presente nelle ""odds""]

Regole per queste tre righe:

- Ogni riga DEVE iniziare con la parola esatta: ""GOL:"", ""CORNER:"", ""TIRI:"".
- Ogni riga DEVE contenere una linea con .5 (1.5, 2.5, 3.5, 9.5, 10.5, ecc.).
- Specifica sempre se si tratta di:
  - gol/corner/tiri TOTALI oppure
  - gol/corner/tiri di una singola squadra (es. {dto.Home} o {dto.Away}).
- Se nel payload ""odds"" esiste una quota per quella linea e per quel mercato:
  - scrivi: ""a quota X.XX su Bet365"" (es. ""Over 2.5 gol totali a quota 1.85 su Bet365"").
  - NON inventare mai Bet365 se il bookmaker è diverso o assente.
- Se NON trovi una quota per quella linea:
  - NON inventare la quota numerica,
  - NON nominare Bet365 o altri bookmaker,
  - scrivi solo la linea (es. ""Over 2.5 gol totali"").

- Se i suggerimenti interni impongono una certa direzione (es. Over corner totali):
  - la riga CORNER DEVE essere coerente con quella direzione (non puoi proporre l’Under sulla stessa linea).
- In caso di dubbio fra più linee possibili, scegli quella che:
  - è coerente con i suggerimenti interni,
  - è realmente presente in ""odds"" (se stai usando anche la quota).

3️⃣ CHECK FINALE PRIMA DI RESTITUIRE LA RISPOSTA

Prima di concludere, verifica che:

- Nel testo compaiano correttamente {dto.Home} e {dto.Away}.
- Non hai scritto frasi che contraddicono chiaramente i suggerimenti interni
  (es. testo che parla di pochi corner quando i suggerimenti interni spingono forte per Over corner).
- La riga GOL contiene: Over/Under X.5 gol totali o di squadra.
- La riga CORNER contiene: Over/Under X.5 corner totali o di squadra.
- La riga TIRI contiene: Over/Under X.5 tiri totali o di squadra.
- Se citi una QUOTA X.XX, quella linea esiste davvero dentro ""odds"".
- Non ci sono numeri di medie grezze (tiri medi, corner medi, ecc.) fuori dalle linee e dalle quote.

RISPONDI SOLO CON IL TESTO FINALE (analisi + le tre righe GOL/CORNER/TIRI).";




            // 6) Chiamata AI
            string aiText;
            try
            {
                var raw = await _openai.AskAsync(prompt);
                aiText = string.IsNullOrWhiteSpace(raw) ? "" : raw.Trim();
                aiText = NormalizeHalfLines(aiText);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[AI][ERR] " + ex);
                StatusMessage = "⚠️ AI non disponibile. Riprova tra poco.";
                return RedirectToPage(new { id });
            }

            if (string.IsNullOrWhiteSpace(aiText))
            {
                StatusMessage = "⚠️ Nessun testo generato dall'AI.";
                return RedirectToPage(new { id });
            }

            // 7) Header e preview finale
            var flag = EmojiHelper.FromCountryCode(dto.CountryCode);
            var header =
                $"{flag} <b>{dto.LeagueName}</b> 🕒 {dto.KickoffUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n" +
                $"⚽️ {dto.Home} - {dto.Away}\n\n";

            var preview = header + aiText;

            TempData["AiPreview"] = preview;
            StatusMessage = "✅ Anteprima generata. Controlla e premi Invia.";
            return RedirectToPage(new { id });

            // =============== HELPERS LOCALI ===============
            static string BuildAnalysisContextForAi(string? json, string home, string away)
            {
                if (string.IsNullOrWhiteSpace(json))
                    return "(nessun payload analitico disponibile)";

                try
                {
                    var root = JsonNode.Parse(json)!.AsObject();

                    string ReadBlock(string blockName, params string[] alias)
                    {
                        var obj = root
                            .Where(kv => string.Equals(kv.Key, blockName, StringComparison.OrdinalIgnoreCase)
                                || alias.Any(a => string.Equals(kv.Key, a, StringComparison.OrdinalIgnoreCase)))
                            .Select(kv => kv.Value as JsonObject)
                            .FirstOrDefault();

                        if (obj is null || obj.Count == 0) return "";

                        var normalized = obj
                            .Where(kv => kv.Value is not null)
                            .Select(kv => new { Key = (kv.Key ?? "").Trim(), Val = kv.Value!.ToString()?.Trim() })
                            .Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Val))
                            .ToList();

                        if (normalized.Count == 0) return "";

                        var pairs = new Dictionary<string, (string? casa, string? ospite)>(StringComparer.OrdinalIgnoreCase);

                        foreach (var item in normalized)
                        {
                            var keyFull = item.Key;
                            var lastDash = keyFull.LastIndexOf('-');
                            var baseName = lastDash > 0 ? keyFull.Substring(0, lastDash).Trim() : keyFull;
                            var side = lastDash > 0 ? keyFull[(lastDash + 1)..].Trim() : "";

                            bool isHome = side.Equals("Casa", StringComparison.OrdinalIgnoreCase) ||
                                          side.Equals("Home", StringComparison.OrdinalIgnoreCase);
                            bool isAway = side.Equals("Ospite", StringComparison.OrdinalIgnoreCase) ||
                                          side.Equals("Away", StringComparison.OrdinalIgnoreCase);

                            if (!pairs.TryGetValue(baseName, out var t)) t = (null, null);
                            if (isHome) t.casa = item.Val;
                            else if (isAway) t.ospite = item.Val;
                            else
                            {
                                if (!pairs.ContainsKey(baseName))
                                    pairs[baseName] = (item.Val, item.Val);
                                continue;
                            }
                            pairs[baseName] = t;
                        }

                        string[] desiredOrder = new[] {
                    "Effettuati","Battuti","Fatti","Subiti","In Casa","Fuoricasa",
                    "Ultime 5","Partite Vinte","Partite Pareggiate","Partite Perse",
                    "% Over 1.5 Casa","% Over 1.5 Trasferta","% Over 1.5 Totale",
                    "% Over 2.5 Casa","% Over 2.5 Trasferta","% Over 2.5 Totale",
                    "% Over 3.5 Casa","% Over 3.5 Trasferta","% Over 3.5 Totale"
                };

                        var rows = pairs.Keys
                            .OrderBy(k => { var i = Array.IndexOf(desiredOrder, k); return i < 0 ? int.MaxValue : i; })
                            .ThenBy(k => k)
                            .Select(k =>
                            {
                                var (casa, ospite) = pairs[k];
                                return $"• {k}: {home} {casa ?? "—"} | {away} {ospite ?? "—"}";
                            });

                        var title = blockName.ToLower() switch
                        {
                            "goals" or "goal" => "GOAL/OVER",
                            "shots" => "TIRI",
                            "corners" => "CORNER",
                            "fouls" => "FALLI",
                            "cards" => "CARTELLINI",
                            "offsides" => "FUORIGIOCO",
                            _ => blockName.ToUpperInvariant()
                        };

                        return $"{title}\n{string.Join("\n", rows)}";
                    }

                    var parts = new List<string>
            {
                ReadBlock("goals", "goal", "goalAttesi"),
                ReadBlock("shots", "tiri"),
                ReadBlock("corners", "corner"),
                ReadBlock("fouls", "falli"),
                ReadBlock("cards", "cartellini"),
                ReadBlock("offsides", "fuorigioco")
            };

                    var filtered = parts.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                    if (filtered.Count == 0) return "(nessuna sezione analitica riconosciuta)";

                    var ctx = string.Join("\n\n", filtered);
                    if (ctx.Length > 1200) ctx = ctx[..1200] + "...";
                    return ctx;
                }
                catch
                {
                    return "(errore nel parsing del payload analitico)";
                }
            }

            static string BuildLeagueContextForAi(List<TableStandingRow> standings, long homeId, long awayId, int remainingRounds)
            {
                if (standings == null || standings.Count == 0)
                    return "(nessun contesto di classifica disponibile)";

                var ordered = standings.OrderBy(s => s.Rank).ToList();
                var n = ordered.Count;

                var home = ordered.FirstOrDefault(s => s.TeamId == homeId);
                var away = ordered.FirstOrDefault(s => s.TeamId == awayId);

                int maxPlayed = ordered.Max(s => s.Played);
                int totalRounds = maxPlayed + Math.Max(remainingRounds, 0);
                if (totalRounds <= 0) totalRounds = maxPlayed;

                var first = ordered.First();
                var second = ordered.Skip(1).FirstOrDefault();
                int lead = second != null ? first.Points - second.Points : 0;

                bool titleLocked = remainingRounds > 0 && lead > 3 * remainingRounds;
                bool titleAlmost = remainingRounds > 0 && !titleLocked && lead >= 2 * remainingRounds;

                int bottomSlots = Math.Min(3, Math.Max(1, n / 4)); // 3 o ~25% della lega
                if (bottomSlots >= n) bottomSlots = Math.Max(1, n - 1);

                int lastSafeIndex = n - bottomSlots - 1;
                if (lastSafeIndex < 0) lastSafeIndex = 0;
                var lastSafe = ordered[lastSafeIndex];
                var relegationTeams = ordered.Skip(n - bottomSlots).ToList();

                bool IsRelegated(TableStandingRow t)
                {
                    if (remainingRounds <= 0) return t.Rank > lastSafe.Rank;
                    int matchesLeft = Math.Max(0, totalRounds - t.Played);
                    int maxPossible = t.Points + matchesLeft * 3;
                    return maxPossible < lastSafe.Points;
                }

                bool IsSafe(TableStandingRow t)
                {
                    if (remainingRounds <= 0) return t.Rank <= lastSafe.Rank;
                    int matchesLeft = Math.Max(0, totalRounds - t.Played);
                    int worstSafePoints = t.Points; // ci basta confrontare con il migliore tra i retrocessi
                    int bestReleg = relegationTeams.Count > 0 ? relegationTeams.Max(x => x.Points) : 0;
                    int maxRelegPossible = bestReleg + remainingRounds * 3;
                    return worstSafePoints > maxRelegPossible;
                }

                string Status(TableStandingRow? t)
                {
                    if (t is null) return "status non disponibile";

                    var tags = new List<string>();

                    // titolo / parte alta
                    if (titleLocked && t.TeamId == first.TeamId)
                        tags.Add("titolo già in tasca, possibile gestione ritmi e rotazioni");
                    else if (titleAlmost && t.TeamId == first.TeamId)
                        tags.Add("titolo molto vicino, ma ancora bisogno di chiudere il discorso");
                    else if (!titleLocked && t.Rank <= 3)
                        tags.Add("zona alta, ancora motivazioni per titolo/Europa");

                    // salvezza / retrocessione
                    if (IsRelegated(t))
                        tags.Add("situazione praticamente compromessa in zona retrocessione");
                    else if (!IsSafe(t) && t.Rank >= lastSafe.Rank - 1)
                        tags.Add("piena lotta salvezza, ogni punto pesa tantissimo");
                    else if (IsSafe(t))
                        tags.Add("zona tranquilla, lontana dalla retrocessione");

                    if (tags.Count == 0)
                        return "situazione di classifica relativamente neutra";

                    return string.Join("; ", tags);
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Giornate rimanenti stimate: {remainingRounds}.");

                if (home != null)
                {
                    int homeLeft = Math.Max(0, totalRounds - home.Played);
                    sb.AppendLine($"{home.TeamName}: {home.Rank}ª con {home.Points} punti, circa {homeLeft} gare ancora da giocare → {Status(home)}.");
                }

                if (away != null)
                {
                    int awayLeft = Math.Max(0, totalRounds - away.Played);
                    sb.AppendLine($"{away.TeamName}: {away.Rank}ª con {away.Points} punti, circa {awayLeft} gare ancora da giocare → {Status(away)}.");
                }

                if (titleLocked)
                    sb.AppendLine("Il titolo è di fatto assegnato: attenzione a possibili cali di intensità della capolista nelle ultime partite.");
                else if (titleAlmost)
                    sb.AppendLine("La corsa al titolo è vicina alla chiusura: la capolista potrebbe voler evitare rischi inutili ma resta motivata.");

                return sb.ToString().TrimEnd();
            }
        }




        // === 2) INVIO: prende il testo dall'anteprima (hidden) e invia sul topic "Idee" ===
        public async Task<IActionResult> OnPostSendAiPickAsync(long id, string preview)
        {
            if (!User.HasClaim("plan", "1"))
                return Forbid();

            if (string.IsNullOrWhiteSpace(preview))
            {
                StatusMessage = "❌ Nessun contenuto da inviare. Genera prima l'anteprima.";
                return RedirectToPage(new { id });
            }

            if (!long.TryParse(_config["Telegram:Topics:Idee"], out var topicId) || topicId <= 0)
            {
                StatusMessage = "❌ Topic 'Idee' non configurato (Telegram:Topics:Idee).";
                return RedirectToPage(new { id });
            }

            var dto = await (
                from mm in _read.Matches
                join lg in _read.Leagues on mm.LeagueId equals lg.Id
                join th in _read.Teams on mm.HomeId equals th.Id
                join ta in _read.Teams on mm.AwayId equals ta.Id
                where mm.Id == id
                select new
                {
                    LeagueName = lg.Name ?? "",
                    KickoffUtc = mm.Date,
                    Home = th.Name ?? "",
                    Away = ta.Name ?? "",
                    HomeLogo = th.Logo,   // <--- servono per il banner
                    AwayLogo = ta.Logo
                }
            ).AsNoTracking().FirstOrDefaultAsync();

            if (dto is null) return NotFound();

            try
            {
                // genera il banner su wwwroot/temp/match_{id}.jpg
                var bannerPath = await _banner.CreateAsync(
                    dto.Home, dto.Away,
                    dto.HomeLogo, dto.AwayLogo,
                    dto.LeagueName,
                    dto.KickoffUtc.ToLocalTime(),
                    id
                );

                if (!string.IsNullOrWhiteSpace(bannerPath) && System.IO.File.Exists(bannerPath))
                    await _telegram.SendPhotoAsync(topicId, bannerPath, preview); // upload file locale
                else
                    await _telegram.SendMessageAsync(topicId, preview);           // fallback solo testo

                StatusMessage = "✅ Inviato su Telegram (Idee).";
            }
            catch
            {
                await _telegram.SendMessageAsync(topicId, preview);
                StatusMessage = "⚠️ Immagine non generata, inviato solo testo su Telegram (Idee).";
            }

            return RedirectToPage(new { id });
        }

        public async Task<IActionResult> OnPostSendAiPickWithImageAsync(long id, string preview)
        {
            if (!User.HasClaim("plan", "1"))
                return Forbid();

            if (string.IsNullOrWhiteSpace(preview))
            {
                StatusMessage = "❌ Nessun contenuto da inviare. Genera prima l'anteprima.";
                return RedirectToPage(new { id });
            }

            if (!long.TryParse(_config["Telegram:Topics:Idee"], out var topicId) || topicId <= 0)
            {
                StatusMessage = "❌ Topic 'Idee' non configurato (Telegram:Topics:Idee).";
                return RedirectToPage(new { id });
            }

            // Ricarico i metadati necessari per il banner (inclusi loghi)
            var dto = await (
                from mm in _read.Matches
                join lg in _read.Leagues on mm.LeagueId equals lg.Id
                join th in _read.Teams on mm.HomeId equals th.Id
                join ta in _read.Teams on mm.AwayId equals ta.Id
                where mm.Id == id
                select new
                {
                    MatchId = mm.Id,
                    LeagueName = lg.Name ?? "",
                    Home = th.Name ?? "",
                    Away = ta.Name ?? "",
                    HomeLogo = th.Logo,
                    AwayLogo = ta.Logo,
                    KickoffUtc = mm.Date
                }
            ).AsNoTracking().FirstOrDefaultAsync();

            if (dto is null)
            {
                StatusMessage = "❌ Match non trovato.";
                return RedirectToPage(new { id });
            }

            // Genera banner (usa ora locale per coerenza col testo)
            var bannerPath = await _banner.CreateAsync(
                dto.Home, dto.Away,
                dto.HomeLogo, dto.AwayLogo,
                dto.LeagueName,
                dto.KickoffUtc.ToLocalTime(),
                dto.MatchId
            );

            // Invia la foto con caption breve (prima riga del preview)
            // Telegram ha limiti: meglio una caption corta. Mettiamo header + 1 riga,
            // e poi inviamo il resto come messaggio di testo a parte.
            var caption = preview.Length > 900 ? preview[..900] + "…" : preview;

            await _telegram.SendPhotoAsync(topicId, bannerPath, caption);

            // Se vuoi, invia anche il testo completo in un messaggio separato:
            // await _telegram.SendMessageAsync(topicId, preview);

            StatusMessage = "✅ Immagine e testo inviati su Telegram (Idee).";
            return RedirectToPage(new { id });
        }

        // =======================
        // GET: carica dati + analyses da Neon
        // =======================
        public async Task<IActionResult> OnGetAsync(long id)
        {
            var dto = await (
                from mm in _read.Matches
                join lg in _read.Leagues on mm.LeagueId equals lg.Id
                join th in _read.Teams on mm.HomeId equals th.Id
                join ta in _read.Teams on mm.AwayId equals ta.Id
                where mm.Id == id
                select new
                {
                    MatchId = mm.Id,
                    LeagueId = mm.LeagueId,
                    Season = mm.Season,
                    LeagueName = lg.Name,
                    LeagueLogo = lg.Logo,
                    CountryName = lg.CountryName,
                    HomeId = th.Id,
                    AwayId = ta.Id,
                    Home = th.Name,
                    Away = ta.Name,
                    HomeLogo = th.Logo,
                    AwayLogo = ta.Logo,
                    KickoffUtc = mm.Date
                }
            ).AsNoTracking().FirstOrDefaultAsync();

            if (dto is null) return NotFound();

            var userId = _userManager.GetUserId(User)!;
            var isFav = await _write.FavoriteMatches.AnyAsync(f => f.UserId == userId && f.MatchId == id);

            // Carichi accessori
            var remaining = await GetRemainingMatchesAsync(dto.LeagueId, dto.Season);
            var homeForm = await GetLastFiveAsync(dto.HomeId, dto.LeagueId, dto.Season);
            var awayForm = await GetLastFiveAsync(dto.AwayId, dto.LeagueId, dto.Season);
            var standings = await GetStandingsAsync(dto.LeagueId, dto.Season);

            // Prediction & Exchange da Analyses
            var prediction = await LoadPredictionAsync(id);
            var exchange = await LoadExchangeAsync(id);

            // Analyses generiche (dizionari)
            var goals = await RunAnalysisAsync("NextMatchGoals_Analyses", dto.LeagueId, dto.Season, (int)id);
            var shots = await RunAnalysisAsync("NextMatchShots_Analyses", dto.LeagueId, dto.Season, (int)id);
            var corners = await RunAnalysisAsync("NextMatchCorners_Analyses", dto.LeagueId, dto.Season, (int)id);
            var cards = await RunAnalysisAsync("NextMatchCards_Analyses", dto.LeagueId, dto.Season, (int)id);
            var fouls = await RunAnalysisAsync("NextMatchFouls_Analyses", dto.LeagueId, dto.Season, (int)id);
            var offsides = await RunAnalysisAsync("NextMatchOffsides_Analyses", dto.LeagueId, dto.Season, (int)id);
            // =======================
            // QUOTE da Neon (tabella odds)
            // =======================
            // ATTENZIONE: se i nomi delle colonne nella entity differiscono (Id, Bookmaker, Betid, Description, Value, Odd, Dateupd),
            // adeguali qui di conseguenza.
            var odds = await (
                from o in _read.Odds
                where o.Id == dto.MatchId   // Id = matchid
                select new OddsRow
                {
                    Bookmaker = o.Bookmaker,
                    BetId = o.Betid,
                    Description = o.Description,
                    Value = o.Value,
                    Odd = (decimal)o.Odd,
                    DateUpdated = o.Dateupd
                }
            ).AsNoTracking().ToListAsync();

            var betIds = odds
                .Select(o => o.BetId)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            Data = new VM
            {
                MatchId = dto.MatchId,
                LeagueId = dto.LeagueId,
                Season = dto.Season,
                LeagueName = dto.LeagueName ?? "",
                LeagueLogo = dto.LeagueLogo,
                CountryName = dto.CountryName,
                HomeId = dto.HomeId,
                AwayId = dto.AwayId,
                Home = dto.Home ?? "",
                Away = dto.Away ?? "",
                HomeLogo = dto.HomeLogo,
                AwayLogo = dto.AwayLogo,
                KickoffUtc = dto.KickoffUtc,
                IsFavorite = isFav,
                RemainingMatches = remaining,

                Prediction = prediction,
                Exchange = exchange,

                Goals = goals,
                Shots = shots,
                Corners = corners,
                Cards = cards,
                Fouls = fouls,
                Offsides = offsides,

                HomeForm = homeForm,
                AwayForm = awayForm,
                Standings = standings,
                Odds = odds,
                BetIds = betIds
            };

            return Page();
        }


        // =======================
        // ⭐ Toggle preferito
        // =======================
        public async Task<IActionResult> OnPostToggleFavoriteAsync(long id)
        {
            var userId = _userManager.GetUserId(User)!;
            var existing = await _write.FavoriteMatches.FirstOrDefaultAsync(f => f.UserId == userId && f.MatchId == id);

            if (existing == null)
                _write.FavoriteMatches.Add(new FavoriteMatch { UserId = userId, MatchId = id });
            else
                _write.FavoriteMatches.Remove(existing);

            await _write.SaveChangesAsync();
            return RedirectToPage(new { id });
        }

        // =======================
        // INVIO PRONOSTICI (topicName -> id da config)
        // =======================

        public async Task<IActionResult> OnPostSendPredictionAsync(long id, string? topicName, string? customPick)
        {
            if (!User.HasClaim("plan", "1"))
                return Forbid();

            var key = string.IsNullOrWhiteSpace(topicName) ? "PronosticiDaPubblicare" : topicName!;
            long.TryParse(_config[$"Telegram:Topics:{key}"], out var topicId);

            var dto = await (
               from mm in _read.Matches
               join lg in _read.Leagues on mm.LeagueId equals lg.Id
               join th in _read.Teams on mm.HomeId equals th.Id
               join ta in _read.Teams on mm.AwayId equals ta.Id
               where mm.Id == id
               select new
               {
                   LeagueName = lg.Name,
                   CountryCode = lg.CountryCode,
                   KickoffUtc = mm.Date,
                   Home = th.Name ?? "",
                   Away = ta.Name ?? ""
               }
            ).AsNoTracking().FirstOrDefaultAsync();

            if (dto is null) return NotFound();

            var p = await LoadPredictionAsync(id);
            if (p is not null && !string.IsNullOrWhiteSpace(customPick))
                p.ComboFinale = customPick.Trim();

            var flag = EmojiHelper.FromCountryCode(dto.CountryCode);

            string message =
                $"{flag} <b>{dto.LeagueName}</b> 🕒 {dto.KickoffUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n" +
                $"⚽️ {dto.Home} - {dto.Away}\n\n" +
                $"🧠 <b>Pronostici:</b>\n" +
                $"  Esito: {p?.Esito}\n" +
                $"  U/O: {p?.OverUnderRange}\n" +
                $"  GG/NG: {p?.GG_NG}\n" +
                $"  MG Casa: {p?.MultigoalCasa}\n" +
                $"  MG Ospite: {p?.MultigoalOspite}\n\n" +
                $"<b>Pronostico Consigliato:</b>\n{p?.ComboFinale}";

            await _telegram.SendMessageAsync(topicId, message);
            TempData["StatusMessage"] = "✅ Pronostico inviato su Telegram.";
            return RedirectToPage(new { id });
        }
        public async Task<IActionResult> OnPostSendOddsIdeaAsync(
            long id,
            string topicName,
            int betId,
            string marketDescription,
            string selectionValue,
            string odd)
        {
            // 🔐 Controllo plan = 1
            var hasPlan1 = User.HasClaim(c =>
                (string.Equals(c.Type, "plan", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(c.Type, "Plan", StringComparison.OrdinalIgnoreCase)) &&
                string.Equals((c.Value ?? "").Trim(), "1", StringComparison.Ordinal));

            if (!hasPlan1)
                return Forbid();

            // 🎯 Recupero Topic Telegram (come fai negli altri handler)
            if (string.IsNullOrWhiteSpace(topicName))
                topicName = "PronosticiDaPubblicare";

            long.TryParse(_config[$"Telegram:Topics:{topicName}"], out var topicId);

            // 📌 Carico i dati della partita ESATTAMENTE come in OnPostSendExchangeAsync
            var dto = await (
                from mm in _read.Matches
                join lg in _read.Leagues on mm.LeagueId equals lg.Id
                join th in _read.Teams on mm.HomeId equals th.Id
                join ta in _read.Teams on mm.AwayId equals ta.Id
                where mm.Id == id
                select new
                {
                    LeagueName = lg.Name,
                    CountryCode = lg.CountryCode,
                    KickoffUtc = mm.Date,
                    Home = th.Name ?? "",
                    Away = ta.Name ?? ""
                }
            ).AsNoTracking().FirstOrDefaultAsync();

            if (dto is null)
            {
                TempData["StatusMessage"] = "Partita non trovata per l’invio della quota.";
                return RedirectToPage(new { id });
            }

            string flag = EmojiHelper.FromCountryCode(dto.CountryCode);

            // Format della quota
            if (!decimal.TryParse(
                odd,
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var oddDec))
            {
                oddDec = 0;
            }

            string oddFormatted = oddDec.ToString("0.00", CultureInfo.InvariantCulture);

            // 📄 Pronostico da inviare
            string pronostico = string.IsNullOrWhiteSpace(marketDescription)
                ? selectionValue
                : $"{selectionValue} {marketDescription}";

            // 📝 Messaggio Telegram
            string message =
                $"{flag} <b>{dto.LeagueName}</b> 🕒 {dto.KickoffUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n" +
                $"⚽️ {dto.Home} - {dto.Away}\n" +
                $"Pronostico: {pronostico} - Quota: {oddFormatted}";

            // 📲 Invio Telegram usando IL TUO servizio
            await _telegram.SendMessageAsync(topicId, message);

            TempData["StatusMessage"] = "Quota inviata al canale Idee!";
            return RedirectToPage(new { id });
        }

        // =======================
        // INVIO EXCHANGE (topicName -> id da config)
        // =======================

        public async Task<IActionResult> OnPostSendExchangeAsync(long id, string customLay, string riskLevel, string? topicName)
        {
            if (!User.HasClaim("plan", "1"))
                return Forbid();

            var key = string.IsNullOrWhiteSpace(topicName) ? "ExchangeDaPubblicare" : topicName!;
            long.TryParse(_config[$"Telegram:Topics:{key}"], out var topicId);

            var dto = await (
                from mm in _read.Matches
                join lg in _read.Leagues on mm.LeagueId equals lg.Id
                join th in _read.Teams on mm.HomeId equals th.Id
                join ta in _read.Teams on mm.AwayId equals ta.Id
                where mm.Id == id
                select new
                {
                    LeagueName = lg.Name,
                    CountryCode = lg.CountryCode,
                    KickoffUtc = mm.Date,
                    Home = th.Name ?? "",
                    Away = ta.Name ?? ""
                }
            ).AsNoTracking().FirstOrDefaultAsync();

            if (dto is null) return NotFound();

            string flag = EmojiHelper.FromCountryCode(dto.CountryCode);
            bool isScore = customLay.Contains('-');
            string tipoBancata = isScore ? "Banca Risultato Esatto" : "Banca 1X2";

            string message =
                $"{flag} <b>{dto.LeagueName}</b> 🕒 {dto.KickoffUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n" +
                $"⚽️ {dto.Home} - {dto.Away}\n\n" +
                $"🧠 <b>Bancate consigliate:</b>\n" +
                $"♦️ {tipoBancata}: {customLay} ⚠️ Rischio: {riskLevel}";

            await _telegram.SendMessageAsync(topicId, message);

            TempData["MessageSent"] = "✅ Messaggio Exchange inviato con successo!";
            return RedirectToPage(new { id });
        }

        // =======================
        // Loader Prediction
        // =======================
        private async Task<PredictionRow?> LoadPredictionAsync(long matchId)
        {
            var script = await _read.Analyses
                .Where(a => a.ViewName == "NextMatch_Prediction_New")
                .Select(a => a.ViewValue)
                .AsNoTracking()
                .FirstOrDefaultAsync();

            if (string.IsNullOrWhiteSpace(script)) return null;

            var cs = _read.Database.GetConnectionString();
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(script, conn);
            cmd.Parameters.AddWithValue("@MatchId", (int)matchId);

            await using var rd = await cmd.ExecuteReaderAsync();
            if (!await rd.ReadAsync()) return null;

            return new PredictionRow
            {
                Id = GetField<int>(rd, "Id"),
                GoalSimulatoCasa = GetField<int>(rd, "Goal Simulati Casa"),
                GoalSimulatoOspite = GetField<int>(rd, "Goal Simulati Ospite"),
                TotaleGoalSimulati = GetField<int>(rd, "Totale Goal Simulati"),
                Esito = GetField<string>(rd, "Esito"),
                OverUnderRange = GetField<string>(rd, "OverUnderRange"),
                Over1_5 = GetField<decimal?>(rd, "Over1_5"),
                Over2_5 = GetField<decimal?>(rd, "Over2_5"),
                Over3_5 = GetField<decimal?>(rd, "Over3_5"),
                GG_NG = GetField<string>(rd, "GG_NG"),
                MultigoalCasa = GetField<string>(rd, "MultigoalCasa"),
                MultigoalOspite = GetField<string>(rd, "MultigoalOspite"),
                ComboFinale = GetField<string>(rd, "ComboFinale")
            };
        }

        // =======================
        // Loader Exchange
        // =======================
        private async Task<ExchangePredictionRow?> LoadExchangeAsync(long matchId)
        {
            var script = await _read.Analyses
                .Where(a => a.ViewName == "NextMatch_Prediction_Exchange")
                .Select(a => a.ViewValue)
                .AsNoTracking()
                .FirstOrDefaultAsync();

            if (string.IsNullOrWhiteSpace(script)) return null;

            var cs = _read.Database.GetConnectionString();
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(script, conn);
            cmd.Parameters.AddWithValue("@MatchId", (int)matchId);

            await using var rd = await cmd.ExecuteReaderAsync();
            if (!await rd.ReadAsync()) return null;

            return new ExchangePredictionRow
            {
                MatchId = GetField<long>(rd, "MatchId"),
                Banca1Affidabilita = GetField<int?>(rd, "Banca 1 - Affidabilità %"),
                BancaXAffidabilita = GetField<int?>(rd, "Banca X - Affidabilità %"),
                Banca2Affidabilita = GetField<int?>(rd, "Banca 2 - Affidabilità %"),
                BancataConsigliata = GetField<string>(rd, "Bancata consigliata"),
                BancaRisultato1 = GetField<string>(rd, "Banca Risultato 1"),
                BancaRisultato2 = GetField<string>(rd, "Banca Risultato 2"),
                BancaRisultato3 = GetField<string>(rd, "Banca Risultato 3")
            };
        }

        // =======================
        // Analyses generiche (ritorna MetricGroup.Metrics)
        // =======================
        private async Task<MetricGroup?> RunAnalysisAsync(string viewName, int leagueId, int season, int matchId)
        {
            var script = await _read.Analyses
                .Where(a => a.ViewName == viewName)
                .Select(a => a.ViewValue)
                .AsNoTracking()
                .FirstOrDefaultAsync();

            if (string.IsNullOrWhiteSpace(script)) return null;

            var cs = _read.Database.GetConnectionString();
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(script, conn);
            cmd.Parameters.AddWithValue("@MatchId", matchId);
            cmd.Parameters.AddWithValue("@Season", season);
            cmd.Parameters.AddWithValue("@LeagueId", leagueId);

            await using var rd = await cmd.ExecuteReaderAsync();
            if (!await rd.ReadAsync()) return null;

            var mg = new MetricGroup { Metrics = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) };
            for (int i = 0; i < rd.FieldCount; i++)
            {
                var name = rd.GetName(i);
                if (name.Equals("Id", StringComparison.OrdinalIgnoreCase) || name.Equals("MatchId", StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = rd.IsDBNull(i) ? "—" : Convert.ToString(rd.GetValue(i)) ?? "—";
                mg.Metrics[name] = value;
            }
            return mg;
        }

        private async Task<int> GetRemainingMatchesAsync(int leagueId, int season)
        {
            return await _read.Matches
               .Where(m => m.LeagueId == leagueId && m.Season == season && (m.StatusShort == null || m.StatusShort != "FT"))
               .Select(m => m.MatchRound)
               .Distinct()
               .CountAsync();
        }

        private async Task<List<FormRow>> GetLastFiveAsync(long teamId, int leagueId, int season)
        {
            var finished = new[] { "FT", "AET", "PEN" };

            var rows = await (
                from m in _read.Matches
                where m.LeagueId == leagueId && m.Season == season
                      && finished.Contains(m.StatusShort!)
                      && (m.HomeId == teamId || m.AwayId == teamId)
                orderby m.Date descending
                select new { m.Id, m.Date, m.HomeId, m.AwayId, m.HomeGoal, m.AwayGoal }
            ).AsNoTracking().Take(5).ToListAsync();

            var result = new List<FormRow>(rows.Count);
            foreach (var r in rows)
            {
                bool wasHome = r.HomeId == teamId;
                long opponentId = wasHome ? r.AwayId : r.HomeId;

                var oppName = await _read.Teams.Where(t => t.Id == opponentId).Select(t => t.Name).FirstOrDefaultAsync() ?? "—";

                int gf = wasHome ? (r.HomeGoal ?? 0) : (r.AwayGoal ?? 0);
                int ga = wasHome ? (r.AwayGoal ?? 0) : (r.HomeGoal ?? 0);
                string res = gf > ga ? "W" : (gf == ga ? "D" : "L");

                result.Add(new FormRow
                {
                    MatchId = r.Id,
                    DateUtc = r.Date,
                    Opponent = oppName,
                    IsHome = wasHome,
                    Score = $"{r.HomeGoal ?? 0}-{r.AwayGoal ?? 0}",
                    Result = res
                });
            }
            return result;
        }

        private async Task<List<TableStandingRow>> GetStandingsAsync(int leagueId, int season)
        {
            return await (
                from s in _read.Standings
                join t in _read.Teams on s.TeamId equals t.Id
                where s.LeagueId == leagueId && s.Season == season
                orderby s.Rank
                select new TableStandingRow
                {
                    Rank = s.Rank ?? 0,
                    TeamId = t.Id,
                    TeamName = t.Name ?? "",
                    Points = s.Points ?? 0,
                    Played = s.AllPlayed ?? 0,
                    Win = s.AllWin ?? 0,
                    Draw = s.AllDraw ?? 0,
                    Lose = s.AllLose ?? 0,
                    GF = s.AllGoalFor ?? 0,
                    GA = s.AllGoalAgainst ?? 0,
                    Diff = s.GoalsDiff ?? 0
                }
            ).AsNoTracking().ToListAsync();
        }


        // Helper conversione field
        // Conta W/D/L su una stringa tipo "WDLWD"
        private static (int w, int d, int l) CountWdl(string seq)
        {
            if (string.IsNullOrWhiteSpace(seq)) return (0, 0, 0);
            int w = 0, d = 0, l = 0;
            foreach (var c in seq)
            {
                if (c == 'W' || c == 'w') w++;
                else if (c == 'D' || c == 'd') d++;
                else if (c == 'L' || c == 'l') l++;
            }
            return (w, d, l);
        }

        // Trasforma 'Over 1.0'/'Under 2.0' ecc. in scaglioni .5 (default verso l’alto)
        private static string NormalizeHalfLines(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            // Over/Under + spazio + X(.0)  -> porta a X.5
            // Esempi: Over 1.0 -> Over 1.5 ; Under 2.0 -> Under 2.5
            return System.Text.RegularExpressions.Regex.Replace(
                text,
                @"\b(Over|Under)\s+(\d+)(?:[.,]0)\b",
                m =>
                {
                    var ou = m.Groups[1].Value; // Over / Under
                    var n = int.Parse(m.Groups[2].Value);
                    return $"{ou} {n}.5";
                },
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
        }

        private static string FormSeqToEmojis(string seq)
        {
            if (string.IsNullOrWhiteSpace(seq)) return "—";
            var sb = new System.Text.StringBuilder(seq.Length * 2);
            foreach (var ch in seq)
            {
                sb.Append(ch switch
                {
                    'W' or 'w' => "🟩",
                    'D' or 'd' => "⬜️",
                    'L' or 'l' => "🟥",
                    _ => "▪️"
                });
            }
            return sb.ToString();
        }

        private static T? GetField<T>(IDataRecord r, string name)
        {
            int ord = r.GetOrdinal(name);
            if (r.IsDBNull(ord)) return default;
            object val = r.GetValue(ord);
            var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            // compatibilità int/long
            if (targetType == typeof(long) && val is int i) val = (long)i;
            if (targetType == typeof(int) && val is long l) val = (int)l;
            return (T)Convert.ChangeType(val, targetType);
        }
        // ------------------------ NUMERIC HELPERS ------------------------
        private static decimal? TryParseDec(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Trim();

            // togli % e simboli strani
            t = t.Replace("%", "").Replace("−", "-");

            // normalizza virgola/punto
            t = t.Replace(',', '.');

            // prendi solo numero (es. "5.20 (media)" -> "5.20")
            var span = t.AsSpan();
            int start = -1, end = -1;
            for (int i = 0; i < span.Length; i++)
            {
                if (char.IsDigit(span[i]) || span[i] == '-' || span[i] == '+')
                {
                    start = i; break;
                }
            }
            if (start == -1) return null;
            end = start;
            while (end < span.Length && (char.IsDigit(span[end]) || span[end] == '.')) end++;

            var num = new string(span[start..end]);
            if (decimal.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                return d;

            return null;
        }

        private static decimal RoundToHalf(decimal x) => Math.Round(x * 2m, MidpointRounding.AwayFromZero) / 2m;
        private static decimal Clamp(decimal v, decimal min, decimal max) => v < min ? min : (v > max ? max : v);

        // ------------------------ METRIC LOOKUP ------------------------
        // Cerca "BaseName - Casa/Ospite" accettando Casa/Ospite o Home/Away
        private static (decimal? home, decimal? away) GetPair(MetricGroup? mg, string baseName)
        {
            if (mg?.Metrics is null || mg.Metrics.Count == 0) return (null, null);

            decimal? H = null, A = null;
            foreach (var kv in mg.Metrics)
            {
                var key = kv.Key?.Trim() ?? "";
                var lastDash = key.LastIndexOf('-');
                if (lastDash <= 0) continue;

                var name = key.Substring(0, lastDash).Trim();
                var side = key[(lastDash + 1)..].Trim();

                if (!name.Equals(baseName, StringComparison.OrdinalIgnoreCase)) continue;

                if (side.Equals("Casa", StringComparison.OrdinalIgnoreCase) || side.Equals("Home", StringComparison.OrdinalIgnoreCase))
                    H = TryParseDec(kv.Value);
                else if (side.Equals("Ospite", StringComparison.OrdinalIgnoreCase) || side.Equals("Away", StringComparison.OrdinalIgnoreCase))
                    A = TryParseDec(kv.Value);
            }
            return (H, A);
        }

        // Ritorna prima metrica utile tra più basi candidate (comodo per "Effettuati"/"Fatti")
        private static (decimal? home, decimal? away) GetFirstAvailablePair(MetricGroup? mg, params string[] baseCandidates)
        {
            foreach (var b in baseCandidates)
            {
                var p = GetPair(mg, b);
                if (p.home.HasValue || p.away.HasValue) return p;
            }
            return (null, null);
        }

        // Blend casa/trasferta con pesi
        private static decimal? Blend((decimal? home, decimal? away) pair, decimal wHome, decimal wAway)
        {
            var (h, a) = pair;
            if (h.HasValue && a.HasValue) return h.Value * wHome + a.Value * wAway;
            if (h.HasValue) return h.Value * wHome;
            if (a.HasValue) return a.Value * wAway;
            return null;
        }
        // ------------------------ WEIGHTS FROM PREDICTION ------------------------
        private sealed record Weights(decimal HomeW, decimal AwayW, decimal AttackBias, decimal DefenseBias, decimal PaceBias);

        private static Weights GetWeightsFromPrediction(PredictionRow? p)
        {
            // default neutro
            decimal wH = 0.5m, wA = 0.5m, att = 0.0m, def = 0.0m, pace = 0.0m;

            if (p != null)
            {
                // Esito: sposta focus tra casa/trasferta
                var esito = (p.Esito ?? "").ToUpperInvariant();
                if (esito.Contains('1') && !esito.Contains('2')) { wH = 0.65m; wA = 0.35m; }
                else if (esito.Contains('2') && !esito.Contains('1')) { wH = 0.35m; wA = 0.65m; }
                else if (esito.Contains('X')) { wH = 0.5m; wA = 0.5m; }

                // Over/Under: attacco vs difesa
                var ou = (p.OverUnderRange ?? "").ToUpperInvariant();
                if (ou.Contains("OVER")) { att += 0.20m; pace += 0.15m; }
                if (ou.Contains("UNDER")) { def += 0.20m; pace -= 0.10m; }

                // GG/NG
                var gg = (p.GG_NG ?? "").ToUpperInvariant();
                if (gg.Contains("GG")) { att += 0.10m; }
                if (gg.Contains("NG")) { def += 0.10m; }

                // Goal simulati totali
                var tot = p.TotaleGoalSimulati;
                if (tot >= 3) { att += 0.15m; pace += 0.10m; }
                else if (tot <= 1) { def += 0.15m; pace -= 0.10m; }
            }

            // clamp & normalize
            wH = Clamp(wH, 0.2m, 0.8m); wA = 1m - wH;
            att = Clamp(att, -0.3m, 0.5m);
            def = Clamp(def, -0.3m, 0.5m);
            pace = Clamp(pace, -0.3m, 0.5m);

            return new Weights(wH, wA, att, def, pace);
        }
        // ------------------------ LINES (TOTALI) ------------------------
        private static string BuildIndicativeLinesWeighted(
            Weights W,
            MetricGroup? goals, MetricGroup? shots, MetricGroup? corners,
            MetricGroup? cards, MetricGroup? fouls, MetricGroup? offsides)
        {
            // GOAL (usa Fatti/Subiti + split casa/trasferta se disponibili)
            // idea: attacco -> più peso su "Fatti", difesa -> più peso su "Subiti"
            var gF_casa = GetFirstAvailablePair(goals, "Fatti", "Goals Fatti", "Scored");
            var gS_casa = GetFirstAvailablePair(goals, "Subiti", "Goals Subiti", "Conceded");

            var gHomeHome = GetPair(goals, "Fatti in Casa");       // casa segna in casa
            var gAwayAway = GetPair(goals, "Fatti in Trasferta");  // ospite segna fuori

            var gHomeSubHome = GetPair(goals, "Subiti in Casa");       // casa subisce in casa
            var gAwaySubAway = GetPair(goals, "Subiti in Trasferta");  // ospite subisce fuori

            // expected goals totali ≈ media ponderata tra (segnati) e (concessi incrociati)
            decimal? expG_H = Blend(gHomeHome, W.HomeW, 0m) ?? Blend(gF_casa, W.HomeW, 0m);
            decimal? expG_A = Blend(gAwayAway, 0m, W.AwayW) ?? Blend(gF_casa, 0m, W.AwayW);

            decimal? expG_H_fromAway = Blend(gAwaySubAway, 0m, W.AwayW) ?? Blend(gS_casa, 0m, W.AwayW);
            decimal? expG_A_fromHome = Blend(gHomeSubHome, W.HomeW, 0m) ?? Blend(gS_casa, W.HomeW, 0m);

            var expGoals = new[] { expG_H, expG_A, expG_H_fromAway, expG_A_fromHome }
                .Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty(2.0m).Average();

            expGoals = Clamp(expGoals + 0.5m * W.AttackBias - 0.4m * W.DefenseBias, 0.5m, 4.5m);
            var lineGoals = RoundToHalf(expGoals);

            // TIRI (total shots): usa "Effettuati"/"In Casa"/"Fuoricasa"
            var shFor = GetFirstAvailablePair(shots, "Effettuati", "Tiri Effettuati", "Total Shots");
            var shHome = GetPair(shots, "In Casa");
            var shAway = GetPair(shots, "Fuoricasa");

            var expShots = new[] {
        Blend(shHome, W.HomeW, 0m),
        Blend(shAway, 0m, W.AwayW),
        Blend(shFor,  W.HomeW, W.AwayW)
    }.Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty(22m).Average();

            expShots = Clamp(expShots + 6m * W.PaceBias, 10m, 40m);
            var lineShots = RoundToHalf(expShots);

            // CORNER: "In Casa" + "Fuoricasa" come baseline, fallback su "Battuti"
            var coHome = GetPair(corners, "In Casa");
            var coAway = GetPair(corners, "Fuoricasa");
            var coFor = GetFirstAvailablePair(corners, "Battuti", "Effettuati");

            var expCorners = new[] {
        Blend(coHome, W.HomeW, 0m),
        Blend(coAway, 0m, W.AwayW),
        Blend(coFor,  W.HomeW, W.AwayW)
    }.Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty(9.0m).Average();

            expCorners = Clamp(expCorners + 2.0m * W.PaceBias, 5.5m, 14.5m);
            var lineCorners = RoundToHalf(expCorners);

            // CARTELLINI: Fatti/Subiti come intensità, meno dipendente da pace
            var caFor = GetFirstAvailablePair(cards, "Fatti", "Cartellini Fatti", "Cards For");
            var caAg = GetFirstAvailablePair(cards, "Subiti", "Cartellini Subiti", "Cards Against");
            var expCards = new[] {
        Blend(caFor, W.HomeW, W.AwayW),
        Blend(caAg,  W.HomeW, W.AwayW)
    }.Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty(4.5m).Average();

            expCards = Clamp(expCards + 0.8m * (W.AttackBias - W.DefenseBias), 2.5m, 7.5m);
            var lineCards = RoundToHalf(expCards);

            // FALLI: simile a cartellini ma con più ampiezza
            var faFor = GetFirstAvailablePair(fouls, "Fatti", "Fouls For");
            var faAg = GetFirstAvailablePair(fouls, "Subiti", "Fouls Against");
            var expFouls = new[] {
        Blend(faFor, W.HomeW, W.AwayW),
        Blend(faAg,  W.HomeW, W.AwayW)
    }.Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty(26m).Average();

            expFouls = Clamp(expFouls + 2.5m * (W.DefenseBias - 0.2m * W.AttackBias), 16m, 38m);
            var lineFouls = RoundToHalf(expFouls);

            // FUORIGIOCO: poco varianza, dipende leggermente da pace
            var ofFor = GetFirstAvailablePair(offsides, "Fatti", "Offsides For");
            var expOff = Blend(ofFor, W.HomeW, W.AwayW) ?? 3.0m;
            expOff = Clamp(expOff + 0.5m * W.PaceBias, 1.5m, 6.0m);
            var lineOff = RoundToHalf(expOff);

            return $"Linee indicative totali → Gol ~ {lineGoals:0.0} | Tiri ~ {lineShots:0.0} | Corner ~ {lineCorners:0.0} | Cartellini ~ {lineCards:0.0} | Falli ~ {lineFouls:0.0} | Fuorigioco ~ {lineOff:0.0}";
        }

        // ------------------------ LINES (SQUADRA) ------------------------
        private static string BuildTeamLinesWeighted(
            Weights W,
            string homeName, string awayName,
            MetricGroup? goals, MetricGroup? shots, MetricGroup? corners)
        {
            // Corner per squadra (usiamo "In Casa" per la home, "Fuoricasa" per l'away; fallback su "Battuti")
            var coHome = GetPair(corners, "In Casa");
            var coAway = GetPair(corners, "Fuoricasa");
            var coFor = GetFirstAvailablePair(corners, "Battuti", "Effettuati");

            var lineHomeCorners = RoundToHalf((Blend(coHome, 1m, 0m) ?? Blend(coFor, 1m, 0m) ?? 4.5m) + 1.0m * W.PaceBias);
            var lineAwayCorners = RoundToHalf((Blend(coAway, 0m, 1m) ?? Blend(coFor, 0m, 1m) ?? 4.0m) + 0.8m * W.PaceBias);

            // Tiri per squadra (In Casa/Fuoricasa; fallback su Effettuati)
            var shHome = GetPair(shots, "In Casa");
            var shAway = GetPair(shots, "Fuoricasa");
            var shFor = GetFirstAvailablePair(shots, "Effettuati", "Total Shots");

            var lineHomeShots = RoundToHalf((Blend(shHome, 1m, 0m) ?? Blend(shFor, 1m, 0m) ?? 11m) + 2.0m * W.PaceBias);
            var lineAwayShots = RoundToHalf((Blend(shAway, 0m, 1m) ?? Blend(shFor, 0m, 1m) ?? 10m) + 1.5m * W.PaceBias);

            // Goal per squadra (come sopra, utile se vuoi “Team Over 0.5” ecc.)
            var gHomeHome = GetPair(goals, "Fatti in Casa");
            var gAwayAway = GetPair(goals, "Fatti in Trasferta");
            var lineHomeGoals = RoundToHalf((Blend(gHomeHome, 1m, 0m) ?? 1.0m) + 0.3m * W.AttackBias - 0.3m * W.DefenseBias);
            var lineAwayGoals = RoundToHalf((Blend(gAwayAway, 0m, 1m) ?? 0.9m) + 0.2m * W.AttackBias - 0.2m * W.DefenseBias);

            return $"Linee squadra → {homeName}: Corner ~ {lineHomeCorners:0.0}, Tiri ~ {lineHomeShots:0.0}, Gol ~ {lineHomeGoals:0.0} | {awayName}: Corner ~ {lineAwayCorners:0.0}, Tiri ~ {lineAwayShots:0.0}, Gol ~ {lineAwayGoals:0.0}";
        }

        /// <summary>
        /// Converte le Metriche in 3–5 "segnali" sintetici per ogni blocco
        /// restituendo due stringhe:
        /// - aiBlock: testo semplice per il prompt (no emoji)
        /// - tgBlock: versione leggibile per Telegram (con bullet ed eventuali emoji)
        /// Funziona con chiavi tipo: "Corner", "Cards/Gialli/Rossi", "Fouls", "Offsides", "Shots".
        /// Tollerante ai nomi (Home/Away oppure Casa/Ospite).
        /// </summary>
        // dentro DetailsModel (non fuori!), sostituisci BuildSignalsBlocks con questa versione
        private static (string aiBlock, string tgBlock) BuildSignalsBlocks(
            MetricGroup? corners,
            MetricGroup? cards,
            MetricGroup? fouls,
            MetricGroup? offsides,
            MetricGroup? shots)
        {
            // Helpers --------------------------------------------------------
            static bool ContainsI(string s, string needle) =>
                s?.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

            static bool AnyContains(string s, params string[] needles) =>
                needles.Any(n => ContainsI(s, n));

            static string FirstOr(string? s, string alt = "—") =>
                string.IsNullOrWhiteSpace(s) ? alt : s;

            static IEnumerable<KeyValuePair<string, string>> FilterKeys(Dictionary<string, string> m, string[] topics) =>
                m.Where(kv => topics.Any(t => ContainsI(kv.Key, t)));

            static string TryGetSidePair(Dictionary<string, string> m, string[] topics, string[] homeSyn, string[] awaySyn)
            {
                // es. "Corner - Casa" / "Corner - Ospite" oppure "Shots Home" / "Shots Away"
                string? homeVal = m.FirstOrDefault(kv =>
                    topics.Any(t => ContainsI(kv.Key, t)) &&
                    homeSyn.Any(h => ContainsI(kv.Key, h))
                ).Value;

                string? awayVal = m.FirstOrDefault(kv =>
                    topics.Any(t => ContainsI(kv.Key, t)) &&
                    awaySyn.Any(a => ContainsI(kv.Key, a))
                ).Value;

                if (!string.IsNullOrWhiteSpace(homeVal) || !string.IsNullOrWhiteSpace(awayVal))
                    return $"{FirstOr(homeVal)} / {FirstOr(awayVal)}";

                return "—";
            }

            static string TryGetForAgainstPair(Dictionary<string, string> m, string[] topics, string[] forSyn, string[] againstSyn)
            {
                // es. "Corner Effettuati" / "Corner Subiti" oppure "Fouls For" / "Fouls Against"
                string? forVal = m.FirstOrDefault(kv =>
                    topics.Any(t => ContainsI(kv.Key, t)) &&
                    forSyn.Any(f => ContainsI(kv.Key, f))
                ).Value;

                string? againstVal = m.FirstOrDefault(kv =>
                    topics.Any(t => ContainsI(kv.Key, t)) &&
                    againstSyn.Any(a => ContainsI(kv.Key, a))
                ).Value;

                if (!string.IsNullOrWhiteSpace(forVal) || !string.IsNullOrWhiteSpace(againstVal))
                    return $"{FirstOr(forVal)} / {FirstOr(againstVal)}";

                return "—";
            }

            static IEnumerable<string> PickOverLines(Dictionary<string, string> m, string[] topics, int max = 2)
            {
                // cattura righe "Over ..." pertinenti all'argomento
                return m
                    .Where(kv =>
                        ContainsI(kv.Key, "over") &&
                        topics.Any(t => ContainsI(kv.Key, t)) &&
                        !string.IsNullOrWhiteSpace(kv.Value) && kv.Value != "—")
                    .OrderBy(kv => kv.Key)
                    .Take(max)
                    .Select(kv => $"• {kv.Key}: {kv.Value}");
            }

            static (string ai, string tg) BuildForTopic(
                string title,
                MetricGroup? mg,
                string[] topicSyn,
                string[] homeSyn,
                string[] awaySyn,
                string[] forSyn,
                string[] againstSyn,
                int maxOvers = 2)
            {
                if (mg is null || mg.Metrics.Count == 0) return ("", "");

                var m = mg.Metrics;

                // 1) Prova coppia Casa/Ospite
                var sidePair = TryGetSidePair(m, topicSyn, homeSyn, awaySyn);
                // 2) Se non c'è, prova Effettuati/Fatti vs Subiti/Concessi/Against
                if (sidePair == "—")
                    sidePair = TryGetForAgainstPair(m, topicSyn, forSyn, againstSyn);

                // 3) Over lines (2 max)
                var overs = PickOverLines(m, topicSyn, maxOvers).ToList();

                // Se non abbiamo nulla, prova a salvare almeno 2-3 metriche raw pertinenti
                if (sidePair == "—" && overs.Count == 0)
                {
                    var raw = FilterKeys(m, topicSyn)
                        .Where(kv => !string.IsNullOrWhiteSpace(kv.Value) && kv.Value != "—")
                        .OrderBy(kv => kv.Key)
                        .Take(3)
                        .Select(kv => $"• {kv.Key}: {kv.Value}")
                        .ToList();

                    if (raw.Count == 0) return ("", "");

                    var aiRaw = string.Join("\n", raw.Select(s => "- " + s.TrimStart('•', ' ')));
                    var tgRaw = "• <b>" + title + "</b>\n  " + string.Join("\n  ", raw);
                    return ("\n" + aiRaw, "\n" + tgRaw);
                }

                // Costruzione blocchi AI / TG
                var sbAi = new System.Text.StringBuilder();
                var sbTg = new System.Text.StringBuilder();

                sbTg.AppendLine($"• <b>{title}</b>");

                if (sidePair != "—")
                {
                    sbAi.AppendLine($"- {title} (Casa/Ospite o For/Against): {sidePair}");
                    sbTg.AppendLine($"  • Distribuzione: {sidePair}");
                }

                foreach (var ov in overs)
                {
                    sbAi.AppendLine("- " + ov.TrimStart('•', ' '));
                    sbTg.AppendLine("  " + ov);
                }

                var aiS = sbAi.ToString().TrimEnd();
                var tgS = sbTg.ToString().TrimEnd();

                return (string.IsNullOrWhiteSpace(aiS) && string.IsNullOrWhiteSpace(tgS)) ? ("", "") : ("\n" + aiS, "\n" + tgS);
            }

            // Sinonimi/varianti ------------------------------------------------
            string[] homeSyn = { "Casa", "Home" };
            string[] awaySyn = { "Ospite", "Away" };
            string[] forSyn = { "Effettuati", "Fatti", "For", "Prodotti", "Per 90 For", "Per Game For" };
            string[] againstSyn = { "Subiti", "Concessi", "Against", "Per 90 Against", "Per Game Against" };

            // topic (ampi, IT+EN)
            string[] tCorners = { "Corner", "Angoli" };
            string[] tCards = { "Cartellini", "Cards", "Gialli", "Rossi" };
            string[] tFouls = { "Falli", "Fouls" };
            string[] tOffsides = { "Fuorigioco", "Offsides", "Offside" };
            string[] tShots = { "Tiri", "Shots", "Tiri Totali", "Total Shots" };

            // Costruisci i 5 blocchi ------------------------------------------
            var ai = new System.Text.StringBuilder();
            var tg = new System.Text.StringBuilder();

            var (aiC, tgC) = BuildForTopic("Corner", corners, tCorners, homeSyn, awaySyn, forSyn, againstSyn);
            var (aiCa, tgCa) = BuildForTopic("Cartellini", cards, tCards, homeSyn, awaySyn, forSyn, againstSyn);
            var (aiF, tgF) = BuildForTopic("Falli", fouls, tFouls, homeSyn, awaySyn, forSyn, againstSyn);
            var (aiO, tgO) = BuildForTopic("Fuorigioco", offsides, tOffsides, homeSyn, awaySyn, forSyn, againstSyn);
            var (aiS, tgS) = BuildForTopic("Tiri", shots, tShots, homeSyn, awaySyn, forSyn, againstSyn);

            ai.Append(aiC).Append(aiCa).Append(aiF).Append(aiO).Append(aiS);
            tg.Append(tgC).Append(tgCa).Append(tgF).Append(tgO).Append(tgS);

            return (ai.ToString().TrimEnd(), tg.ToString().TrimEnd());
        }
        // ==========================================
        // 🔹 CORNER: suggerimenti interni (totali + team)
        // ==========================================
        /// <summary>
        /// Costruisce 2–3 suggerimenti sui corner usando:
        /// - Esito proprietario (per capire favorita/sfavorita),
        /// - Analisi corner (medie nelle VITTORIE/PERSE, casa/trasferta),
        /// - Quote reali da tabella Odds.
        ///
        /// Restituisce una stringa tipo:
        /// CORNERS_SUGGERITI_INTERNO:
        /// - Totali: Over 9.5 corner totali a quota 1.80
        /// - Bodo/Glimt: Over 5.5 corner a quota 1.73
        /// - KFUM Oslo: Over 3.5 corner a quota 1.91 (borderline)
        ///
        /// Questa stringa va passata al prompt con l’obbligo:
        /// "NON cambiare linea/direzione/quote, riscrivi solo in modo discorsivo".
        /// </summary>
        // ==========================================
        // 🔹 CORNER: suggerimenti interni (totali + team)
        // ==========================================
        // ==========================================
        // 🔹 CORNER: suggerimenti interni (totali + team) da linee + odds
        // ==========================================
        private static string BuildCornerSuggestions(
            IndicativeLines baseLines,
            TeamIndicativeLines teamLines,
            List<OddsRow> odds,
            string esitoProprietario,
            string homeName,
            string awayName)
        {
            if (odds is null || odds.Count == 0)
                return "CORNERS_SUGGERITI_INTERNO: (nessun mercato corner disponibile)";

            decimal expectedTotalCorners = baseLines.Corners;
            decimal expectedHomeCorners = teamLines.HomeCorners;
            decimal expectedAwayCorners = teamLines.AwayCorners;

            // 1) chi è favorita secondo il modello proprietario?
            esitoProprietario = (esitoProprietario ?? "").Trim().ToUpperInvariant();
            bool favHome = esitoProprietario is "1" or "1X";
            bool favAway = esitoProprietario is "2" or "X2";

            // 2) helper parsing linea da stringa "Over 9.5"
            static bool TryParseLine(string? value, out decimal line)
            {
                line = 0m;
                if (string.IsNullOrWhiteSpace(value)) return false;

                var parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var last = parts[^1].Replace(',', '.');

                return decimal.TryParse(
                    last,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out line
                );
            }

            // 3) scegli Over/Under in base a quanto l'expected è distante dalla linea
            static (string direction, decimal line, decimal odd, bool borderline)? ChooseSide(
                decimal expected,
                IEnumerable<OddsRow> candidates,
                decimal minDeltaStrong = 0.75m,
                decimal minDeltaBorder = 0.30m)
            {
                (string direction, decimal line, decimal odd, bool borderline)? best = null;
                decimal bestScore = 0m;

                foreach (var o in candidates)
                {
                    if (!TryParseLine(o.Value, out var line)) continue;

                    bool isOver = o.Value!.Trim().StartsWith("Over", StringComparison.OrdinalIgnoreCase);
                    decimal delta = isOver ? expected - line : line - expected;

                    if (delta < minDeltaBorder) continue;

                    bool borderline = delta < minDeltaStrong;

                    if (delta > bestScore)
                    {
                        bestScore = delta;
                        best = (isOver ? "Over" : "Under", line, o.Odd, borderline);
                    }
                }

                return best;
            }

            // 4) separa mercati totali / casa / trasferta
            var totalOdds = odds
                .Where(o =>
                    (o.Description ?? "").IndexOf("Corners", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (o.Description ?? "").IndexOf("Over", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (o.Description ?? "").IndexOf("Under", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (o.Description ?? "").IndexOf("Home", StringComparison.OrdinalIgnoreCase) < 0 &&
                    (o.Description ?? "").IndexOf("Away", StringComparison.OrdinalIgnoreCase) < 0
                )
                .ToList();

            var homeOdds = odds
                .Where(o =>
                    (o.Description ?? "").IndexOf("Corners", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (
                        (o.Description ?? "").IndexOf("Home", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (o.Value ?? "").IndexOf(homeName, StringComparison.OrdinalIgnoreCase) >= 0
                    )
                )
                .ToList();

            var awayOdds = odds
                .Where(o =>
                    (o.Description ?? "").IndexOf("Corners", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (
                        (o.Description ?? "").IndexOf("Away", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (o.Value ?? "").IndexOf(awayName, StringComparison.OrdinalIgnoreCase) >= 0
                    )
                )
                .ToList();

            // 5) pick totali
            var totalPick = totalOdds.Count > 0
                ? ChooseSide(expectedTotalCorners, totalOdds)
                : null;

            // 6) pick squadra (uso comunque le expected singole)
            var homePick = (homeOdds.Count > 0 && expectedHomeCorners > 0m)
                ? ChooseSide(expectedHomeCorners, homeOdds, 0.50m, 0.20m)
                : null;

            var awayPick = (awayOdds.Count > 0 && expectedAwayCorners > 0m)
                ? ChooseSide(expectedAwayCorners, awayOdds, 0.50m, 0.20m)
                : null;

            if (totalPick is null && homePick is null && awayPick is null)
                return "CORNERS_SUGGERITI_INTERNO: (nessun edge chiaro rispetto alle linee di mercato)";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("CORNERS_SUGGERITI_INTERNO:");

            if (totalPick is not null)
            {
                var (dir, line, odd, borderline) = totalPick.Value;
                var note = borderline ? " (edge più sottile)" : "";
                sb.AppendLine($"- Totali: {dir} {line:0.0} corner totali a quota {odd:0.00}{note}");
            }

            if (homePick is not null)
            {
                var (dir, line, odd, borderline) = homePick.Value;
                var note = borderline ? " (borderline)" : "";
                sb.AppendLine($"- {homeName}: {dir} {line:0.0} corner a quota {odd:0.00}{note}");
            }

            if (awayPick is not null)
            {
                var (dir, line, odd, borderline) = awayPick.Value;
                var note = borderline ? " (borderline)" : "";
                sb.AppendLine($"- {awayName}: {dir} {line:0.0} corner a quota {odd:0.00}{note}");
            }

            return sb.ToString().TrimEnd();
        }

        // ==========================================
        // 🔹 TIRI: suggerimenti (totali + team) da linee indicative + odds
        // ==========================================
        private static string BuildShotsSuggestions(
            IndicativeLines baseLines,
            TeamIndicativeLines teamLines,
            List<OddsRow> odds,
            string homeName,
            string awayName)
        {
            if (odds is null || odds.Count == 0)
                return "SHOTS_SUGGERITI_INTERNO: (nessun mercato tiri disponibile)";

            decimal expectedTotalShots = baseLines.Shots;
            decimal expectedHomeShots = teamLines.HomeShots;
            decimal expectedAwayShots = teamLines.AwayShots;

            static bool TryParseLine(string? value, out decimal line)
            {
                line = 0m;
                if (string.IsNullOrWhiteSpace(value)) return false;
                var parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var last = parts[^1].Replace(',', '.');
                return decimal.TryParse(last, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out line);
            }

            static (string direction, decimal line, decimal odd)? ChooseSideShots(
                decimal expected,
                IEnumerable<OddsRow> candidates,
                decimal minDelta = 1.0m)
            {
                (string direction, decimal line, decimal odd)? best = null;
                decimal bestScore = 0m;

                foreach (var o in candidates)
                {
                    if (!TryParseLine(o.Value, out var line)) continue;

                    bool isOver = o.Value!.Trim().StartsWith("Over", StringComparison.OrdinalIgnoreCase);
                    decimal delta = isOver ? expected - line : line - expected;

                    if (delta < minDelta) continue;

                    if (delta > bestScore)
                    {
                        bestScore = delta;
                        best = (isOver ? "Over" : "Under", line, o.Odd);
                    }
                }

                return best;
            }

            // cerco totali / casa / ospite in Description
            var totalOdds = odds
                .Where(o => (o.Description ?? "").IndexOf("Shots", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            (o.Description ?? "").IndexOf("Home", StringComparison.OrdinalIgnoreCase) < 0 &&
                            (o.Description ?? "").IndexOf("Away", StringComparison.OrdinalIgnoreCase) < 0)
                .ToList();

            var homeOdds = odds
                .Where(o => (o.Description ?? "").IndexOf("Shots", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            (o.Description ?? "").IndexOf("Home", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            var awayOdds = odds
                .Where(o => (o.Description ?? "").IndexOf("Shots", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            (o.Description ?? "").IndexOf("Away", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            var totalPick = (totalOdds.Count > 0)
                ? ChooseSideShots(expectedTotalShots, totalOdds)
                : null;

            var homePick = (homeOdds.Count > 0 && expectedHomeShots > 0m)
                ? ChooseSideShots(expectedHomeShots, homeOdds, 0.7m)
                : null;

            var awayPick = (awayOdds.Count > 0 && expectedAwayShots > 0m)
                ? ChooseSideShots(expectedAwayShots, awayOdds, 0.7m)
                : null;

            if (totalPick is null && homePick is null && awayPick is null)
                return "SHOTS_SUGGERITI_INTERNO: (nessun edge chiaro sui tiri)";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("SHOTS_SUGGERITI_INTERNO:");

            if (totalPick is not null)
            {
                var (dir, line, odd) = totalPick.Value;
                sb.AppendLine($"- Totali: {dir} {line:0.0} tiri totali a quota {odd:0.00}");
            }

            if (homePick is not null)
            {
                var (dir, line, odd) = homePick.Value;
                sb.AppendLine($"- {homeName}: {dir} {line:0.0} tiri a quota {odd:0.00}");
            }

            if (awayPick is not null)
            {
                var (dir, line, odd) = awayPick.Value;
                sb.AppendLine($"- {awayName}: {dir} {line:0.0} tiri a quota {odd:0.00}");
            }

            return sb.ToString().TrimEnd();
        }
        // ==========================================
        // 🔹 CARTELLINI: solo totali (per ora) da linee + odds
        // ==========================================
        private static string BuildCardsSuggestions(
            IndicativeLines baseLines,
            List<OddsRow> odds)
        {
            if (odds is null || odds.Count == 0)
                return "CARDS_SUGGERITI_INTERNO: NON PROPORRE MERCATI SPECIFICI SUI CARTELLINI (nessuna quota disponibile)";


            decimal expectedCards = baseLines.Cards;

            static bool TryParseLine(string? value, out decimal line)
            {
                line = 0m;
                if (string.IsNullOrWhiteSpace(value)) return false;
                var parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var last = parts[^1].Replace(',', '.');
                return decimal.TryParse(last, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out line);
            }

            static (string direction, decimal line, decimal odd)? ChooseSideCards(
                decimal expected,
                IEnumerable<OddsRow> candidates,
                decimal minDelta = 0.5m)
            {
                (string direction, decimal line, decimal odd)? best = null;
                decimal bestScore = 0m;

                foreach (var o in candidates)
                {
                    if (!TryParseLine(o.Value, out var line)) continue;

                    bool isOver = o.Value!.Trim().StartsWith("Over", StringComparison.OrdinalIgnoreCase);
                    decimal delta = isOver ? expected - line : line - expected;

                    if (delta < minDelta) continue;

                    if (delta > bestScore)
                    {
                        bestScore = delta;
                        best = (isOver ? "Over" : "Under", line, o.Odd);
                    }
                }

                return best;
            }

            var cardsOdds = odds
                .Where(o => (o.Description ?? "").IndexOf("Cards", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            if (cardsOdds.Count == 0)
                return "CARDS_SUGGERITI_INTERNO: NON PROPORRE MERCATI SPECIFICI SUI CARTELLINI (nessuna linea cartellini nelle quote)";


            var pick = ChooseSideCards(expectedCards, cardsOdds);

            if (pick is null || expectedCards < 2.0m)
                return "CARDS_SUGGERITI_INTERNO: NON PROPORRE MERCATI SPECIFICI SUI CARTELLINI (volume atteso basso / nessun edge)";


            var (dirFinal, lineFinal, oddFinal) = pick.Value;

            return
                $"CARDS_SUGGERITI_INTERNO:\n" +
                $"- Totali: {dirFinal} {lineFinal:0.0} cartellini totali a quota {oddFinal:0.00}";
        }


        // =======================
        // EMOJI HELPER
        // =======================
        public static class EmojiHelper
        {
            public static string FromCountryCode(string? code)
            {
                if (string.IsNullOrWhiteSpace(code))
                    return "🌍";

                var cc = code.Length > 2 && code.Contains('-')
                    ? code.Split('-')[0]
                    : code;
                cc = cc.Trim().ToUpperInvariant();

                if (cc.Length != 2) return "🌍";
                int offset = 0x1F1E6;
                return string.Concat(
                    char.ConvertFromUtf32(offset + (cc[0] - 'A')),
                    char.ConvertFromUtf32(offset + (cc[1] - 'A'))
                );
            }
        }

        // ===== NUMERIC LINE HELPERS =====
        private static decimal? ParseDec(string? s)
        {
            // Usa la versione robusta che abbiamo definito sopra (TryParseDec),
            // che pulisce % , testo extra, parentesi, ecc.
            var d = TryParseDec(s);
            if (d.HasValue) return d;

            // Fallback di sicurezza (quasi mai usato, ma non fa male)
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().Replace("%", "");

            if (decimal.TryParse(s, System.Globalization.NumberStyles.Any,
                new System.Globalization.CultureInfo("it-IT"), out var it))
                return it;

            if (decimal.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var inv))
                return inv;

            return null;
        }


        // Trova coppie "Fatti/Effettuati" e "Subiti/Concessi" per HOME e AWAY in un JsonObject di metriche.
        private static (decimal? fattiHome, decimal? subitiHome, decimal? fattiAway, decimal? subitiAway) ExtractForAgainst(JsonObject? obj)
        {
            if (obj is null) return (null, null, null, null);

            // funzioni per cercare chiavi con suffix "-Casa"/"-Ospite" o "Home"/"Away" e con base-name variabile
            decimal? find(string[] forKeys, string side) // side: "Casa"/"Home" oppure "Ospite"/"Away"
            {
                foreach (var kv in obj)
                {
                    var key = kv.Key?.Trim() ?? "";
                    var val = kv.Value?.ToString();
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(val)) continue;

                    // split sull’ultimo '-'
                    var lastDash = key.LastIndexOf('-');
                    var baseName = lastDash > 0 ? key.Substring(0, lastDash).Trim() : key;
                    var sideName = lastDash > 0 ? key[(lastDash + 1)..].Trim() : "";

                    bool sideOk =
                        side.Equals("Casa", StringComparison.OrdinalIgnoreCase) ?
                            (sideName.Equals("Casa", StringComparison.OrdinalIgnoreCase) || sideName.Equals("Home", StringComparison.OrdinalIgnoreCase))
                          : (sideName.Equals("Ospite", StringComparison.OrdinalIgnoreCase) || sideName.Equals("Away", StringComparison.OrdinalIgnoreCase));

                    if (!sideOk) continue;

                    // “for” synonyms
                    bool isFor = forKeys.Any(fk => baseName.IndexOf(fk, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (isFor)
                    {
                        var d = ParseDec(val);
                        if (d.HasValue) return d;
                    }
                }
                return null;
            }

            decimal? fattiCasa = find(new[] { "Fatti", "Effettuati", "For", "Prodotti" }, "Casa");
            decimal? fattiOspite = find(new[] { "Fatti", "Effettuati", "For", "Prodotti" }, "Ospite");
            decimal? subCasa = find(new[] { "Subiti", "Concessi", "Against" }, "Casa");
            decimal? subOspite = find(new[] { "Subiti", "Concessi", "Against" }, "Ospite");

            return (fattiCasa, subCasa, fattiOspite, subOspite);
        }

        // Estrae eventualmente anche le versioni "Ultime 5", "In Casa", "Fuoricasa" per un leggero ribilanciamento.
        private static (decimal? fattiUlt5Home, decimal? subUlt5Home, decimal? fattiUlt5Away, decimal? subUlt5Away,
                       decimal? fattiCasaHome, decimal? subCasaHome, decimal? fattiCasaAway, decimal? subCasaAway,
                       decimal? fattiFuoriHome, decimal? subFuoriHome, decimal? fattiFuoriAway, decimal? subFuoriAway)
            ExtractContextual(JsonObject? obj)
        {
            if (obj is null) return (null, null, null, null, null, null, null, null, null, null, null, null);

            decimal? get(string containsBase, string side) // "Ultime 5", "In Casa", "Fuoricasa" + Fatti/Subiti
            {
                foreach (var kv in obj)
                {
                    var key = kv.Key?.Trim() ?? "";
                    var val = kv.Value?.ToString();
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(val)) continue;

                    // separa base-side
                    var lastDash = key.LastIndexOf('-');
                    var baseName = lastDash > 0 ? key.Substring(0, lastDash).Trim() : key;
                    var sideName = lastDash > 0 ? key[(lastDash + 1)..].Trim() : "";

                    bool sideOk =
                        side.Equals("Casa", StringComparison.OrdinalIgnoreCase) ?
                            (sideName.Equals("Casa", StringComparison.OrdinalIgnoreCase) || sideName.Equals("Home", StringComparison.OrdinalIgnoreCase))
                          : (sideName.Equals("Ospite", StringComparison.OrdinalIgnoreCase) || sideName.Equals("Away", StringComparison.OrdinalIgnoreCase));

                    if (!sideOk) continue;

                    // es: "Fatti Ultime 5", "Subiti In Casa", "Fatti Fuoricasa"
                    if (baseName.IndexOf("Fatti", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        baseName.IndexOf(containsBase, StringComparison.OrdinalIgnoreCase) >= 0)
                        return ParseDec(val);

                    if (baseName.IndexOf("Subiti", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        baseName.IndexOf(containsBase, StringComparison.OrdinalIgnoreCase) >= 0)
                        return ParseDec(val);
                }
                return null;
            }

            // Ultime 5 (fatti e subiti)
            var f5h = get("Ultime 5", "Casa"); var s5h = get("Ultime 5", "Casa");   // tornerà il primo matchante (Fatti/Subiti)
            var f5a = get("Ultime 5", "Ospite"); var s5a = get("Ultime 5", "Ospite");

            // In Casa / Fuoricasa (fatti/subiti)
            var fic_h = get("In Casa", "Casa"); var sic_h = get("In Casa", "Casa");
            var fic_a = get("In Casa", "Ospite"); var sic_a = get("In Casa", "Ospite");

            var ffc_h = get("Fuoricasa", "Casa"); var sfc_h = get("Fuoricasa", "Casa");
            var ffc_a = get("Fuoricasa", "Ospite"); var sfc_a = get("Fuoricasa", "Ospite");

            return (f5h, s5h, f5a, s5a, fic_h, sic_h, fic_a, sic_a, ffc_h, sfc_h, ffc_a, sfc_a);
        }

        private static decimal Blend(params decimal?[] parts)
        {
            var vals = parts.Where(v => v.HasValue && v.Value > 0m).Select(v => v!.Value).ToList();
            if (vals.Count == 0) return 0m;
            return vals.Average();
        }

        private static decimal Quantize(decimal v, decimal step, decimal min, decimal max)
        {
            if (v <= 0m) return min;
            var q = Math.Round(v / step) * step;
            if (q < min) q = min;
            if (q > max) q = max;
            return q;
        }
        // ===== TEAM LINES =====
        private record TeamIndicativeLines(
            decimal HomeGoals, decimal AwayGoals,
            decimal HomeCorners, decimal AwayCorners,
            decimal HomeCards, decimal AwayCards,
            decimal HomeShots, decimal AwayShots
        );

        private static TeamIndicativeLines BuildTeamLines(JsonObject? goals, JsonObject? corners, JsonObject? cards, JsonObject? shots, PredictionRow? p)
        {
            // Goals expected (già ricavati nella BuildIndicativeLines: homeExpGoals/awayExpGoals)
            // Ricalcolo qui per non dipendere da variabili locali
            var (gFh, gSh, gFa, gSa) = ExtractForAgainst(goals);
            var homeExpGoals = Blend(gFh, gSa);
            var awayExpGoals = Blend(gFa, gSh);

            if (p is not null && p.GoalSimulatoCasa > 0 && p.GoalSimulatoOspite > 0)
            {
                // blend 60/40 con i simulati interni se presenti
                homeExpGoals = (homeExpGoals > 0 ? 0.4m * homeExpGoals : 0m) + 0.6m * p.GoalSimulatoCasa;
                awayExpGoals = (awayExpGoals > 0 ? 0.4m * awayExpGoals : 0m) + 0.6m * p.GoalSimulatoOspite;
            }

            var homeGoalsLine = Quantize(homeExpGoals, 0.5m, 0.5m, 2.5m);
            var awayGoalsLine = Quantize(awayExpGoals, 0.5m, 0.5m, 2.5m);

            // Corners: team ≈ media tra "Corner fatti" della squadra e "Corner concessi" dell'avversaria
            var (cFh, cSh, cFa, cSa) = ExtractForAgainst(corners);
            var homeCornersExp = Blend(cFh, cSa);
            var awayCornersExp = Blend(cFa, cSh);
            var homeCornersLine = Quantize(homeCornersExp, 0.5m, 3.5m, 6.5m);
            var awayCornersLine = Quantize(awayCornersExp, 0.5m, 3.5m, 6.5m);

            // Cartellini
            var (caFh, caSh, caFa, caSa) = ExtractForAgainst(cards);
            var homeCardsExp = Blend(caFh, caSa);
            var awayCardsExp = Blend(caFa, caSh);
            var homeCardsLine = Quantize(homeCardsExp, 0.5m, 1.5m, 3.5m);
            var awayCardsLine = Quantize(awayCardsExp, 0.5m, 1.5m, 3.5m);

            // Tiri
            var (sFh, sSh, sFa, sSa) = ExtractForAgainst(shots);
            var homeShotsExp = Blend(sFh, sSa);
            var awayShotsExp = Blend(sFa, sSh);
            var homeShotsLine = Quantize(homeShotsExp, 0.5m, 7.5m, 16.5m);
            var awayShotsLine = Quantize(awayShotsExp, 0.5m, 7.5m, 16.5m);

            return new TeamIndicativeLines(
                homeGoalsLine, awayGoalsLine,
                homeCornersLine, awayCornersLine,
                homeCardsLine, awayCardsLine,
                homeShotsLine, awayShotsLine
            );
        }

        // Decide automaticamente Over/Under sul mercato singola squadra 
        // in base alla distanza dall’aspettativa (soglia 0.10 = cuscinetto antibandiera)
        private static string DecideDirection(decimal expected, decimal line)
        {
            if (expected >= line + 0.10m) return "Over";
            if (expected <= line - 0.10m) return "Under";
            // se a ridosso della linea lascia comunque Over (più “market friendly”)
            return expected >= line ? "Over" : "Under";
        }

        // Crea 2–4 candidati “singola squadra” già con direzione consigliata
        // (stringa da passare al prompt, NON da stampare in pagina)
        private static string BuildTeamCandidates(JsonObject? goals, JsonObject? corners, JsonObject? cards, JsonObject? shots,
                                                 PredictionRow? p, string home, string away)
        {
            // riuso same expectations
            var (gFh, gSh, gFa, gSa) = ExtractForAgainst(goals);
            var homeExpGoals = Blend(gFh, gSa);
            var awayExpGoals = Blend(gFa, gSh);

            if (p is not null && p.GoalSimulatoCasa > 0 && p.GoalSimulatoOspite > 0)
            {
                homeExpGoals = (homeExpGoals > 0 ? 0.4m * homeExpGoals : 0m) + 0.6m * p.GoalSimulatoCasa;
                awayExpGoals = (awayExpGoals > 0 ? 0.4m * awayExpGoals : 0m) + 0.6m * p.GoalSimulatoOspite;
            }

            var (cFh, cSh, cFa, cSa) = ExtractForAgainst(corners);
            var (caFh, caSh, caFa, caSa) = ExtractForAgainst(cards);
            var (sFh, sSh, sFa, sSa) = ExtractForAgainst(shots);

            var homeCornersExp = Blend(cFh, cSa);
            var awayCornersExp = Blend(cFa, cSh);
            var homeCardsExp = Blend(caFh, caSa);
            var awayCardsExp = Blend(caFa, caSh);
            var homeShotsExp = Blend(sFh, sSa);
            var awayShotsExp = Blend(sFa, sSh);

            var tl = BuildTeamLines(goals, corners, cards, shots, p);

            var cand = new List<string>();

            // Goals singola
            if (homeExpGoals > 0) cand.Add($"{home} Gol: {DecideDirection(homeExpGoals, tl.HomeGoals)} {tl.HomeGoals:0.0} (exp≈{homeExpGoals:0.00})");
            if (awayExpGoals > 0) cand.Add($"{away} Gol: {DecideDirection(awayExpGoals, tl.AwayGoals)} {tl.AwayGoals:0.0} (exp≈{awayExpGoals:0.00})");

            // Corners singola
            if (homeCornersExp > 0) cand.Add($"{home} Corner: {DecideDirection(homeCornersExp, tl.HomeCorners)} {tl.HomeCorners:0.0} (exp≈{homeCornersExp:0.00})");
            if (awayCornersExp > 0) cand.Add($"{away} Corner: {DecideDirection(awayCornersExp, tl.AwayCorners)} {tl.AwayCorners:0.0} (exp≈{awayCornersExp:0.00})");

            // Cartellini singola
            if (homeCardsExp > 0) cand.Add($"{home} Cartellini: {DecideDirection(homeCardsExp, tl.HomeCards)} {tl.HomeCards:0.0} (exp≈{homeCardsExp:0.00})");
            if (awayCardsExp > 0) cand.Add($"{away} Cartellini: {DecideDirection(awayCardsExp, tl.AwayCards)} {tl.AwayCards:0.0} (exp≈{awayCardsExp:0.00})");

            // Tiri singola
            if (homeShotsExp > 0) cand.Add($"{home} Tiri: {DecideDirection(homeShotsExp, tl.HomeShots)} {tl.HomeShots:0.0} (exp≈{homeShotsExp:0.00})");
            if (awayShotsExp > 0) cand.Add($"{away} Tiri: {DecideDirection(awayShotsExp, tl.AwayShots)} {tl.AwayShots:0.0} (exp≈{awayShotsExp:0.00})");

            // ordina per “confidenza” (|exp-line| desc) e tieni i migliori 3–4
            var ranked = cand
                .Select(s =>
                {
                    // parse veloce della parte (line, exp) dalla stringa tipo: "... {line} (exp≈{exp})"
                    var li = s.LastIndexOf('(');
                    if (li <= 0) return (score: 0m, text: s);
                    var main = s[..li];
                    var tail = s[li..];
                    // estrai numeri
                    decimal line = 0, exp = 0;
                    var m1 = System.Text.RegularExpressions.Regex.Match(main, @"\s(\d+(\.\d)?)\s*$");
                    var m2 = System.Text.RegularExpressions.Regex.Match(tail, @"exp≈(\d+(\.\d+)?)");
                    if (m1.Success) decimal.TryParse(m1.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out line);
                    if (m2.Success) decimal.TryParse(m2.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out exp);
                    return (score: Math.Abs(exp - line), text: s);
                })
                .OrderByDescending(x => x.score)
                .Take(4)
                .Select(x => x.text);

            return string.Join("\n- ", ranked);
        }

        private record IndicativeLines(
            decimal Goals,     // linea totale gol
            decimal Corners,   // linea corner totali
            decimal Cards,     // linea cartellini totali
            decimal Shots      // linea tiri totali
        );

        // Calcola linee totali attese per Gol / Corner / Cartellini / Tiri
        private static IndicativeLines BuildIndicativeLines(JsonObject? goals, JsonObject? corners, JsonObject? cards, JsonObject? shots, PredictionRow? p)
        {
            // 1) Gol
            var (gFh, gSh, gFa, gSa) = ExtractForAgainst(goals);
            // base: media incrociata fatti vs subiti
            var homeExpGoals = Blend(gFh, gSa);
            var awayExpGoals = Blend(gFa, gSh);
            var totalGoals = homeExpGoals + awayExpGoals;

            // se abbiamo simulazione proprietaria, blend 60/40
            if (p is not null && p.TotaleGoalSimulati > 0)
                totalGoals = (0.6m * p.TotaleGoalSimulati) + (0.4m * totalGoals);

            var goalsLine = Quantize(totalGoals, 0.5m, 1.0m, 4.5m);

            // 2) Corner
            var (cFh, cSh, cFa, cSa) = ExtractForAgainst(corners);
            var totalCorners = Blend(cFh, cSa) + Blend(cFa, cSh);
            var cornersLine = Quantize(totalCorners, 0.5m, 7.5m, 12.5m); // range tipico 7.5–12.5

            // 3) Cartellinipublic class DetailsModel : PageModel
            // 3) Cartellini
            var (caFh, caSh, caFa, caSa) = ExtractForAgainst(cards);
            var totalCards = Blend(caFh, caSa) + Blend(caFa, caSh);

            // Se i dati dicono pochissimi cartellini, NON forziamo il minimo a 3.5.
            // Range più realistico: da 1.5 a 7.5.
            var cardsLine = Quantize(totalCards, 0.5m, 1.5m, 7.5m);


            // 4) Tiri
            var (sFh, sSh, sFa, sSa) = ExtractForAgainst(shots);
            var totalShots = Blend(sFh, sSa) + Blend(sFa, sSh);
            var shotsLine = Quantize(totalShots, 0.5m, 18.5m, 32.5m); // totali spesso 20–30

            return new IndicativeLines(goalsLine, cornersLine, cardsLine, shotsLine);
        }

    }
}