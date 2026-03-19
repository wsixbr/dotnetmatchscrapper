using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using System.Net.Http;
using System.IO;
using System.Text.RegularExpressions;
using Twilio;
using Twilio.Rest.Api.V2010.Account;

class FlashScoreScraper
{
    private static HttpClient? httpClient;
    private const string BaseUrl = "https://www.odibets.com";
    private const string UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36";
    
    // Twilio WhatsApp Configuration (set these from environment or config)
    private static readonly string? TwilioAccountSid = Environment.GetEnvironmentVariable("TWILIO_ACCOUNT_SID");
    private static readonly string? TwilioAuthToken = Environment.GetEnvironmentVariable("TWILIO_AUTH_TOKEN");
    private static readonly string TwilioPhoneNumber = Environment.GetEnvironmentVariable("TWILIO_WHATSAPP_FROM") ?? "whatsapp:+14155238886"; // Twilio sandbox number
    private static readonly string[] RecipientPhoneNumbers = ParseRecipientList(Environment.GetEnvironmentVariable("RECIPIENT_WHATSAPP") ?? "whatsapp:+1234567890");

    private static string[] ParseRecipientList(string raw)
    {
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Where(v => !string.IsNullOrWhiteSpace(v))
                  .Select(v => v.Trim())
                  .Select(v => v.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase) ? v : $"whatsapp:{v}")
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .ToArray();
    }

    static async Task Main()
    {
        // Return if trial date is reached
        // var trialEndDate = new DateTime(2026, 03, 10);
        // if (DateTime.Now > trialEndDate)
        // {
        //     Console.WriteLine("Trial period has ended.");
        //     System.Threading.Thread.Sleep(5000); // Delay for 5 seconds before exiting
        //     return;
        // }
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() 
        { 
            Headless = true
        });
        
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(120000);
        
        try
        {
            Console.WriteLine("Navigating to FlashScore...");
            
            await page.GotoAsync("https://www.flashscore.com/football/", new()
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 120000
            });
            
            Console.WriteLine("Loading matches...");
            await Task.Delay(15000);
            
            // Accept cookies
            try
            {
                await page.ClickAsync("button:has-text('I Accept')", new() { Timeout = 5000 });
                await Task.Delay(3000);
            }
            catch { }
            
            var matchesJson = await page.EvaluateAsync<string>(@"
                () => {
                    const matches = [];
                    const matchElements = document.querySelectorAll('div.event__match');
                    
                    matchElements.forEach((el) => {
                        try {
                            const homeEl = el.querySelector('.event__homeParticipant .wcl-name_jjfMf, .event__participant--home');
                            const homeTeam = homeEl ? homeEl.textContent.trim() : '';
                            
                            const awayEl = el.querySelector('.event__awayParticipant .wcl-name_jjfMf, .event__participant--away');
                            const awayTeam = awayEl ? awayEl.textContent.trim() : '';
                            
                            const timeEl = el.querySelector('.event__time');
                            let time = timeEl ? timeEl.textContent.trim() : '';
                            
                            const homeScoreEl = el.querySelector('.event__score--home');
                            const awayScoreEl = el.querySelector('.event__score--away');
                            const homeScore = homeScoreEl ? homeScoreEl.textContent.trim() : '';
                            const awayScore = awayScoreEl ? awayScoreEl.textContent.trim() : '';
                            
                            let matchStatus = 'Scheduled';
                            if (el.classList.contains('event__match--live')) {
                                matchStatus = 'Live';
                            } else if (el.classList.contains('event__match--finished')) {
                                matchStatus = 'Finished';
                            }
                            
                            if (homeTeam && awayTeam) {
                                matches.push({
                                    HomeTeam: homeTeam,
                                    AwayTeam: awayTeam,
                                    HomeScore: homeScore,
                                    AwayScore: awayScore,
                                    Time: time,
                                    Status: matchStatus
                                });
                            }
                        } catch (e) {}
                    });
                    
                    return JSON.stringify(matches);
                }
            ");
            
            var allMatches = JsonSerializer.Deserialize<List<MatchData>>(matchesJson) ?? new List<MatchData>();
            
            // Filter matches with scheduled time (exclude empty times)
            var matchesWithTime = allMatches.Where(m => !string.IsNullOrEmpty(m.Time)).ToList();
            
            // Statistics
            var totalMatches = allMatches.Count;
            var liveMatches = allMatches.Count(m => m.Status == "Live");
            var scheduledMatches = allMatches.Count(m => m.Status == "Scheduled");
            var finishedMatches = allMatches.Count(m => m.Status == "Finished");
            var noTimeMatches = allMatches.Count(m => string.IsNullOrEmpty(m.Time));
            
            // Print statistics
            Console.WriteLine("\n╔══════════════════════════════════════════════╗");
            Console.WriteLine("║            MATCH STATISTICS                  ║");
            Console.WriteLine("╠══════════════════════════════════════════════╣");
            Console.WriteLine($"║  Total Matches:              {totalMatches,6}        ║");
            Console.WriteLine($"║  Live:                       {liveMatches,6}        ║");
            Console.WriteLine($"║  Scheduled:                  {scheduledMatches,6}        ║");
            Console.WriteLine($"║  Finished:                   {finishedMatches,6}        ║");
            Console.WriteLine($"║  No Time Determined:         {noTimeMatches,6}        ║");
            Console.WriteLine("╚══════════════════════════════════════════════╝\n");
            
            Console.WriteLine($"Showing {matchesWithTime.Count} matches with scheduled times:\n");
            
            // Print table header
            Console.WriteLine("Time     | Home Team                  | Away Team                  | Status");
            Console.WriteLine("---------|----------------------------|----------------------------|------------------");
            
            foreach (var match in matchesWithTime)
            {
                var time = match.Time.PadRight(8);
                var home = match.HomeTeam;
                var away = match.AwayTeam;
                var status = match.Status;
                
                // Add score to home/away if available
                if (!string.IsNullOrEmpty(match.HomeScore))
                {
                    home = $"{match.HomeTeam} ({match.HomeScore})";
                    away = $"{match.AwayTeam} ({match.AwayScore})";
                }
                
                var homeTeam = home.PadRight(26).Substring(0, 26);
                var awayTeam = away.PadRight(26).Substring(0, 26);
                
                Console.WriteLine($"{time} | {homeTeam} | {awayTeam} | {status}");
            }
            
            // Save to JSON (only matches with time)
            var json = JsonSerializer.Serialize(matchesWithTime, new JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync("flashscore_matches.json", json);
            Console.WriteLine("✓ Saved FlashScore matches to: flashscore_matches.json");

            // Now fetch Odibets data
            Console.WriteLine("\n" + new string('=', 80));
            Console.WriteLine("Fetching Odibets data...");
            Console.WriteLine(new string('=', 80) + "\n");
            
            await InitializeHttpClient();
            var odibetsMatches = await GetOdiBettsMatches();
            
            if (odibetsMatches != null && odibetsMatches.Any())
            {
                Console.WriteLine($"\n✓ Retrieved {odibetsMatches.Count} Odibets matches\n");
                
                // Print Odibets table
                Console.WriteLine("Time     | Home Team                  | Away Team                  | Status");
                Console.WriteLine("---------|----------------------------|----------------------------|------------------");
                
                foreach (var match in odibetsMatches)
                {
                    var time = match.Time.PadRight(8);
                    var homeTeam = match.HomeTeam.PadRight(26).Substring(0, 26);
                    var awayTeam = match.AwayTeam.PadRight(26).Substring(0, 26);
                    var status = match.Status;
                    
                    Console.WriteLine($"{time} | {homeTeam} | {awayTeam} | {status}");
                }
                
                // Save Odibets to JSON
                var odibetsJson = JsonSerializer.Serialize(odibetsMatches, new JsonSerializerOptions { WriteIndented = true });
                await System.IO.File.WriteAllTextAsync("odibets_matches.json", odibetsJson);
                Console.WriteLine("✓ Saved Odibets matches to: odibets_matches.json");
            }
            else
            {
                Console.WriteLine("✗ Failed to fetch Odibets data");
            }
            // Fetch Bangbet matches and save
            Console.WriteLine("\nFetching Bangbet data...");
            var bangbetMatches = await GetBangbetMatches();
            if (bangbetMatches != null && bangbetMatches.Any())
            {
                Console.WriteLine($"\n✓ Retrieved {bangbetMatches.Count} Bangbet matches\n");

                // Print Bangbet table
                Console.WriteLine("Time     | Home Team                  | Away Team                  | Status");
                Console.WriteLine("---------|----------------------------|----------------------------|------------------");
                foreach (var match in bangbetMatches)
                {
                    var time = match.Time.PadRight(8);
                    var homeTeam = match.HomeTeam.PadRight(26).Substring(0, 26);
                    var awayTeam = match.AwayTeam.PadRight(26).Substring(0, 26);
                    var status = match.Status;
                    Console.WriteLine($"{time} | {homeTeam} | {awayTeam} | {status}");
                }

                // Save Bangbet JSON
                var bangJson = JsonSerializer.Serialize(bangbetMatches, new JsonSerializerOptions { WriteIndented = true });
                await System.IO.File.WriteAllTextAsync("bangbet_matches.json", bangJson);
                Console.WriteLine("✓ Saved Bangbet matches to: bangbet_matches.json");
            }
            else
            {
                Console.WriteLine("✗ Failed to fetch Bangbet data");
            }

            // Fetch Betika matches and save
            Console.WriteLine("\nFetching Betika data...");
            var betikaMatches = await GetBetikaMatches();
            if (betikaMatches != null && betikaMatches.Any())
            {
                Console.WriteLine($"\n✓ Retrieved {betikaMatches.Count} Betika matches\n");

                // Print Betika table
                Console.WriteLine("Time     | Home Team                  | Away Team                  | Status");
                Console.WriteLine("---------|----------------------------|----------------------------|------------------");
                foreach (var match in betikaMatches)
                {
                    var time = match.Time.PadRight(8);
                    var homeTeam = match.HomeTeam.PadRight(26).Substring(0, 26);
                    var awayTeam = match.AwayTeam.PadRight(26).Substring(0, 26);
                    var status = match.Status;
                    Console.WriteLine($"{time} | {homeTeam} | {awayTeam} | {status}");
                }

                // Save Betika JSON
                var betJson = JsonSerializer.Serialize(betikaMatches, new JsonSerializerOptions { WriteIndented = true });
                await System.IO.File.WriteAllTextAsync("betika_matches.json", betJson);
                Console.WriteLine("✓ Saved Betika matches to: betika_matches.json");
            }
            else
            {
                Console.WriteLine("✗ Failed to fetch Betika data");
            }

            // Fetch SportPesa matches and save
            Console.WriteLine("\nFetching SportPesa data...");
            var sportpesaMatches = await GetSportPesaMatches();
            if (sportpesaMatches != null && sportpesaMatches.Any())
            {
                Console.WriteLine($"\n✓ Retrieved {sportpesaMatches.Count} SportPesa matches\n");

                // Print SportPesa table
                Console.WriteLine("Time     | Home Team                  | Away Team                  | Status");
                Console.WriteLine("---------|----------------------------|----------------------------|------------------");
                foreach (var match in sportpesaMatches)
                {
                    var time = match.Time.PadRight(8);
                    var homeTeam = match.HomeTeam.PadRight(26).Substring(0, 26);
                    var awayTeam = match.AwayTeam.PadRight(26).Substring(0, 26);
                    var status = match.Status;
                    Console.WriteLine($"{time} | {homeTeam} | {awayTeam} | {status}");
                }

                // Save SportPesa JSON
                var sportJson = JsonSerializer.Serialize(sportpesaMatches, new JsonSerializerOptions { WriteIndented = true });
                await System.IO.File.WriteAllTextAsync("sportpesa_matches.json", sportJson);
                Console.WriteLine("✓ Saved SportPesa matches to: sportpesa_matches.json");
            }
            else
            {
                Console.WriteLine("✗ Failed to fetch SportPesa data");
            }

            // Compare across available sources (flashscore, odibets, bangbet, betika, sportpesa)
            if (matchesWithTime.Any())
            {
                var sources = new List<string> { "flashscore_matches.json", "odibets_matches.json", "bangbet_matches.json", "betika_matches.json", "sportpesa_matches.json" };
                Console.WriteLine("Comparing matches across sources...");
                await CompareAcrossSources(sources);
                Console.WriteLine("Matches compared.");
            }
        }

        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
        
        Console.WriteLine("\nPress Enter to close...");
        Console.ReadLine();
    }

    static async Task InitializeHttpClient()
    {
        var cookies = new System.Net.CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };

        httpClient = new HttpClient(handler);
        httpClient.BaseAddress = new Uri(BaseUrl);

        httpClient.DefaultRequestHeaders.Add("user-agent", UserAgent);
        await httpClient.GetAsync("/");

        httpClient.DefaultRequestHeaders.Clear();
        httpClient.DefaultRequestHeaders.Add("accept", "application/json, text/plain, */*");
        httpClient.DefaultRequestHeaders.Add("referer", $"{BaseUrl}/");
        httpClient.DefaultRequestHeaders.Add("user-agent", UserAgent);
    }

    static async Task<List<OdibetsMatchData>?> GetOdiBettsMatches()
    {
        if (httpClient == null) return null;
        
        var dayToday = DateTime.Now.ToString("yyyy-MM-dd");
        var url = "https://api.odi.site/sportsbook/v1?sport_id=soccer&day=&country_id=&sort_by=&sub_type_id=&competition_id=&hour=&filter=&cs=&hs=&sportsbook=sportsbook&ua=Mozilla%2F5.0+(X11%3B+Linux+x86_64)+AppleWebKit%2F537.36+(KHTML,+like+Gecko)+Chrome%2F143.0.0.0+Safari%2F537.36&resource=sport";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            
            // Add headers to mimic browser request
            request.Headers.Add("accept", "application/json, text/plain, */*");
            // request.Headers.Add("accept-language", "en-US,en;q=0.9");
            // request.Headers.Add("authorization", "Bearer");
            // request.Headers.Add("cache-control", "no-cache");
            // request.Headers.Add("pragma", "no-cache");
            // request.Headers.Add("sec-ch-ua", "\"Google Chrome\";v=\"143\", \"Chromium\";v=\"143\", \"Not A(Brand\";v=\"24\"");
            // request.Headers.Add("sec-ch-ua-mobile", "?0");
            // request.Headers.Add("sec-ch-ua-platform", "\"Linux\"");
            // request.Headers.Add("sec-fetch-dest", "empty");
            // request.Headers.Add("sec-fetch-mode", "cors");
            // request.Headers.Add("sec-fetch-site", "cross-site");
            // request.Headers.Add("user-agent", UserAgent);
            // request.Headers.Referrer = new Uri("https://www.odibets.com/");
            
            var resp = await httpClient.SendAsync(request);
            var content = await resp.Content.ReadAsStringAsync();

            var result = JsonSerializer.Deserialize<SportsbookResponse>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result?.status_code == 200 && result.data != null)
            {
                var competitionIds = new List<string>();
                var matches = new List<OdibetsMatchData>();
                foreach (var cmp in result.data.competitions)
                {
                    competitionIds.Add(cmp.competition_name);
                    var matchesByCompetitionId = await GetOdiBettsMatchesByCompetitionId(cmp.competition_id);
                    matches.AddRange(matchesByCompetitionId);
                    Console.WriteLine($"Matches for competition {cmp.competition_id}: {matchesByCompetitionId?.Count}");
                }
                Console.WriteLine($"Total matches: {matches.Count}");
                Console.WriteLine($"Competition IDs: {string.Join(", ", competitionIds)}");
                return matches;
                // foreach (var league in result.data.leagues)
                // {
                //     Console.WriteLine($"{league.competition_name} - {league.matches.Count}");
                //     foreach (var match in league.matches)
                //     {
                    
                //         // Extract time only (HH:MM) from start_time
                //         var timePart = match.start_time.Length >= 16 
                //             ? match.start_time.Substring(11, 5) 
                //             : match.start_time;
                        
                //         matches.Add(new OdibetsMatchData
                //         {
                //             Time = timePart,
                //             HomeTeam = match.home_team,
                //             AwayTeam = match.away_team,
                //             Status = match.status_desc
                //         });
                //     }
                // }
                // return matches;
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching Odibets sportsbook: {ex.Message}");
            return null;
        }
    }

    static async Task<List<OdibetsMatchData>?> GetOdiBettsMatchesByCompetitionId(string competitionId)
    {
        if (httpClient == null) return null;
        
        var dayToday = DateTime.Now.ToString("yyyy-MM-dd");
        var url = $"https://api.odi.site/odi/sportsbook?day={dayToday}&sport_id=1&sort_by=&sub_type_id=1&competition_id={competitionId}&resource=sportevents&platform=mobile&mode=1";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            
            // Add headers to mimic browser request
            request.Headers.Add("accept", "application/json, text/plain, */*");
            // request.Headers.Add("accept-language", "en-US,en;q=0.9");
            // request.Headers.Add("authorization", "Bearer");
            // request.Headers.Add("cache-control", "no-cache");
            // request.Headers.Add("pragma", "no-cache");
            // request.Headers.Add("sec-ch-ua", "\"Google Chrome\";v=\"143\", \"Chromium\";v=\"143\", \"Not A(Brand\";v=\"24\"");
            // request.Headers.Add("sec-ch-ua-mobile", "?0");
            // request.Headers.Add("sec-ch-ua-platform", "\"Linux\"");
            // request.Headers.Add("sec-fetch-dest", "empty");
            // request.Headers.Add("sec-fetch-mode", "cors");
            // request.Headers.Add("sec-fetch-site", "cross-site");
            // request.Headers.Add("user-agent", UserAgent);
            // request.Headers.Referrer = new Uri("https://www.odibets.com/");
            
            var resp = await httpClient.SendAsync(request);
            var content = await resp.Content.ReadAsStringAsync();

            var result = JsonSerializer.Deserialize<SportsbookResponse>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result?.status_code == 200 && result.data != null)
            {
                var matches = new List<OdibetsMatchData>();
                foreach (var league in result.data.leagues)
                {
                    Console.WriteLine($"{league.competition_name} - {league.matches.Count}");
                    foreach (var match in league.matches)
                    {
                    
                        // Extract time only (HH:MM) from start_time
                        var timePart = match.start_time.Length >= 16 
                            ? match.start_time.Substring(11, 5) 
                            : match.start_time;
                        
                        matches.Add(new OdibetsMatchData
                        {
                            Time = timePart,
                            HomeTeam = match.home_team,
                            AwayTeam = match.away_team,
                            Status = match.status_desc
                        });
                    }
                }
                
                return matches;
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching Odibets sportsbook: {ex.Message}");
            return null;
        }
    }
    
    static async Task<List<OdibetsMatchData>?> GetBangbetMatches()
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("user-agent", UserAgent);
            client.DefaultRequestHeaders.Add("accept", "application/json, text/plain, */*");

            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var beginTime = $"{today}T00:00:00";
            var endTime = $"{today}T23:59:59";

            var url = "https://bet-api.bangbet.com/api/bet/match/list";
            var body = new
            {
                sportId = "sr:sport:1",
                groupIndex = 0,
                tournamentId = "",
                producer = 3,
                position = 17,
                beginTime = beginTime,
                highLight = false,
                endTime = endTime,
                showMarket = true,
                timeZone = "+3",
                page = 1,
                sortType = 2,
                pageSize = 300,
                country = "ke",
                pageNo = 1,
                isMyTeam = false,
                dataGroup = true
            };

            var json = JsonSerializer.Serialize(body);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(url, content);
            var respStr = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(respStr);
            var root = doc.RootElement;
            if (root.TryGetProperty("result", out var resultProp) && resultProp.GetInt32() == 1)
            {
                var matches = new List<OdibetsMatchData>();
                if (root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Object)
                {
                    if (dataProp.TryGetProperty("data", out var groups) && groups.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var group in groups.EnumerateArray())
                        {
                            if (!group.TryGetProperty("matchVoList", out var matchList) || matchList.ValueKind != JsonValueKind.Array) continue;

                            foreach (var m in matchList.EnumerateArray())
                            {
                                var home = m.TryGetProperty("homeTeamName", out var ht) ? ht.GetString() ?? "" : "";
                                var away = m.TryGetProperty("awayTeamName", out var at) ? at.GetString() ?? "" : "";
                                var scheduledDate = m.TryGetProperty("scheduledDate", out var sd) ? sd.GetString() ?? "" : "";
                                var matchStatus = m.TryGetProperty("matchStatus", out var ms) ? ms.GetString() ?? "" : "";

                                string timePart = "";
                                if (!string.IsNullOrEmpty(scheduledDate))
                                {
                                    try
                                    {
                                        var dt = DateTime.Parse(scheduledDate);
                                        // dt = dt.AddHours(3); // Remove offset, assume UTC is correct
                                        timePart = dt.ToString("HH:mm");
                                    }
                                    catch
                                    {
                                        timePart = scheduledDate.Length >= 16 ? scheduledDate.Substring(11, 5) : scheduledDate;
                                    }
                                }

                                var status = "Scheduled";
                                if (matchStatus != null)
                                {
                                    var msLower = matchStatus.ToLower();
                                    if (msLower.Contains("in_play") || msLower.Contains("live")) status = "Live";
                                    else if (msLower.Contains("finished") || msLower.Contains("ended")) status = "Finished";
                                }

                                if (!string.IsNullOrEmpty(home) && !string.IsNullOrEmpty(away))
                                {
                                    matches.Add(new OdibetsMatchData
                                    {
                                        Time = timePart,
                                        HomeTeam = home,
                                        AwayTeam = away,
                                        Status = status
                                    });
                                }
                            }
                        }
                    }
                }

                return matches;
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching Bangbet data: {ex.Message}");
            return null;
        }
    }
    
    static async Task<List<OdibetsMatchData>?> GetBetikaMatches()
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("user-agent", UserAgent);
            client.DefaultRequestHeaders.Add("accept", "application/json, text/plain, */*");

            var url = "https://api.betika.com/v1/uo/matches?page=1&limit=1000&tab=upcoming&sub_type_id=1,186,340&sport_id=14&sort_id=2&period_id=9&esports=false";

            var resp = await client.GetAsync(url);
            var respStr = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(respStr);
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Array)
            {
                var matches = new List<OdibetsMatchData>();
                foreach (var m in dataProp.EnumerateArray())
                {
                    var home = m.TryGetProperty("home_team", out var ht) ? ht.GetString() ?? "" : "";
                    var away = m.TryGetProperty("away_team", out var at) ? at.GetString() ?? "" : "";
                    var startTime = m.TryGetProperty("start_time", out var st) ? st.GetString() ?? "" : "";

                    string timePart = "";
                    if (!string.IsNullOrEmpty(startTime) && startTime.Length >= 16)
                    {
                        try { timePart = startTime.Substring(11, 5); }
                        catch { timePart = startTime; }
                    }

                    var status = "Scheduled"; // Assuming upcoming

                    if (!string.IsNullOrEmpty(home) && !string.IsNullOrEmpty(away))
                    {
                        matches.Add(new OdibetsMatchData
                        {
                            Time = timePart,
                            HomeTeam = home,
                            AwayTeam = away,
                            Status = status
                        });
                    }
                }

                return matches;
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching Betika data: {ex.Message}");
            return null;
        }
    }
    
    static async Task<List<OdibetsMatchData>?> GetSportPesaMatches()
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("user-agent", UserAgent);
            client.DefaultRequestHeaders.Add("accept", "application/json, text/plain, */*");

            var url = "https://www.ke.sportpesa.com/api/todays/1/games?type=prematch&section=today&markets_layout=single&o=startTime&pag_count=1000&pag_min=1";

            var resp = await client.GetAsync(url);
            var respStr = await resp.Content.ReadAsStringAsync();

            var matches = JsonSerializer.Deserialize<List<SportPesaMatch>>(respStr, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (matches != null)
            {
                var result = new List<OdibetsMatchData>();
                foreach (var m in matches)
                {
                    var home = m.competitors?.Count > 0 ? m.competitors[0].name ?? "" : "";
                    var away = m.competitors?.Count > 1 ? m.competitors[1].name ?? "" : "";
                    var dateStr = m.date ?? "";

                    string timePart = "";
                    if (!string.IsNullOrEmpty(dateStr))
                    {
                        try
                        {
                            var dt = DateTime.Parse(dateStr);
                            timePart = dt.ToString("HH:mm");
                        }
                        catch
                        {
                            timePart = dateStr.Length >= 16 ? dateStr.Substring(11, 5) : dateStr;
                        }
                    }

                    var status = "Scheduled";

                    if (!string.IsNullOrEmpty(home) && !string.IsNullOrEmpty(away))
                    {
                        result.Add(new OdibetsMatchData
                        {
                            Time = timePart,
                            HomeTeam = home,
                            AwayTeam = away,
                            Status = status
                        });
                    }
                }

                return result;
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching SportPesa data: {ex.Message}");
            return null;
        }
    }

    static async Task CompareAcrossSources(List<string> sourceFiles)
    {
        var allEntries = new List<Dictionary<string, string>>();
        var siteNameMap = new Dictionary<string, string>
        {
            { "flashscore_matches.json", "FlashScore" },
            { "odibets_matches.json", "Odibets" },
            { "bangbet_matches.json", "Bangbet" },
            { "betika_matches.json", "Betika" },
            { "sportpesa_matches.json", "SportPesa" }
        };

        foreach (var file in sourceFiles)
        {
            if (!File.Exists(file)) continue;
            var rows = LoadJson(file);
            Console.WriteLine($"\n--- Raw times from {file} ---");
            foreach (var r in rows)
            {
                var rawTime = r.TryGetValue("Time", out var t) ? t : "";
                var home = r.TryGetValue("HomeTeam", out var h) ? h : "";
                var away = r.TryGetValue("AwayTeam", out var a) ? a : "";
                Console.WriteLine($"{rawTime} | {home} vs {away}");
            }
            foreach (var r in rows)
            {
                var entry = new Dictionary<string, string>(r);
                entry["Source"] = siteNameMap.TryGetValue(file, out var site) ? site : file;
                entry["CleanTime"] = CleanTime(r.TryGetValue("Time", out var t) ? t : "");
                entry["CleanHome"] = NormalizeTeam(r.TryGetValue("HomeTeam", out var h) ? h : "");
                entry["CleanAway"] = NormalizeTeam(r.TryGetValue("AwayTeam", out var a) ? a : "");
                allEntries.Add(entry);
            }
        }

        var varying = new List<List<Dictionary<string, string>>>();
        var used = new bool[allEntries.Count];

        for (int i = 0; i < allEntries.Count; i++)
        {
            if (used[i]) continue;
            var group = new List<Dictionary<string, string>> { allEntries[i] };
            used[i] = true;

            for (int j = i + 1; j < allEntries.Count; j++)
            {
                if (used[j]) continue;
                var homeSim = Similarity(allEntries[i]["CleanHome"], allEntries[j]["CleanHome"]);
                var awaySim = Similarity(allEntries[i]["CleanAway"], allEntries[j]["CleanAway"]);
                if (homeSim > 0.7 && awaySim > 0.7)
                {
                    // Check time difference - only group if within 2 hours
                    var timeDiff = GetTimeDifferenceInHours(allEntries[i]["CleanTime"], allEntries[j]["CleanTime"]);
                    if (timeDiff <= 2.0)
                    {
                        group.Add(allEntries[j]);
                        used[j] = true;
                    }
                }
            }

            if (group.Count > 1)
            {
                var times = group.Select(g => g["CleanTime"]).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();
                if (times.Count > 1)
                {
                    varying.Add(group);
                }
            }
        }

        if (varying.Any())
        {
            Console.WriteLine($"\nFound {varying.Count} groups with varying times across sources:");
            Console.WriteLine("Source | Time | Home | Away");
            Console.WriteLine(new string('-', 100));

            var csvLines = new List<string> { "GroupId,Source,Time,Home,Away" };
            var whatsappMessage = new System.Text.StringBuilder("Matches\n\n");
            
            int gid = 1;
            foreach (var g in varying)
            {
                whatsappMessage.Append($"*Group {gid}:* ");
                foreach (var e in g)
                {
                    Console.WriteLine($"{e["Source"],-12} | {e["Time"],-8} | {e["HomeTeam"],-25} | {e["AwayTeam"]}");
                    csvLines.Add($"\"{gid}\",\"{e["Source"]}\",\"{e["Time"]}\",\"{e["HomeTeam"]}\",\"{e["AwayTeam"]}\"");
                }
                whatsappMessage.Append($"\n{g[0]["HomeTeam"]} vs {g[0]["AwayTeam"]}\n");
                foreach (var e in g)
                {
                    whatsappMessage.Append($"  {e["Source"]}: {e["Time"]}\n");
                }
                whatsappMessage.Append("\n");
                gid++;
                Console.WriteLine(new string('-', 100));
            }

            File.WriteAllText("varying_matches.csv", string.Join("\n", csvLines));
            Console.WriteLine("\n✓ Saved to varying_matches.csv");
            
            // Send WhatsApp alert
            await SendWhatsAppAlert(whatsappMessage.ToString());
        }
        else
        {
            Console.WriteLine("\nNo matches with varying times found across sources.");
        }
    }
    
    static string TruncateOrPad(string text, int length)
    {
        if (text.Length > length)
            return text.Substring(0, length - 3) + "...";
        return text.PadRight(length);
    }

    static List<Dictionary<string, string>> LoadJson(string filePath)
    {
        var matches = JsonSerializer.Deserialize<List<OdibetsMatchData>>(File.ReadAllText(filePath)) ?? new List<OdibetsMatchData>();
        var dicts = new List<Dictionary<string, string>>();
        foreach (var m in matches)
        {
            dicts.Add(new Dictionary<string, string>
            {
                { "Time", m.Time },
                { "HomeTeam", m.HomeTeam },
                { "AwayTeam", m.AwayTeam },
                { "Status", m.Status }
            });
        }
        return dicts;
    }

    static string CleanTime(string timeStr)
    {
        var match = Regex.Match(timeStr, @"(\d{2}:\d{2})");
        return match.Success ? match.Groups[1].Value : "";
    }

    static string NormalizeTeam(string team)
    {
        team = Regex.Replace(team, @"\s*\(-.*\)", ""); // Remove (-...)
        team = Regex.Replace(team, @"\s*(FC|SC|BC|FK|AC|AS|SG|Utd|United|Turin|Istanbul|Olympique|Eintracht|SL|Ath|Athletic|Royale|Saint-Gilloise|Union)", "", RegexOptions.IgnoreCase);
        team = Regex.Replace(team, @"[^\w\s]", "").Trim().ToLower();
        return team;
    }

    static double GetTimeDifferenceInHours(string time1, string time2)
    {
        if (!TimeSpan.TryParse(time1, out var t1) || !TimeSpan.TryParse(time2, out var t2)) return double.MaxValue;
        var diff = Math.Abs((t1 - t2).TotalHours);
        return diff;
    }

    static double Similarity(string s1, string s2)
    {
        int[,] matrix = new int[s1.Length + 1, s2.Length + 1];
        for (int i = 0; i <= s1.Length; i++) matrix[i, 0] = i;
        for (int j = 0; j <= s2.Length; j++) matrix[0, j] = j;

        for (int i = 1; i <= s1.Length; i++)
            for (int j = 1; j <= s2.Length; j++)
            {
                int cost = (s2[j - 1] == s1[i - 1]) ? 0 : 1;
                matrix[i, j] = Math.Min(Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1), matrix[i - 1, j - 1] + cost);
            }

        int maxLen = Math.Max(s1.Length, s2.Length);
        return maxLen == 0 ? 1.0 : 1.0 - (double)matrix[s1.Length, s2.Length] / maxLen;
    }

    static async Task SendWhatsAppAlert(string message)
    {
        try
        {
            // Check if Twilio credentials are configured
            if (string.IsNullOrEmpty(TwilioAccountSid) || string.IsNullOrEmpty(TwilioAuthToken))
            {
                Console.WriteLine("\n⚠️  WhatsApp Alert: Twilio credentials not configured.");
                Console.WriteLine("   Set environment variables: TWILIO_ACCOUNT_SID, TWILIO_AUTH_TOKEN");
                Console.WriteLine("   Optional: TWILIO_WHATSAPP_FROM, RECIPIENT_WHATSAPP");
                return;
            }

            // Initialize Twilio client with credentials
            Twilio.TwilioClient.Init(TwilioAccountSid, TwilioAuthToken);

            // Ensure we have at least one recipient
            if (RecipientPhoneNumbers.Length == 0)
            {
                Console.WriteLine("\n⚠️  WhatsApp Alert: No recipient numbers configured (RECIPIENT_WHATSAPP).");
                return;
            }

            foreach (var recipient in RecipientPhoneNumbers)
            {
                var msg = await MessageResource.CreateAsync(
                    body: message,
                    from: new Twilio.Types.PhoneNumber(TwilioPhoneNumber),
                    to: new Twilio.Types.PhoneNumber(recipient)
                );
                Console.WriteLine($"\n✓ WhatsApp alert sent to {recipient} (SID: {msg.Sid})");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n✗ Failed to send WhatsApp alert: {ex.Message}");
        }
    }
}

public class MatchData
{
    public string HomeTeam { get; set; } = "";
    public string AwayTeam { get; set; } = "";
    public string HomeScore { get; set; } = "";
    public string AwayScore { get; set; } = "";
    public string Time { get; set; } = "";
    public string Status { get; set; } = "";
}

public class OdibetsMatchData
{
    public string Time { get; set; } = "";
    public string HomeTeam { get; set; } = "";
    public string AwayTeam { get; set; } = "";
    public string Status { get; set; } = "";
}

// Odibets API Response Classes
public class SportsbookResponse
{
    public int status_code { get; set; }
    public string status_description { get; set; } = string.Empty;
    public SportsbookData? data { get; set; }
}

public class SportsbookData
{
    public List<object>? days { get; set; } = new();
    public List<object>? sports { get; set; } = new();
    public List<Market>? markets { get; set; } = new();
    public List<League>? leagues { get; set; } = new();
    public List<object>? hours { get; set; } = new();
    public List<Competition>? competitions { get; set; } = new();
    public SportsbookMeta? meta { get; set; }
}

public class Market
{
    public string sub_type_id { get; set; } = string.Empty;
    public string odd_type { get; set; } = string.Empty;
}

public class Competition
{
    public string competition_id { get; set; } = string.Empty;
    public string competition_name { get; set; } = string.Empty;
    public string c_binomen { get; set; } = string.Empty;
    public string country_name { get; set; } = string.Empty;
    public string match_count { get; set; } = string.Empty;
    public int i { get; set; } = 0;
}

public class League
{
    public string competition_id { get; set; } = string.Empty;
    public string competition_name { get; set; } = string.Empty;
    public int match_count { get; set; } = 0;
    public string category_id { get; set; } = string.Empty;
    public string category_name { get; set; } = string.Empty;
    public string sport_id { get; set; } = string.Empty;
    public string sport_name { get; set; } = string.Empty;
    public List<Match> matches { get; set; } = new();
}

public class Match
{
    public string parent_match_id { get; set; } = string.Empty;
    public string home_team { get; set; } = string.Empty;
    public string away_team { get; set; } = string.Empty;
    public string start_time { get; set; } = string.Empty;
    public string schedule_date { get; set; } = string.Empty;
    public string competition_name { get; set; } = string.Empty;
    public string category_name { get; set; } = string.Empty;
    public string status { get; set; } = string.Empty;
    public string status_desc { get; set; } = string.Empty;
    public List<MatchMarket> markets { get; set; } = new();
}

public class MatchMarket
{
    public string sub_type_id { get; set; } = string.Empty;
    public string odd_type { get; set; } = string.Empty;
    public List<Line> lines { get; set; } = new();
}

public class Line
{
    public string specifiers { get; set; } = string.Empty;
    public List<Outcome> outcomes { get; set; } = new();
}

public class Outcome
{
    public string outcome_id { get; set; } = string.Empty;
    public string outcome_key { get; set; } = string.Empty;
    public string outcome_name { get; set; } = string.Empty;
    public int active { get; set; }
    public int status { get; set; }
    public string odd_value { get; set; } = string.Empty;
    public string prev_odd_value { get; set; } = string.Empty;
}

public class SportsbookMeta
{
    public int producer { get; set; } = 0;
    public string day { get; set; } = string.Empty;
    public string sport_id { get; set; } = string.Empty;
    public string total { get; set; } = string.Empty;
}

public class SportPesaMatch
{
    public int id { get; set; }
    public int betgeniusId { get; set; }
    public long betradarId { get; set; }
    public int smsId { get; set; }
    public int hasCustomBet { get; set; }
    public SportPesaCompetition? competition { get; set; }
    public SportPesaCountry? country { get; set; }
    public SportPesaSport? sport { get; set; }
    public List<SportPesaCompetitor>? competitors { get; set; }
    public long dateTimestamp { get; set; }
    public string? date { get; set; }
    public object? state { get; set; }
    public int marketsCount { get; set; }
    public List<SportPesaMarket>? markets { get; set; }
}

public class SportPesaCompetition
{
    public int id { get; set; }
    public string? name { get; set; }
}

public class SportPesaCountry
{
    public int id { get; set; }
    public string? name { get; set; }
    public string? iso { get; set; }
}

public class SportPesaSport
{
    public int id { get; set; }
    public string? name { get; set; }
}

public class SportPesaCompetitor
{
    public int id { get; set; }
    public string? name { get; set; }
}

public class SportPesaMarket
{
    public int id { get; set; }
    public string? name { get; set; }
    public int columns { get; set; }
    public int columnsApp { get; set; }
    public List<SportPesaSelection>? selections { get; set; }
}

public class SportPesaSelection
{
    public long id { get; set; }
    public string? name { get; set; }
    public string? odds { get; set; }
    public string? shortName { get; set; }
    public int specValue { get; set; }
}
