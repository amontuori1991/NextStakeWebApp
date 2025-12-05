using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;
using WebPush;

// alias per evitare ambiguità
using DbPushSubscription = NextStakeWebApp.Models.PushSubscription;
using WebPushSubscription = WebPush.PushSubscription;

namespace NextStakeWebApp.Services
{
    public class LiveNotifyWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IHttpClientFactory _httpFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<LiveNotifyWorker> _logger;

        // intervallo tra un ciclo e l’altro
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

        private static readonly string[] LIVE_STATUSES = { "1H", "2H", "ET", "P", "BT", "LIVE", "HT" };
        private static readonly string[] FINISHED_STATUSES = { "FT", "AET", "PEN" };

        public LiveNotifyWorker(
            IServiceScopeFactory scopeFactory,
            IHttpClientFactory httpFactory,
            IConfiguration config,
            ILogger<LiveNotifyWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _httpFactory = httpFactory;
            _config = config;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("✅ LiveNotifyWorker avviato");

            using var timer = new PeriodicTimer(Interval);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await RunAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Errore nel ciclo LiveNotifyWorker");
                }
            }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            // Chiavi minime: se mancano, non facciamo nulla
            var apiKey =
                _config["ApiSports:Key"] ??
                Environment.GetEnvironmentVariable("ApiSports__Key");

            var publicKey =
                _config["Vapid:PublicKey"] ??
                _config["VAPID_PUBLIC_KEY"] ??
                Environment.GetEnvironmentVariable("VAPID_PUBLIC_KEY");

            var privateKey =
                _config["Vapid:PrivateKey"] ??
                _config["VAPID_PRIVATE_KEY"] ??
                Environment.GetEnvironmentVariable("VAPID_PRIVATE_KEY");

            var subject =
                _config["Vapid:Subject"] ??
                _config["VAPID_SUBJECT"] ??
                Environment.GetEnvironmentVariable("VAPID_SUBJECT") ??
                "mailto:info@nextstake.app";

            if (string.IsNullOrWhiteSpace(apiKey) ||
                string.IsNullOrWhiteSpace(publicKey) ||
                string.IsNullOrWhiteSpace(privateKey))
            {
                // chiavi mancanti: esco in silenzio
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var readDb = scope.ServiceProvider.GetRequiredService<ReadDbContext>();
            var writeDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var vapidDetails = new VapidDetails(subject, publicKey, privateKey);
            var pushClient = new WebPushClient();
            var nowUtc = DateTime.UtcNow;

            // --- 1) Chiamata API-FOOTBALL per i live --------------------------
            var http = _httpFactory.CreateClient("ApiSports");
            var req = new HttpRequestMessage(
                HttpMethod.Get,
                "fixtures?live=all&timezone=Europe/Rome"
            );
            req.Headers.Add("x-apisports-key", apiKey);

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return;

            var raw = await resp.Content.ReadAsStringAsync(ct);
            var fixtures = JsonSerializer.Deserialize<ApiFootballFixturesResponse>(raw);

            var liveList = fixtures?.Response ?? new List<ApiFixtureItem>();
            if (liveList.Count == 0)
                return;

            // tutti gli ID live come long
            var liveIds = liveList
                .Select(f => (long)f.Fixture.Id)
                .Distinct()
                .ToList();   // List<long>

            // --- 2) Meta-dati (lega, loghi, nomi squadre) ---------------------
            var meta = await (
                from m in readDb.Matches
                join lg in readDb.Leagues on m.LeagueId equals lg.Id
                join th in readDb.Teams on m.HomeId equals th.Id
                join ta in readDb.Teams on m.AwayId equals ta.Id
                where liveIds.Contains((long)m.Id)
                select new
                {
                    MatchId = (long)m.Id,
                    LeagueName = lg.Name,
                    LeagueLogo = lg.Logo,
                    HomeName = th.Name,
                    HomeLogo = th.Logo,
                    AwayName = ta.Name,
                    AwayLogo = ta.Logo
                }
            ).ToListAsync(ct);

            var metaById = meta.ToDictionary(x => x.MatchId, x => x);

            // --- 3) Stati precedenti dei live ---------------------------------
            var existingStates = await writeDb.LiveMatchStates
                .Where(s => liveIds.Contains((long)s.MatchId))
                .ToListAsync(ct);

            var stateById = existingStates.ToDictionary(s => (long)s.MatchId, s => s);

            // --- 4) Preferiti + subscription per utente -----------------------
            var favPerMatch = await writeDb.FavoriteMatches
                .Where(fm => liveIds.Contains(fm.MatchId)) // fm.MatchId è long → OK
                .GroupBy(fm => fm.MatchId)
                .Select(g => new
                {
                    MatchId = g.Key,
                    UserIds = g.Select(fm => fm.UserId).Distinct().ToList()
                })
                .ToListAsync(ct);

            var favUsersByMatch = favPerMatch.ToDictionary(x => x.MatchId, x => x.UserIds);

            var activeSubs = await writeDb.PushSubscriptions
                .Where(s => s.IsActive && s.MatchNotificationsEnabled && s.UserId != null)
                .ToListAsync(ct);

            var subsByUser = activeSubs
                .GroupBy(s => s.UserId!)
                .ToDictionary(g => g.Key, g => g.ToList());

            // --- 5) Rilevazione eventi + costruzione notifiche ----------------
            var toSend = new List<(DbPushSubscription sub, string title, string body, string url)>();

            foreach (var item in liveList)
            {
                var id = (long)item.Fixture.Id;   // long

                var status = (item.Fixture.Status.Short ?? "").ToUpperInvariant();
                var elapsed = item.Fixture.Status.Elapsed;
                var home = item.Goals.Home;
                var away = item.Goals.Away;

                metaById.TryGetValue(id, out var mMeta);
                var leagueName = mMeta?.LeagueName ?? "Match";
                var homeName = mMeta?.HomeName ?? "Home";
                var awayName = mMeta?.AwayName ?? "Away";

                stateById.TryGetValue(id, out var prev);

                var prevStatus = prev?.LastStatus;
                var prevHome = prev?.LastHome;
                var prevAway = prev?.LastAway;

                bool isLiveNow = LIVE_STATUSES.Contains(status);
                bool isLivePrev = prevStatus != null && LIVE_STATUSES.Contains(prevStatus);
                bool isFinishedNow = FINISHED_STATUSES.Contains(status);
                bool isFinishedPrev = prevStatus != null && FINISHED_STATUSES.Contains(prevStatus);

                int? currHome = home;
                int? currAway = away;
                int prevHomeVal = prevHome ?? 0;
                int prevAwayVal = prevAway ?? 0;

                bool scoreChanged = prev != null &&
                                    (prevHome != currHome || prevAway != currAway);

                int prevTotal = prevHomeVal + prevAwayVal;
                int currTotal = (currHome ?? 0) + (currAway ?? 0);

                // Non ci sono utenti che hanno questo match nei preferiti -> aggiorniamo solo stato
                if (!favUsersByMatch.TryGetValue(id, out var userIdsForMatch) || userIdsForMatch.Count == 0)
                {
                    if (prev == null)
                    {
                        writeDb.LiveMatchStates.Add(new LiveMatchState
                        {
                            MatchId = (int)id,
                            LastStatus = status,
                            LastHome = currHome,
                            LastAway = currAway,
                            LastElapsed = elapsed,
                            LastUpdatedUtc = nowUtc
                        });
                    }
                    else
                    {
                        prev.LastStatus = status;
                        prev.LastHome = currHome;
                        prev.LastAway = currAway;
                        prev.LastElapsed = elapsed;
                        prev.LastUpdatedUtc = nowUtc;
                    }

                    continue;
                }

                // Se non avevamo stato precedente -> inizializziamo e non notifichiamo
                if (prev == null)
                {
                    writeDb.LiveMatchStates.Add(new LiveMatchState
                    {
                        MatchId = (int)id,
                        LastStatus = status,
                        LastHome = currHome,
                        LastAway = currAway,
                        LastElapsed = elapsed,
                        LastUpdatedUtc = nowUtc
                    });
                    continue;
                }

                // Abbiamo prev: calcoliamo gli "eventi"
                var events = new List<string>();

                // Inizio partita: da NON-live a live
                if (!isLivePrev && isLiveNow)
                {
                    events.Add("start");
                }

                // Fine primo tempo: 1H -> HT
                if (prevStatus == "1H" && status == "HT")
                {
                    events.Add("halftime");
                }

                // Inizio secondo tempo: HT -> 2H
                if (prevStatus == "HT" && status == "2H")
                {
                    events.Add("second");
                }

                // Fine partita (se un giorno avremo FT in live=all)
                if (!isFinishedPrev && isFinishedNow)
                {
                    events.Add("end");
                }

                // Goal / Rettifica (VAR)
                if (scoreChanged && !isFinishedNow)
                {
                    if (currTotal > prevTotal)
                    {
                        events.Add("goal");
                    }
                    else if (currTotal < prevTotal)
                    {
                        events.Add("correction");
                    }
                }

                if (events.Count > 0)
                {
                    var scoreStr = (currHome.HasValue && currAway.HasValue)
                        ? $"{currHome}-{currAway}"
                        : "";

                    var minuteStr = elapsed.HasValue ? $"{elapsed}'" : null;
                    var matchLabel = $"{homeName} - {awayName}";
                    var leagueLabel = leagueName;

                    foreach (var ev in events)
                    {
                        string title;
                        string body;

                        switch (ev)
                        {
                            case "start":
                                title = "Inizio partita";
                                body = $"{leagueLabel} | {matchLabel} è iniziata.";
                                break;
                            case "halftime":
                                title = "Fine primo tempo";
                                body = $"{leagueLabel} | {matchLabel} | HT {scoreStr}";
                                break;
                            case "second":
                                title = "Inizio secondo tempo";
                                body = $"{leagueLabel} | {matchLabel} | Ripresa in corso.";
                                break;
                            case "end":
                                title = "Partita terminata";
                                body = $"{leagueLabel} | {matchLabel} | Finale {scoreStr}";
                                break;
                            case "goal":
                                title = "GOAL!";
                                body = $"{leagueLabel} | {matchLabel} | {scoreStr}"
                                     + (minuteStr != null ? $" al {minuteStr}" : "");
                                break;
                            case "correction":
                                title = "Rettifica punteggio";
                                body = $"{leagueLabel} | {matchLabel} | Nuovo punteggio {scoreStr}";
                                break;
                            default:
                                title = "Aggiornamento match";
                                body = $"{leagueLabel} | {matchLabel}";
                                break;
                        }

                        var url = $"/Match/Details?id={id}";

                        foreach (var userId in userIdsForMatch)
                        {
                            if (!subsByUser.TryGetValue(userId, out var userSubs)) continue;

                            foreach (var sub in userSubs)
                            {
                                toSend.Add((sub, title, body, url));
                            }
                        }
                    }
                }

                // Aggiorna stato a DB
                prev.LastStatus = status;
                prev.LastHome = currHome;
                prev.LastAway = currAway;
                prev.LastElapsed = elapsed;
                prev.LastUpdatedUtc = nowUtc;
            }

            // Salva gli stati aggiornati
            await writeDb.SaveChangesAsync(ct);

            // --- 6) Invio WebPush ----------------------------------------------
            int success = 0, failed = 0;

            foreach (var item in toSend)
            {
                var sub = item.sub;

                var payloadObj = new
                {
                    title = item.title,
                    body = item.body,
                    url = item.url
                    // in futuro: icon = leagueLogo / homeLogo, ecc.
                };

                var payloadJson = JsonSerializer.Serialize(payloadObj);
                var pushSub = new WebPushSubscription(sub.Endpoint, sub.P256Dh, sub.Auth);

                try
                {
                    await pushClient.SendNotificationAsync(pushSub, payloadJson, vapidDetails, ct);
                    success++;
                }
                catch (WebPushException wex)
                {
                    if (wex.StatusCode == System.Net.HttpStatusCode.Gone ||
                        wex.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        sub.IsActive = false;
                    }
                    failed++;
                    _logger.LogWarning("LIVEPUSH errore {Status} {Msg}", wex.StatusCode, wex.Message);
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogError(ex, "LIVEPUSH eccezione generica");
                }
            }

            if (failed > 0)
            {
                await writeDb.SaveChangesAsync(ct);
            }

            if (success > 0)
            {
                _logger.LogInformation("LIVEPUSH: inviate {Success} notifiche (fallite {Failed})", success, failed);
            }
        }
    }
}
