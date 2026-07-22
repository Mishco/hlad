using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace HladScraper;

public record Restaurant(int Id, string Name, string Address, string WebsiteUrl, string? PhoneNumber, double Rating, List<string> Tags);
public record MenuItem(string Name, decimal Price, string? Description, bool IsVegetarian);
public record DailyMenu(int RestaurantId, string Date, string? SoupOfTheDay, List<MenuItem> Items, string LastUpdated);
public record MenuData(List<Restaurant> Restaurants, List<DailyMenu> Menus, string GeneratedAt);
class Program
{
    static readonly HttpClient Http = new();
    static readonly string? GeminiApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
    static readonly string? MistralApiKey = Environment.GetEnvironmentVariable("MISTRAL_API_KEY");
    static readonly string? LlmProvider = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MISTRAL_API_KEY")) ? "mistral"
                                        : !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GEMINI_API_KEY")) ? "gemini"
                                        : null;

    static async Task Main(string[] args)
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

        var restaurants = GetRestaurants();
        var weekMenus = new List<DailyMenu>();
        var today = DateTime.Today;
        var monday = today.AddDays(-(int)today.DayOfWeek + 1);

        if (today.DayOfWeek == DayOfWeek.Saturday || today.DayOfWeek == DayOfWeek.Sunday)
        {
            Console.WriteLine("Weekend - using static data only");
        }

        var useAI = LlmProvider != null;
        if (useAI)
            Console.WriteLine($"AI scraping enabled ({LlmProvider})\n");
        else
            Console.WriteLine("No MISTRAL_API_KEY or GEMINI_API_KEY - using legacy scrapers + static data\n");

        foreach (var r in restaurants)
        {
            Console.Write($"Scraping {r.Name}... ");
            List<DailyMenu>? menus = null;

            try
            {
                if (useAI)
                {
                    menus = await ScrapeWithAI(r);
                }

                // Fallback to legacy scrapers if AI didn't work
                if (menus == null || menus.Count == 0)
                {
                    // Try multi-day scrapers first (Plzeňka, VEG have full week on one page)
                    if (r.WebsiteUrl.Contains("popradskaplzenka.sk") || r.WebsiteUrl.Contains("zavolatobsluhu.sk"))
                    {
                        menus = new List<DailyMenu>();
                        for (int d = 0; d < 5; d++)
                        {
                            var date = monday.AddDays(d);
                            DailyMenu? dayMenu = null;
                            try
                            {
                                dayMenu = r.WebsiteUrl.Contains("popradskaplzenka.sk")
                                    ? await ScrapePlzenka(r.Id, r.WebsiteUrl, date)
                                    : await ScrapeVeg(r.Id, r.WebsiteUrl, date);
                            }
                            catch { }
                            if (dayMenu != null) menus.Add(dayMenu);
                        }
                        if (menus.Count == 0) menus = null;
                    }
                    else
                    {
                        // Single-day scrapers
                        var todayMenu = r.WebsiteUrl switch
                        {
                            var u when u.Contains("forumpoprad.sk") => await ScrapeForumPoprad(r.Id, u),
                            var u when u.Contains("menucka.sk") => await ScrapeMenuckaSk(r.Id, u),
                            var u when u.Contains("angrychef.sk") => await ScrapeAngryChef(r.Id, u),
                            _ => null
                        };
                        if (todayMenu != null)
                            menus = new List<DailyMenu> { todayMenu };
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Write($"ERROR: {ex.Message} ");
            }

            // For each day Mon-Fri, add scraped or static menu
            int addedCount = 0;
            for (int d = 0; d < 5; d++)
            {
                var date = monday.AddDays(d);
                var dateStr = DateOnly.FromDateTime(date).ToString("yyyy-MM-dd");
                var scraped = menus?.FirstOrDefault(m => m.Date == dateStr);

                if (scraped != null)
                {
                    weekMenus.Add(scraped);
                    addedCount += scraped.Items.Count;
                }
                else
                {
                    var staticMenu = GetStaticMenu(r.Id, date);
                    if (staticMenu != null)
                    {
                        weekMenus.Add(staticMenu);
                        addedCount += staticMenu.Items.Count;
                    }
                }
            }

            Console.WriteLine($"OK ({addedCount} items across week)");
        }

        var output = new MenuData(
            Restaurants: restaurants,
            Menus: weekMenus,
            GeneratedAt: DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        );

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var outputPath = args.Length > 0 ? args[0] : "../docs/menus.json";
        var json = JsonSerializer.Serialize(output, jsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, json);

        Console.WriteLine($"\nWritten to {outputPath} ({weekMenus.Count} menus for {restaurants.Count} restaurants)");
    }

    static List<Restaurant> GetRestaurants() => new()
    {
        new(1, "Popradská Plzeňka", "Námestie sv. Egídia 9/19, Poprad", "https://www.popradskaplzenka.sk/obedove-menu", "+421 905 499 994", 4.9, new() { "Slovenská", "Tradičná", "Obedy" }),
        new(2, "Aquacity Poprad - High Tatras", "Športová 1397/1, 058 01 Poprad", "https://aquacity.sk/sluzby/menu/", null, 3.8, new() { "Hotel", "Chef's menu", "Medzinárodná" }),
        new(3, "Rock'n'Roll Steak Pub (Forum Poprad)", "Námestie sv. Egídia 3290/124, Poprad", "https://forumpoprad.sk/ponuka/obedove-menu/", "+421 948 007 051", 4.8, new() { "Steaky", "Burgre", "Pub" }),
        new(4, "Barn Club", "Francisciho 19, 058 01 Poprad", "https://menucka.sk/denne-menu/poprad/barn-club", "052/772 12 00", 4.7, new() { "Slovenská", "Pub", "Obedy" }),
        new(5, "Angry Chef", "Námestie svätého Egídia 10/23, 058 01 Poprad", "https://www.angrychef.sk/sk/sk-menu/", "+421 910 565 685", 4.5, new() { "Ázijská", "Street Food", "Bao", "Bowls" }),
        new(6, "Mamut Pub & Restaurant", "Moyzesova 5400/28, 058 01 Poprad", "https://mamutpoprad.sk/denne-menu/", "+421 919 300 300", 4.4, new() { "Moderná", "Pub", "Obedy" }),
        new(7, "Pho King", "Poprad", "https://www.phoking.sk/", "+421 949 698 790", 4.6, new() { "Vietnamská", "Ázijská", "Pho", "Bistro" }),
        new(8, "SAVOURY Asian Restaurant & Sushi Bar", "Námestie svätého Egídia 44/85, 058 01 Poprad", "https://wolt.com/sk/svk/poprad/restaurant/savoury-asian-restaurant-sushi-bar", "+421 949 487 358", 4.5, new() { "Ázijská", "Sushi", "Japonská", "Thajská" }),
        new(9, "VEG", "Nám. sv. Egídia 42/97, 058 01 Poprad", "https://www.zavolatobsluhu.sk/m/svv64o.pos", "0948 79 63 63", 4.7, new() { "Vegetariánska", "Vegánska", "Indická" })
    };

    // ===== AI SCRAPER (Gemini Flash) =====

    static async Task<List<DailyMenu>?> ScrapeWithAI(Restaurant restaurant)
    {
        if (LlmProvider == null) return null;

        // Fetch the page content
        string pageText;
        try
        {
            var html = await Http.GetStringAsync(restaurant.WebsiteUrl);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            // Extract visible text, strip scripts/styles
            foreach (var script in doc.DocumentNode.SelectNodes("//script|//style|//noscript|//svg") ?? Enumerable.Empty<HtmlNode>())
                script.Remove();
            pageText = HtmlEntity.DeEntitize(doc.DocumentNode.InnerText);
            // Clean up whitespace
            pageText = Regex.Replace(pageText, @"[ \t]+", " ");
            pageText = Regex.Replace(pageText, @"\n\s*\n+", "\n");
            pageText = pageText.Trim();
            // Limit to 8000 chars to stay within token limits
            if (pageText.Length > 8000)
                pageText = pageText[..8000];
        }
        catch
        {
            return null;
        }

        var today = DateTime.Today;
        var monday = today.AddDays(-(int)today.DayOfWeek + 1);
        var friday = monday.AddDays(4);

        var prompt = $@"Extract the weekly lunch menu from this restaurant page text. 
Restaurant: {restaurant.Name}
Week: {monday:dd.MM.yyyy} - {friday:dd.MM.yyyy}

Return ONLY valid JSON (no markdown, no explanation) in this exact format:
{{
  ""days"": [
    {{
      ""date"": ""YYYY-MM-DD"",
      ""soup"": ""soup name or null"",
      ""items"": [
        {{""name"": ""dish name"", ""price"": 7.90, ""description"": ""allergens or extra info or null"", ""isVegetarian"": false}}
      ]
    }}
  ]
}}

Rules:
- Include all days Monday to Friday that have menu data
- Remove numbering prefixes like ""1."", ""2."" from dish names
- Price should be a decimal number (e.g. 7.90), use 0 if not listed
- If the page has no daily menu data (e.g. it's a permanent menu or just info), return {{""days"": []}}
- If a specific day says the restaurant is closed or not serving menu (e.g. ""nepodávame"", ""zatvorené""), include that day with empty items array and set soup to the notice text
- Keep dish names in original Slovak language
- For soup, extract just the name without price/allergens
- description field: include allergens like ""(1,3,7)"" or weight like ""150g"" if available, null otherwise

PAGE TEXT:
{pageText}";

        try
        {
            string responseJson;

            if (LlmProvider == "mistral")
            {
                var requestBody = new
                {
                    model = "mistral-small-latest",
                    messages = new[] { new { role = "user", content = prompt } },
                    temperature = 0.1,
                    max_tokens = 4096
                };

                var request = new HttpRequestMessage(HttpMethod.Post, "https://api.mistral.ai/v1/chat/completions");
                request.Headers.Add("Authorization", $"Bearer {MistralApiKey}");
                request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

                var response = await Http.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    Console.Write($"[AI {response.StatusCode}] ");
                    return null;
                }
                responseJson = await response.Content.ReadAsStringAsync();

                using var respDoc = JsonDocument.Parse(responseJson);
                var textResult = respDoc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

                if (string.IsNullOrEmpty(textResult)) return null;
                return ParseAIMenuResponse(textResult, restaurant.Id);
            }
            else // gemini
            {
                var requestBody = new
                {
                    contents = new[] { new { parts = new[] { new { text = prompt } } } },
                    generationConfig = new { temperature = 0.1, maxOutputTokens = 4096 }
                };

                var response = await Http.PostAsync(
                    $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent?key={GeminiApiKey}",
                    new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
                );

                if (!response.IsSuccessStatusCode)
                {
                    Console.Write($"[AI {response.StatusCode}] ");
                    return null;
                }
                responseJson = await response.Content.ReadAsStringAsync();

                using var respDoc = JsonDocument.Parse(responseJson);
                var textResult = respDoc.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString();

                if (string.IsNullOrEmpty(textResult)) return null;
                return ParseAIMenuResponse(textResult, restaurant.Id);
            }
        }
        catch (Exception ex)
        {
            Console.Write($"[AI error: {ex.Message}] ");
            return null;
        }
    }

    static List<DailyMenu>? ParseAIMenuResponse(string textResult, int restaurantId)
    {
        // Strip markdown code fences if present
        textResult = Regex.Replace(textResult, @"^```(?:json)?\s*\n?", "", RegexOptions.Multiline);
        textResult = Regex.Replace(textResult, @"\n?```\s*$", "", RegexOptions.Multiline);
        textResult = textResult.Trim();

        using var menuDoc = JsonDocument.Parse(textResult);
        var days = menuDoc.RootElement.GetProperty("days");

        var menus = new List<DailyMenu>();
        foreach (var day in days.EnumerateArray())
        {
            var date = day.GetProperty("date").GetString();
            if (string.IsNullOrEmpty(date)) continue;

            string? soup = null;
            if (day.TryGetProperty("soup", out var soupEl) && soupEl.ValueKind == JsonValueKind.String)
                soup = soupEl.GetString();

            var items = new List<MenuItem>();
            foreach (var item in day.GetProperty("items").EnumerateArray())
            {
                var name = item.GetProperty("name").GetString() ?? "";
                decimal price = 0;
                if (item.TryGetProperty("price", out var priceEl))
                {
                    if (priceEl.ValueKind == JsonValueKind.Number)
                        price = priceEl.GetDecimal();
                }
                string? desc = null;
                if (item.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
                    desc = descEl.GetString();
                bool isVeg = false;
                if (item.TryGetProperty("isVegetarian", out var vegEl) && vegEl.ValueKind == JsonValueKind.True)
                    isVeg = true;

                if (!string.IsNullOrWhiteSpace(name))
                    items.Add(new MenuItem(name, price, desc, isVeg));
            }

            if (items.Count > 0 || !string.IsNullOrEmpty(soup))
                menus.Add(new DailyMenu(restaurantId, date, soup, items, DateTime.Now.ToString("HH:mm")));
        }

        if (menus.Count > 0)
            Console.Write($"[AI: {menus.Count} days] ");

        return menus.Count > 0 ? menus : null;
    }

    // ===== SCRAPERS =====

    static async Task<DailyMenu?> ScrapePlzenka(int restaurantId, string url, DateTime? forDate = null)
    {
        var html = await Http.GetStringAsync(url);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var targetDate = forDate ?? DateTime.Today;
        var targetStr = targetDate.ToString("dd.MM.yyyy");

        // Find h5.food-section containing target date
        var h5Nodes = doc.DocumentNode.SelectNodes("//h5[@class='food-section']");
        if (h5Nodes == null) return null;

        HtmlNode? todayHeader = null;
        foreach (var h5 in h5Nodes)
        {
            if (h5.InnerText.Contains(targetStr))
            { todayHeader = h5; break; }
        }
        if (todayHeader == null) return null;

        // Collect all sibling elements until next day header (h5 containing a date pattern)
        var items = new List<MenuItem>();
        string? soup = null;
        var current = todayHeader.NextSibling;
        var datePattern = new Regex(@"\d{2}\.\d{2}\.\d{4}");

        while (current != null)
        {
            if (current.Name == "h5" && datePattern.IsMatch(current.InnerText))
                break;

            if (current.Name == "div" && current.GetAttributeValue("class", "").Contains("list-item"))
            {
                var portionNode = current.SelectSingleNode(".//h3/div[1]");
                var nameNode = current.SelectSingleNode(".//h3/div[2]/div");
                var priceNode = current.SelectSingleNode(".//span[@class='menu-price']");

                var portion = portionNode != null ? HtmlEntity.DeEntitize(portionNode.InnerText).Trim() : "";
                var nameRaw = nameNode != null ? HtmlEntity.DeEntitize(nameNode.InnerText).Trim() : "";
                nameRaw = Regex.Replace(nameRaw, @"\s+", " ").Trim();

                if (portion.Contains("0,33 l") && soup == null)
                {
                    var soupName = Regex.Replace(nameRaw, @"\(\s*[\d\s,]+\s*\)", "").Trim();
                    soup = soupName;
                }
                else if (priceNode != null && !string.IsNullOrWhiteSpace(nameRaw))
                {
                    var priceText = HtmlEntity.DeEntitize(priceNode.InnerText).Trim();
                    var priceMatch = Regex.Match(priceText, @"(\d+[.,]\d+)\s*EUR");
                    decimal price = 0;
                    if (priceMatch.Success)
                    {
                        var priceStr = priceMatch.Groups[1].Value.Replace(",", ".");
                        decimal.TryParse(priceStr, NumberStyles.Any, CultureInfo.InvariantCulture, out price);
                    }

                    var itemName = Regex.Replace(nameRaw, @"^\d+\.\s*", "");
                    var allergenMatch = Regex.Match(itemName, @"\(\s*([\d\s,]+)\s*\)\s*$");
                    string? desc = null;
                    if (allergenMatch.Success)
                    {
                        desc = "(" + allergenMatch.Groups[1].Value.Replace(" ", "").Trim() + ")";
                        itemName = itemName[..allergenMatch.Index].Trim();
                    }

                    if (!string.IsNullOrWhiteSpace(itemName))
                        items.Add(new MenuItem(itemName, price, desc, false));
                }
            }

            current = current.NextSibling;
        }

        // Detect "no menu today" notices (e.g. "nepodávame", "neponúkame")
        if (items.Count == 0 && soup != null &&
            (soup.Contains("nepodávame", StringComparison.OrdinalIgnoreCase) ||
             soup.Contains("neponúkame", StringComparison.OrdinalIgnoreCase) ||
             soup.Contains("zatvorené", StringComparison.OrdinalIgnoreCase) ||
             soup.Contains("technických príčin", StringComparison.OrdinalIgnoreCase)))
        {
            return new DailyMenu(restaurantId, DateOnly.FromDateTime(targetDate).ToString("yyyy-MM-dd"), soup, new List<MenuItem>(), DateTime.Now.ToString("HH:mm"));
        }

        if (items.Count == 0) return null;
        return new DailyMenu(restaurantId, DateOnly.FromDateTime(targetDate).ToString("yyyy-MM-dd"), soup, items, DateTime.Now.ToString("HH:mm"));
    }

    static async Task<DailyMenu?> ScrapeVeg(int restaurantId, string url, DateTime? forDate = null)
    {
        var html = await Http.GetStringAsync(url);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var targetDate = forDate ?? DateTime.Today;

        // Each day is in div.pos-detail-daymnu containing div.daily-menu-pnl
        var dayNodes = doc.DocumentNode.SelectNodes("//div[contains(@class,'pos-detail-daymnu')]");
        if (dayNodes == null) return null;

        // Day titles are like "Monday 29.6." or "Thursday 2.7. - Today"
        // We need to match by day.month format
        var targetDayMonth = $"{targetDate.Day}.{targetDate.Month}.";

        HtmlNode? targetDay = null;
        foreach (var dayNode in dayNodes)
        {
            var titleNode = dayNode.SelectSingleNode(".//div[contains(@class,'daily-menu-title')]/span");
            if (titleNode == null) continue;
            var titleText = HtmlEntity.DeEntitize(titleNode.InnerText).Trim();
            if (titleText.Contains(targetDayMonth))
            { targetDay = dayNode; break; }
        }
        if (targetDay == null) return null;

        // Extract soup from daily-menu-beforetxt
        string? soup = null;
        var soupNode = targetDay.SelectSingleNode(".//div[contains(@class,'daily-menu-beforetxt')]");
        if (soupNode != null)
        {
            var beforeText = HtmlEntity.DeEntitize(soupNode.InnerText).Trim();
            // Format: "Soup: 1. Name 0,3l allergens | price € | included..."
            var soupMatch = Regex.Match(beforeText, @"1\.\s*(.+?)(?:\s+\d+[.,]\d+\s*l|\s*\|)");
            if (soupMatch.Success)
                soup = soupMatch.Groups[1].Value.Trim();
        }

        // Extract items from h4.sm-det-name
        var items = new List<MenuItem>();
        var itemNodes = targetDay.SelectNodes(".//div[contains(@class,'daily-menu-items')]//div[contains(@class,'sm-detail-record')]");
        if (itemNodes == null) return null;

        foreach (var itemNode in itemNodes)
        {
            var nameNode = itemNode.SelectSingleNode(".//span[contains(@class,'sm-det-name-in')]");
            if (nameNode == null) continue;
            var name = HtmlEntity.DeEntitize(nameNode.InnerText).Trim();
            name = Regex.Replace(name, @"\s+", " ");

            // Price: span.price contains price as text like "7,90 €"
            var priceSpan = itemNode.SelectSingleNode(".//p[contains(@class,'sm-det-props')]//span[contains(@class,'price')]");
            decimal price = 0;
            if (priceSpan != null)
            {
                var priceText = HtmlEntity.DeEntitize(priceSpan.InnerText).Trim();
                var priceMatch = Regex.Match(priceText, @"(\d+)[.,](\d+)\s*€");
                if (priceMatch.Success)
                {
                    var priceStr = $"{priceMatch.Groups[1].Value}.{priceMatch.Groups[2].Value}";
                    decimal.TryParse(priceStr, NumberStyles.Any, CultureInfo.InvariantCulture, out price);
                }
            }

            // Allergens from propval
            var propNodes = itemNode.SelectNodes(".//p[contains(@class,'sm-det-props')]/span[contains(@class,'propval')]");
            string? desc = null;
            if (propNodes != null)
            {
                foreach (var prop in propNodes)
                {
                    var propText = HtmlEntity.DeEntitize(prop.InnerText).Trim();
                    // Allergens are like "1,6,7,10"
                    if (Regex.IsMatch(propText, @"^[\d,]+$"))
                        desc = $"({propText})";
                }
            }

            if (!string.IsNullOrWhiteSpace(name))
                items.Add(new MenuItem(name, price, desc, false));
        }

        if (items.Count == 0) return null;
        return new DailyMenu(restaurantId, DateOnly.FromDateTime(targetDate).ToString("yyyy-MM-dd"), soup, items, DateTime.Now.ToString("HH:mm"));
    }

    static async Task<DailyMenu?> ScrapeForumPoprad(int restaurantId, string url)
    {
        var html = await Http.GetStringAsync(url);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var today = DateTime.Today;
        var contentNode = doc.DocumentNode.SelectSingleNode("//section[@id='offerDetail']//div[@class='text']");
        if (contentNode == null) return null;

        var dayNames = new Dictionary<DayOfWeek, string>
        {
            { DayOfWeek.Monday, "Pondelok" }, { DayOfWeek.Tuesday, "Utorok" },
            { DayOfWeek.Wednesday, "Streda" }, { DayOfWeek.Thursday, "Štvrtok" },
            { DayOfWeek.Friday, "Piatok" }
        };

        if (!dayNames.TryGetValue(today.DayOfWeek, out var todayName)) return null;

        var items = new List<MenuItem>();
        string? soup = null;
        var allNodes = contentNode.ChildNodes.ToList();
        bool inTodaySection = false;
        bool foundSoup = false;

        for (int i = 0; i < allNodes.Count; i++)
        {
            var node = allNodes[i];
            var text = HtmlEntity.DeEntitize(node.InnerText).Trim();

            if (text.Contains(todayName) && node.InnerHtml.Contains("<strong>"))
            { inTodaySection = true; continue; }

            if (inTodaySection && node.InnerHtml != null && node.InnerHtml.Contains("<strong>")
                && dayNames.Values.Any(d => text.Contains(d)) && !text.Contains(todayName))
                break;

            if (!inTodaySection) continue;

            if (!foundSoup && text.StartsWith("Polievka:"))
            { soup = text.Replace("Polievka:", "").Trim(); foundSoup = true; }

            if (node.Name == "ol")
            {
                var lis = node.SelectNodes(".//li");
                if (lis == null) continue;
                foreach (var li in lis)
                {
                    var itemText = HtmlEntity.DeEntitize(li.InnerText).Trim();
                    var priceMatch = Regex.Match(itemText, @"\((\d+[.,]\d+)€\)\s*$");
                    decimal price = 8.90m;
                    if (priceMatch.Success)
                    {
                        decimal.TryParse(priceMatch.Groups[1].Value.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out price);
                        itemText = itemText[..itemText.LastIndexOf('(')].Trim();
                    }
                    items.Add(new MenuItem(itemText, price, null, false));
                }
            }
        }

        if (items.Count == 0) return null;
        return new DailyMenu(restaurantId, DateOnly.FromDateTime(today).ToString("yyyy-MM-dd"), soup, items, DateTime.Now.ToString("HH:mm"));
    }

    static async Task<DailyMenu?> ScrapeMenuckaSk(int restaurantId, string url)
    {
        var html = await Http.GetStringAsync(url);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var today = DateTime.Today;
        var todayStr = today.ToString("dd.MM.yyyy");
        var dayNames = new Dictionary<DayOfWeek, string>
        {
            { DayOfWeek.Monday, "Pondelok" }, { DayOfWeek.Tuesday, "Utorok" },
            { DayOfWeek.Wednesday, "Streda" }, { DayOfWeek.Thursday, "Štvrtok" },
            { DayOfWeek.Friday, "Piatok" }
        };

        var dayTitles = doc.DocumentNode.SelectNodes("//div[@class='day-title']");
        if (dayTitles == null) return null;

        HtmlNode? todayTitle = null;
        foreach (var dt in dayTitles)
        {
            var text = HtmlEntity.DeEntitize(dt.InnerText).Trim();
            if (text.Contains(todayStr))
            { todayTitle = dt; break; }
            if (dayNames.TryGetValue(today.DayOfWeek, out var dayName) && text.Contains(dayName))
            { todayTitle = dt; break; }
        }
        if (todayTitle == null) return null;

        var items = new List<MenuItem>();
        string? soup = null;
        var container = todayTitle.ParentNode;
        var current = container?.NextSibling;
        bool firstItem = true;

        while (current != null)
        {
            if (current.SelectSingleNode(".//div[@class='day-title']") != null) break;
            var classes = current.GetAttributeValue("class", "");
            var text = HtmlEntity.DeEntitize(current.InnerText).Trim();

            if (classes.Contains("col-xs-10") && !string.IsNullOrWhiteSpace(text))
            {
                var priceNode = current.NextSibling;
                while (priceNode != null && priceNode.NodeType != HtmlNodeType.Element)
                    priceNode = priceNode.NextSibling;

                decimal price = 0;
                if (priceNode != null)
                {
                    var priceText = HtmlEntity.DeEntitize(priceNode.InnerText).Trim()
                        .Replace("€", "").Replace("&nbsp;", "").Replace("\u00a0", "").Trim().Replace(",", ".");
                    decimal.TryParse(priceText, NumberStyles.Any, CultureInfo.InvariantCulture, out price);
                }

                if (firstItem && price <= 1.50m && price > 0)
                { soup = text; firstItem = false; }
                else if (price > 0)
                { items.Add(new MenuItem(text, price, null, false)); firstItem = false; }
            }
            current = current.NextSibling;
        }

        if (items.Count == 0) return null;
        return new DailyMenu(restaurantId, DateOnly.FromDateTime(today).ToString("yyyy-MM-dd"), soup, items, DateTime.Now.ToString("HH:mm"));
    }

    static async Task<DailyMenu?> ScrapeAngryChef(int restaurantId, string url)
    {
        var html = await Http.GetStringAsync(url);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var items = new List<MenuItem>();
        var h4Nodes = doc.DocumentNode.SelectNodes("//h4");
        if (h4Nodes == null) return null;

        foreach (var h4 in h4Nodes)
        {
            var name = h4.InnerText.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var parent = h4.ParentNode;
            var parentText = parent?.InnerText ?? "";
            var priceMatch = Regex.Match(parentText, @"(\d+[,\.]\d{2})\s*€", RegexOptions.RightToLeft);
            decimal price = 0;
            if (priceMatch.Success)
                decimal.TryParse(priceMatch.Groups[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out price);

            if (price > 0)
            {
                var descNode = h4.NextSibling;
                string? description = null;
                while (descNode != null)
                {
                    var text = descNode.InnerText.Trim();
                    if (!string.IsNullOrWhiteSpace(text) && !Regex.IsMatch(text, @"^\d+[,\.]\d{2}\s*€"))
                    { description = text.Split('\n').FirstOrDefault()?.Trim(); break; }
                    descNode = descNode.NextSibling;
                }

                items.Add(new MenuItem(name, price, description,
                    name.Contains("tofu", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("VEGAN", StringComparison.OrdinalIgnoreCase)));
            }
        }

        if (items.Count == 0) return null;
        return new DailyMenu(restaurantId, DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd"), null, items, DateTime.Now.ToString("HH:mm"));
    }

    // ===== STATIC DATA =====

    static DailyMenu? GetStaticMenu(int restaurantId, DateTime date)
    {
        var dateStr = DateOnly.FromDateTime(date).ToString("yyyy-MM-dd");
        var day = date.DayOfWeek;
        var now = DateTime.Now.ToString("HH:mm");

        if (day == DayOfWeek.Saturday || day == DayOfWeek.Sunday) return null;

        return restaurantId switch
        {
            1 => GetPlzenkaMenu(dateStr, day, now),
            2 => GetAquacityMenu(dateStr, day, now),
            3 => GetForumMenu(dateStr, day, now),
            4 => GetBarnMenu(dateStr, day, now),
            5 => GetAngryChefMenu(dateStr, day, now),
            6 => GetMamutMenu(dateStr, day, now),
            7 => GetPhoKingMenu(dateStr, day, now),
            8 => GetSavouryMenu(dateStr, day, now),
            _ => null
        };
    }

    static DailyMenu GetPlzenkaMenu(string date, DayOfWeek day, string time) => day switch
    {
        DayOfWeek.Monday => new(1, date, "Jemná krémová polievka s fazuľou a mrkvou (1, 7)", new()
        {
            new("Bravčová vypražaná fašírka, zemiakové pyré so smotanou, šalát z kyslej kapusty", 8.10m, "150g (1, 3, 7)", false),
            new("Hydinové srbské soté, ryža, hranolky", 8.10m, "150g", false),
            new("Špagety Carbonara s parmezánom", 7.80m, "300g (1, 3, 7)", false),
            new("Miešaný listový šalát s kuracími nugetkami, cocktailový dresing, bylinková bageta", 9.10m, "300g (1, 3, 7)", false),
            new("Vyprážaný bravčový rezeň, zemiakový šalát s majonézou", 8.10m, "150g (1, 3, 7, 10)", false)
        }, time),
        DayOfWeek.Tuesday => new(1, date, "Falošná gulášová s klobásou (1, 9)", new()
        {
            new("Bravčový steak s bylinkovo-cesnakovou omáčkou, gratinované zemiaky", 8.10m, "150g (3, 7)", false),
            new("Morčacie prsia na grilovanej zelenine, ryža", 9.10m, "150g", false),
            new("Hrachový prívarok s volským okom, pečenou špekáčkou, chlieb", 7.80m, "300g (1, 3)", false),
            new("Miešaný listový šalát s restovanými šampiňónmi a slaninou, vinaigrette, bylinková bageta", 9.10m, "300g (1, 3, 7)", false),
            new("Vyprážaný bravčový rezeň, zemiakový šalát s majonézou", 8.10m, "150g (1, 3, 7, 10)", false)
        }, time),
        DayOfWeek.Wednesday => new(1, date, "Cesnaková číra s vajíčkom a opečeným chlebom (1, 3)", new()
        {
            new("Bravčové stehno na znojemský spôsob, slovenská ryža", 8.10m, "150g (1, 3, 7, 10)", false),
            new("Kuracie medajlónky na hríbovom ragú, ryža, opekané zemiaky", 8.10m, "150g (7)", false),
            new("Lasagne so zeleninou, paradajková omáčka s bazalkou", 7.80m, "300g (1, 3, 7)", true),
            new("Miešaný listový šalát s kuracími nugetkami, cocktailový dresing, bylinková bageta", 9.10m, "300g (1, 3, 7)", false),
            new("Vyprážaný bravčový rezeň, zemiakový šalát s majonézou", 8.10m, "150g (1, 3, 7, 10)", false)
        }, time),
        DayOfWeek.Thursday => new(1, date, "Šošovícová s kyslou kapustou (1, 7)", new()
        {
            new("Guľky z mletého mäsa so smotanovo-horčicovou omáčkou, tlačené zemiaky, šalát", 8.10m, "150g (1, 3, 7, 10)", false),
            new("Kurací závitok so šunkou, eidamom a kápiou, dusená ryža", 8.10m, "150g (7)", false),
            new("Zeleninový cous-cous s opečeným údeným tofu a bazalkovým pestom", 7.80m, "300g (1, 7)", true),
            new("Miešaný listový šalát s restovanými šampiňónmi a slaninou, vinaigrette, bylinková bageta", 9.10m, "300g (1, 3, 7)", false),
            new("Vyprážaný bravčový rezeň, zemiakový šalát s majonézou", 8.10m, "150g (1, 3, 7, 10)", false)
        }, time),
        _ => new(1, date, "Hubová polievka so zemiakmi (1)", new()
        {
            new("Bratislavské pliecko na smotane, cestovina", 8.10m, "150g (1, 3, 7)", false),
            new("Vyprážané vykostené kuracie stehno, zemiaková kaša, tatranský šalát", 8.10m, "150g (1, 3, 7)", false),
            new("Tvarohové pirôžky s kakaom a rozpusteným maslom", 7.80m, "300g (1, 3, 7)", true),
            new("Miešaný listový šalát s kuracími nugetkami, cocktailový dresing, bylinková bageta", 9.10m, "300g (1, 3, 7)", false),
            new("Vyprážaný bravčový rezeň, zemiakový šalát s majonézou", 8.10m, "150g (1, 3, 7, 10)", false)
        }, time)
    };

    static DailyMenu GetAquacityMenu(string date, DayOfWeek day, string time) => day switch
    {
        DayOfWeek.Monday => new(2, date, "Slepačí vývar s rezancami (1, 3, 7, 9)", new()
        {
            new("Bravčový rezeň v trojobale, zemiaková kaša, šalát", 8.90m, "150/200/50g (1, 3, 7)", false),
            new("Grilovaný losos na špenátovom lôžku s ryžou", 10.90m, "150/150/50g (4, 7)", false),
            new("Zeleninové rizoto s parmezánom", 7.90m, "350g (7)", true)
        }, time),
        DayOfWeek.Tuesday => new(2, date, "Krémová paradajková polievka s bazalkou (7)", new()
        {
            new("Kuracie stehno na paprike, halušky", 8.90m, "200/200g (1, 3, 7)", false),
            new("Hovädzie ragú s gnocchi", 10.90m, "250/150g (1, 3, 7)", false),
            new("Šošovicový dhal s naan chlebom", 7.90m, "300/80g (1)", true)
        }, time),
        DayOfWeek.Wednesday => new(2, date, "Hubová polievka s kôprom (7)", new()
        {
            new("Pečená kačica, lokše, červená kapusta", 10.90m, "200/150/80g (1, 7)", false),
            new("Morčacie prsia na grile, grilovaná zelenina, bylinkové maslo", 9.90m, "180/150/20g (7)", false),
            new("Caprese šalát s mozzarellou a pestom", 7.90m, "250g (7)", true)
        }, time),
        DayOfWeek.Thursday => new(2, date, "Gulášová polievka (1, 7)", new()
        {
            new("Vyprážaný syr, hranolky, tatárska omáčka", 8.90m, "150/200/30g (1, 3, 7)", true),
            new("Grilovaný bravčový steak, pečené zemiaky, coleslaw", 9.90m, "180/200/50g (7, 10)", false),
            new("Ázijská miska s tofu a ryžovými rezancami", 7.90m, "350g (6, 11)", true)
        }, time),
        _ => new(2, date, "Rybacia polievka (4, 7, 9)", new()
        {
            new("Pstruh na masle s mandľami, varené zemiaky", 10.90m, "200/200g (4, 7, 8)", false),
            new("Hovädzí burger, hranolky, BBQ omáčka", 9.90m, "200/150/30g (1, 3, 7, 10)", false),
            new("Špagety aglio olio s cherry paradajkami", 7.90m, "350g (1)", true)
        }, time)
    };

    static DailyMenu GetForumMenu(string date, DayOfWeek day, string time) => day switch
    {
        DayOfWeek.Monday => new(3, date, "Zemiaková na kyslo s vajíčkom a kôprom / Kurací vývar s koreňovou zeleninou", new()
        {
            new("Kurací steak s rukolou a grilovanou repou, horčicový dip, ryža", 8.90m, null, false),
            new("Zapekané zemiaky s kukuricou, brokolicou a so smotanou", 8.90m, null, true),
            new("Pečené bravčové karé, hráškovo-zemiakové pyré", 9.90m, null, false),
            new("Šalát s grilovaným hermelínom", 9.90m, null, true),
            new("Vyprážaný pastiersky syr, hranolky, tatárska omáčka", 10.90m, null, true),
            new("Classic burger s hranolkami a tatárskou omáčkou", 9.90m, null, false),
            new("Medailónky z bravčovej panenky na hubovej omáčke, opekané zemiaky", 14.90m, null, false),
            new("Rib eye steak, bylinkové maslo, zelená fazuľa, grilovaná kukurica, opekané zemiaky", 18.90m, null, false)
        }, time),
        DayOfWeek.Tuesday => new(3, date, "Krúpová s údeným mäsom / Kurací vývar s koreňovou zeleninou", new()
        {
            new("Kurací steak na listovom šaláte, karamelizovaná hruška, granátové jadierka", 8.90m, null, false),
            new("Tagliatelle s kuracím mäsom a pestom, cherry paradajky, parmezán", 8.90m, null, false),
            new("Bravčový stroganov, 1/2 ryža, 1/2 hranolky", 9.90m, null, false),
            new("Šalát s grilovaným hermelínom", 9.90m, null, true),
            new("Vyprážaný pastiersky syr, hranolky, tatárska omáčka", 10.90m, null, true),
            new("Classic burger s hranolkami a tatárskou omáčkou", 9.90m, null, false),
            new("Medailónky z bravčovej panenky na hubovej omáčke, opekané zemiaky", 14.90m, null, false),
            new("Rib eye steak, bylinkové maslo, zelená fazuľa, grilovaná kukurica, opekané zemiaky", 18.90m, null, false)
        }, time),
        DayOfWeek.Wednesday => new(3, date, "Paradajková s parmezánom / Kurací vývar s koreňovou zeleninou", new()
        {
            new("Kurací steak na zelenej fazuli, dusená ryža", 8.90m, null, false),
            new("Makové šúľance s maslom a jahodový dip", 8.90m, null, true),
            new("Živánska pochúťka", 9.90m, null, false),
            new("Šalát s grilovaným hermelínom", 9.90m, null, true),
            new("Vyprážaný pastiersky syr, hranolky, tatárska omáčka", 10.90m, null, true),
            new("Classic burger s hranolkami a tatárskou omáčkou", 9.90m, null, false),
            new("Medailónky z bravčovej panenky na hubovej omáčke, opekané zemiaky", 14.90m, null, false),
            new("Rib eye steak, bylinkové maslo, zelená fazuľa, grilovaná kukurica, opekané zemiaky", 18.90m, null, false)
        }, time),
        DayOfWeek.Thursday => new(3, date, "Hrášková krémová s krutónmi / Kurací vývar s koreňovou zeleninou", new()
        {
            new("Kačací šalát s brusnicovým dressingom", 8.90m, null, false),
            new("Šafránové rizoto s kuracím mäsom, parmezán", 8.90m, null, false),
            new("Bravčové dusené s kyslou kapustou, zemiaková placka", 9.90m, null, false),
            new("Šalát s grilovaným hermelínom", 9.90m, null, true),
            new("Vyprážaný pastiersky syr, hranolky, tatárska omáčka", 10.90m, null, true),
            new("Classic burger s hranolkami a tatárskou omáčkou", 9.90m, null, false),
            new("Medailónky z bravčovej panenky na hubovej omáčke, opekané zemiaky", 14.90m, null, false),
            new("Rib eye steak, bylinkové maslo, zelená fazuľa, grilovaná kukurica, opekané zemiaky", 18.90m, null, false)
        }, time),
        _ => new(3, date, "Číra cesnaková s vajíčkom a syrom / Kurací vývar s koreňovou zeleninou", new()
        {
            new("Kurací steak s opekanými zemiakmi, špargľa, syr", 8.90m, null, false),
            new("Krémové rizoto s kuracím mäsom, sušená paradajka, parmezán", 8.90m, null, false),
            new("Sviečková na smotane, domáca parená knedľa", 9.90m, null, false),
            new("Šalát s grilovaným hermelínom", 9.90m, null, true),
            new("Vyprážaný pastiersky syr, hranolky, tatárska omáčka", 10.90m, null, true),
            new("Classic burger s hranolkami a tatárskou omáčkou", 9.90m, null, false),
            new("Medailónky z bravčovej panenky na hubovej omáčke, opekané zemiaky", 14.90m, null, false),
            new("Rib eye steak, bylinkové maslo, zelená fazuľa, grilovaná kukurica, opekané zemiaky", 18.90m, null, false)
        }, time)
    };

    static DailyMenu GetBarnMenu(string date, DayOfWeek day, string time) => day switch
    {
        DayOfWeek.Monday => new(4, date, "Zemiakovo-cesnaková (1)", new()
        {
            new("Kuracie stripsy v panko strúhanke s hranolkami a dom. tatár. omáčkou", 6.90m, "130g (1,3,7,10)", false),
            new("Francúzske zemiaky s klobásou, vajíčkami a kyslou uhorkou", 6.90m, "400g (1,3,7,10)", false),
            new("Miešaný šalát s kuracím mäsom a cesnakovým dressingom, toust", 6.90m, "300g (1,10)", false),
            new("Vyprážaný Encián s hranolkami a domácou tatárskou omáčkou", 6.90m, "110g (1,3,7,10)", true),
            new("Vyprážaný bravčový rezeň so zemiakmi a kyslou uhorkou", 7.50m, "150g (1,3,7,10)", false)
        }, time),
        DayOfWeek.Tuesday => new(4, date, "Špenátová mliečna s vajíčkom (1,3,7)", new()
        {
            new("Pečené kuracie stehno s dusenou ryžou a prírodnou šťavou, uhorkový šalát", 6.90m, "240g (1,3,7)", false),
            new("Bratislavské špikované bravčové stehno s kolienkami", 6.90m, "130g (1,3,7,10)", false),
            new("Miešaný šalát s kuracím mäsom a cesnakovým dressingom, toust", 6.90m, "300g (1,10)", false),
            new("Vyprážaný Encián s hranolkami a domácou tatárskou omáčkou", 6.90m, "110g (1,3,7,10)", true),
            new("Vyprážaný bravčový rezeň so zemiakmi a kyslou uhorkou", 7.50m, "150g (1,3,7,10)", false)
        }, time),
        DayOfWeek.Wednesday => new(4, date, "Zeleninová (9)", new()
        {
            new("1/4 Pečená kačka s dusenou červenou kapustou a par. knedľou", 8.60m, "(1,3,7)", false),
            new("Kurací plátok na prírodno s tarhoňou, kompót", 6.90m, "130g (1)", false),
            new("Miešaný šalát s kuracím mäsom a cesnakovým dressingom, toust", 6.90m, "300g (1,10)", false),
            new("Vyprážaný Encián s hranolkami a domácou tatárskou omáčkou", 6.90m, "110g (1,3,7,10)", true),
            new("Vyprážaný bravčový rezeň so zemiakmi a kyslou uhorkou", 7.50m, "150g (1,3,7,10)", false)
        }, time),
        DayOfWeek.Thursday => new(4, date, "Kačací vývar s rezancami (1,3,9)", new()
        {
            new("Bravčová panenka so smotanovo-cheddarovou omáčkou a pečenými zemiakmi", 7.90m, "130g", false),
            new("Kuracie kúsky na karí s ananásom a ryžou", 6.90m, "130g (3,7)", false),
            new("Miešaný šalát s kuracím mäsom a cesnakovým dressingom, toust", 6.90m, "300g (1,10)", false),
            new("Vyprážaný Encián s hranolkami a domácou tatárskou omáčkou", 6.90m, "110g (1,3,7,10)", true),
            new("Vyprážaný bravčový rezeň so zemiakmi a kyslou uhorkou", 7.50m, "150g (1,3,7,10)", false)
        }, time),
        _ => new(4, date, "Tradičná cibuľačka s hriankou a syrom (1,3,7)", new()
        {
            new("Pečený bôčik s kyslou kapustou a parenou knedľou", 6.90m, "130g (1,3,7,10)", false),
            new("Tagliatelle s Nivovo-smotanovou omáčkou", 6.90m, "350g (1,3,7)", false),
            new("Miešaný šalát s kuracím mäsom a cesnakovým dressingom, toust", 6.90m, "300g (1,10)", false),
            new("Vyprážaný Encián s hranolkami a domácou tatárskou omáčkou", 6.90m, "110g (1,3,7,10)", true),
            new("Vyprážaný bravčový rezeň so zemiakmi a kyslou uhorkou", 7.50m, "150g (1,3,7,10)", false)
        }, time)
    };

    static DailyMenu GetAngryChefMenu(string date, DayOfWeek day, string time) => new(5, date,
        "Tom Yum s morčacím mäsom (250ml - 4,00€ / 400ml - 7,00€)", new()
        {
            new("Bao s trhaným bravčovým", 6.00m, "Kimchi majonéza, domáca čalamáda, koriander (1,6)", false),
            new("Bao s trhaným hovädzím", 7.00m, "Mangový dressing, nakladaná redkvička, koriander (1,6)", false),
            new("Bao s krevetami", 7.00m, "Spicy mayo, nakladaná redkvička, koriander (1,6)", false),
            new("Bao s chrumkavým bôčikom", 6.00m, "Arašidová satay omáčka, domáca čalamáda, koriander (1,5,6)", false),
            new("Bao s údeným tofu", 6.00m, "Hlivové teriyaki, nakladaná uhorka, sezam, koriander (1,6,11)", true),
            new("Bowl s krevetami", 13.00m, "Jasmínová ryža, spicy mayo, pakchoi, mrkva, edamame (2,3,6,11)", false),
            new("Bowl s trhaným hovädzím", 13.00m, "Jasmínová ryža, mangový dressing, nakladaná zelenina, kimchi (1,5,6)", false),
            new("Bowl s trhaným bravčovým", 11.00m, "Jasmínová ryža, kimchi majonéza, nakladaná zelenina (1,5,6)", false),
            new("Bowl s chrumkavým bôčikom", 12.00m, "Jasmínová ryža, arašidová satay omáčka, nakladaná zelenina (1,5,6)", false),
            new("Bowl s údeným tofu", 10.00m, "Jasmínová ryža, hlivové teriyaki, nakladaná zelenina (1,5,6)", true),
            new("VEGAN Šošovicový dhal", 7.50m, "Kari z červenej šošovice, paradajky, jasmínová ryža, koriander (1,11)", true),
            new("Ázijské bravčové rebrá", 14.50m, "Jasmínová ryža, BBQ omáčka, kimchi, koriander (1,6,10)", false)
        }, time);

    static DailyMenu GetMamutMenu(string date, DayOfWeek day, string time) => day switch
    {
        DayOfWeek.Monday => new(6, date, "Romanesco krém, krutóny (1,3,7) / Slepačí vývar s domácimi rezancami (1,3,9)", new()
        {
            new("Kuracie stripsy v corn flakes", 8.95m, "parmezánová kaša, údená mayo (1,3,7)", false),
            new("Bulgur s grilovanou zeleninou", 8.95m, "quinoa, halloumi, medovo limetkový vinaigrette (1)", true),
            new("Fish and chips", 10.95m, "hráškové pyré (1,3,4)", false),
            new("Panenka sous vide", 12.95m, "karfiolové pyré, špargľa, pálené zemiaky, demi glacé (7)", false)
        }, time),
        DayOfWeek.Tuesday => new(6, date, "Špenátová s vajíčkom (3) / Slepačí vývar s domácimi rezancami (1,3,9)", new()
        {
            new("Restovaná kuracia pečeň s panenkou", 8.95m, "farebná paprika, ryža s hráškom", false),
            new("Vyprážaná mozzarella", 8.95m, "batátové pyré, brusnice, mix šalát (1,3,7)", true),
            new("Fish and chips", 10.95m, "hráškové pyré (1,3,4)", false),
            new("Panenka sous vide", 12.95m, "karfiolové pyré, špargľa, pálené zemiaky, demi glacé (7)", false)
        }, time),
        DayOfWeek.Wednesday => new(6, date, "Hubová na paprike s mrvenicou a zemiakami (1) / Slepačí vývar (1,3,9)", new()
        {
            new("Bravčová roláda s prosciuttom a špenátom", 8.95m, "štuchané zemiaky, chrumkavá cibuľa, demi glacé (1)", false),
            new("Penne puttanesca", 8.95m, "(1,3,4)", false),
            new("Fish and chips", 10.95m, "hráškové pyré (1,3,4)", false),
            new("Panenka sous vide", 12.95m, "karfiolové pyré, špargľa, pálené zemiaky, demi glacé (7)", false)
        }, time),
        DayOfWeek.Thursday => new(6, date, "Frankfurtská / Slepačí vývar s domácimi rezancami (1,3,9)", new()
        {
            new("Kurací rezeň v bylinkovo parmezánovej strúhanke", 8.95m, "mrkvovo zemiakové pyré, šalát (1,3,7)", false),
            new("Gnocchi Hokkaido", 8.95m, "semiačka, tempeh, rukola (1,3,6)", true),
            new("Fish and chips", 10.95m, "hráškové pyré (1,3,4)", false),
            new("Panenka sous vide", 12.95m, "karfiolové pyré, špargľa, pálené zemiaky, demi glacé (7)", false)
        }, time),
        _ => new(6, date, "Šampiňónový krém, krutóny (1,3,7) / Slepačí vývar (1,3,9)", new()
        {
            new("Lasagne bolognese", 8.95m, "paradajková omáčka, rukola (1,3,7)", false),
            new("Domáce šišky", 8.95m, "pistáciové mascarpone, čokoláda (1,3,7,8)", false),
            new("Fish and chips", 10.95m, "hráškové pyré (1,3,4)", false),
            new("Panenka sous vide", 12.95m, "karfiolové pyré, špargľa, pálené zemiaky, demi glacé (7)", false)
        }, time)
    };

    static DailyMenu GetPhoKingMenu(string date, DayOfWeek day, string time) => new(7, date, null, new()
    {
        new("Pho Bo (hovädzí vývar)", 8.90m, "ryžové rezance, hovädzie mäso, bylinky", false),
        new("Pho Ga (slepačí vývar)", 7.90m, "ryžové rezance, kuracie mäso, bylinky", false),
        new("Bun Bo Nam Bo", 8.90m, "ryžové rezance, hovädzie mäso, arašidy, zelenina", false),
        new("Com Rang (vyprážaná ryža)", 7.90m, "s kuracím mäsom a zeleninou", false),
        new("Mi Xao (vyprážané rezance)", 7.90m, "s kuracím mäsom a zeleninou", false),
        new("Curi Ga (kuracie kari)", 8.50m, "kokosové mlieko, zelenina, ryža", false),
        new("Udon s krevetami", 9.90m, "udon rezance, krevety, zelenina, teriyaki", false),
        new("Jarné rolky (2ks)", 3.90m, "s bravčovým mäsom", false)
    }, time);

    static DailyMenu GetSavouryMenu(string date, DayOfWeek day, string time) => day switch
    {
        DayOfWeek.Monday => new(8, date, "Tom Yum (330ml) (2,4) - 6,00 €", new()
        {
            new("Ryžové rezance s kuracím mäsom", 11.50m, null, false),
            new("Ryžové rezance s hovädzím mäsom", 11.50m, null, false),
            new("Ryžové rezance s krevetami", 11.50m, null, false),
            new("Kuracie prsia tempura s rezancami a sweet chilli", 12.00m, null, false),
            new("Bun Bó Nam Bo", 12.00m, null, false),
            new("Kuracie soté sweet chilli, jasmínová ryža", 12.00m, null, false),
            new("Sushi: Alaska roll (8ks), Maki Oshinko (4ks), Maki Tamago (4ks)", 13.50m, null, false),
            new("Sushi: Vegeta roll (8ks), Maki avokádo (4ks), Maki Kappa+Oshinko (4ks)", 12.00m, null, true)
        }, time),
        DayOfWeek.Tuesday => new(8, date, "Tom kha gai (330ml) (6,7) - 6,00 €", new()
        {
            new("Pad Thai s kuracím mäsom", 12.00m, "(2,3,5,6)", false),
            new("Pad Thai s krevetami", 12.00m, "(2,3,5,6)", false),
            new("Kungpao s kuracím mäsom a ryžou", 11.50m, "(5,6)", false),
            new("Chrumkavá kačica s arašidovou omáčkou a zeleninou", 12.00m, "(5,7,11)", false),
            new("Kuracie soté so zeleninou, jasmínová ryža", 11.50m, "(11)", false),
            new("Sushi: Sake roll (8ks), Maki sake (4ks), Maki ebi (4ks)", 14.00m, null, false),
            new("Sushi: Vegeta roll (8ks), Maki avokádo (4ks), Maki Kappa (4ks)", 12.00m, null, true)
        }, time),
        DayOfWeek.Wednesday => new(8, date, "Miso polievka (330ml) (6) - 6,00 €", new()
        {
            new("Thajské kari s kuracím mäsom a ryžou", 11.50m, "(7)", false),
            new("Thajské kari s krevetami a ryžou", 12.00m, "(7)", false),
            new("Bún chả hà nôi", 12.00m, "grilované bravčové, ryžové rezance, šalát", false),
            new("Opekaná ryža s kuracím mäsom a zeleninou", 11.50m, "(1,3)", false),
            new("Udon s hovädzím mäsom a zeleninou", 12.00m, "(1,3)", false),
            new("Sushi: Maguro roll (8ks), Maki sake (4ks), Maki oshinko (4ks)", 14.50m, null, false),
            new("Sushi: Vegeta roll (8ks), Maki avokádo (4ks), Maki Kappa (4ks)", 12.00m, null, true)
        }, time),
        DayOfWeek.Thursday => new(8, date, "Ostrokyslá polievka (330ml) (3,6) - 4,50 €", new()
        {
            new("Sklenené rezance s kuracím mäsom a zeleninou", 12.50m, null, false),
            new("Sklenené rezance s krevetami a zeleninou", 12.50m, null, false),
            new("Chrumkavé kuracie s arašidovou omáčkou a zeleninou", 11.00m, "(5,7,11)", false),
            new("Bún nem (jarné rolky s rezancami a šalátom)", 12.00m, null, false),
            new("Opekaná ryža s ananásom a kešu orieškami", 12.50m, "(1,3,8)", false),
            new("Sushi: Saro roll (8ks), Maki sake (4ks), Maki ebi (4ks)", 14.00m, null, false),
            new("Sushi: Vegeta roll (8ks), Maki avokádo (4ks), Maki oshinko (4ks)", 12.00m, null, true)
        }, time),
        _ => new(8, date, "Hanoi vývar (330ml) - 6,00 €", new()
        {
            new("Čínske rezance s hovädzím mäsom a zeleninou", 11.50m, null, false),
            new("Čínske rezance s krevetami a zeleninou", 11.50m, null, false),
            new("Bun Bó Nam Bo", 13.50m, "hovädzie, citrónová tráva, cesnak, rezance, šalát", false),
            new("Kuracie prsia tempura s rezancami a sweet chilli", 12.00m, null, false),
            new("Opekaná ryža s kuracím mäsom a zeleninou", 11.50m, "(1,3)", false),
            new("Sushi: Alaska roll (8ks), Maki maguro (4ks), Maki sake (4ks)", 14.50m, null, false),
            new("Sushi: Vegeta roll (8ks), Maki avokádo (4ks), Maki Kappa (4ks)", 12.00m, null, true)
        }, time)
    };
}
