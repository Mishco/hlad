using System.Globalization;
using System.Text.Json;
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

    static async Task Main(string[] args)
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

        var restaurants = GetRestaurants();
        var menus = new List<DailyMenu>();

        // Scrape for today (Mon-Fri)
        var today = DateTime.Today;
        if (today.DayOfWeek == DayOfWeek.Saturday || today.DayOfWeek == DayOfWeek.Sunday)
        {
            Console.WriteLine("Weekend - using static data only");
        }

        foreach (var r in restaurants)
        {
            Console.Write($"Scraping {r.Name}... ");
            DailyMenu? menu = null;

            try
            {
                menu = r.WebsiteUrl switch
                {
                    var u when u.Contains("restauracie.sme.sk") => await ScrapeRestauracieSmeSk(r.Id, u),
                    var u when u.Contains("forumpoprad.sk") => await ScrapeForumPoprad(r.Id, u),
                    var u when u.Contains("menucka.sk") => await ScrapeMenuckaSk(r.Id, u),
                    var u when u.Contains("angrychef.sk") => await ScrapeAngryChef(r.Id, u),
                    _ => null
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex.Message}");
            }

            // Fallback to static data
            menu ??= GetStaticMenu(r.Id, today);

            if (menu != null)
            {
                menus.Add(menu);
                Console.WriteLine($"OK ({menu.Items.Count} items)");
            }
            else
            {
                Console.WriteLine("no menu");
            }
        }

        // Also generate static menus for the whole week (Mon-Fri)
        var weekMenus = new List<DailyMenu>();
        var monday = today.AddDays(-(int)today.DayOfWeek + 1);
        for (int d = 0; d < 5; d++)
        {
            var date = monday.AddDays(d);
            foreach (var r in restaurants)
            {
                DailyMenu? dayMenu;
                if (date == today)
                {
                    dayMenu = menus.FirstOrDefault(m => m.RestaurantId == r.Id);
                }
                else
                {
                    dayMenu = GetStaticMenu(r.Id, date);
                }
                if (dayMenu != null)
                    weekMenus.Add(dayMenu);
            }
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
        new(1, "Popradská Plzeňka", "Poprad", "https://restauracie.sme.sk/restauracia/popradska-plzenka_12459-poprad_2660/denne-menu", null, 4.9, new() { "Slovenská", "Tradičná", "Obedy" }),
        new(2, "Aquacity Poprad - High Tatras", "Športová 1397/1, 058 01 Poprad", "https://aquacity.sk/sluzby/menu/", null, 3.8, new() { "Hotel", "Chef's menu", "Medzinárodná" }),
        new(3, "Rock'n'Roll Steak Pub (Forum Poprad)", "Námestie sv. Egídia 3290/124, Poprad", "https://forumpoprad.sk/ponuka/obedove-menu/", "+421 948 007 051", 4.8, new() { "Steaky", "Burgre", "Pub" }),
        new(4, "Barn Club", "Francisciho 19, 058 01 Poprad", "https://menucka.sk/denne-menu/poprad/barn-club", "052/772 12 00", 4.7, new() { "Slovenská", "Pub", "Obedy" }),
        new(5, "Angry Chef", "Námestie svätého Egídia 10/23, 058 01 Poprad", "https://www.angrychef.sk/sk/sk-menu/", "+421 910 565 685", 4.5, new() { "Ázijská", "Street Food", "Bao", "Bowls" }),
        new(6, "Mamut Pub & Restaurant", "Moyzesova 5400/28, 058 01 Poprad", "https://mamutpoprad.sk/denne-menu/", "+421 919 300 300", 4.4, new() { "Moderná", "Pub", "Obedy" }),
        new(7, "Pho King", "Poprad", "https://www.phoking.sk/", "+421 949 698 790", 4.6, new() { "Vietnamská", "Ázijská", "Pho", "Bistro" }),
        new(8, "SAVOURY Asian Restaurant & Sushi Bar", "Námestie svätého Egídia 44/85, 058 01 Poprad", "https://wolt.com/sk/svk/poprad/restaurant/savoury-asian-restaurant-sushi-bar", "+421 949 487 358", 4.5, new() { "Ázijská", "Sushi", "Japonská", "Thajská" })
    };

    // ===== SCRAPERS =====

    static async Task<DailyMenu?> ScrapeRestauracieSmeSk(int restaurantId, string url)
    {
        var html = await Http.GetStringAsync(url);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var today = DateTime.Today;
        var todayStr = today.ToString("dd.MM.yyyy");

        var headings = doc.DocumentNode.SelectNodes("//h2");
        if (headings == null) return null;

        HtmlNode? todaySection = null;
        foreach (var h2 in headings)
        {
            if (h2.InnerText.Contains(todayStr))
            { todaySection = h2; break; }
        }
        if (todaySection == null) return null;

        var items = new List<MenuItem>();
        string? soup = null;
        var current = todaySection.NextSibling;
        bool foundSoup = false;

        while (current != null)
        {
            if (current.Name == "h2") break;
            var text = HtmlEntity.DeEntitize(current.InnerText).Trim();
            if (string.IsNullOrWhiteSpace(text)) { current = current.NextSibling; continue; }

            if (!foundSoup && text.Contains("0,33 l"))
            {
                soup = text.Replace("0,33 l", "").Trim();
                foundSoup = true;
            }
            else if (text.Contains("EUR"))
            {
                var priceMatch = Regex.Match(text, @"(\d+[.,]\d+)\s*EUR");
                if (priceMatch.Success)
                {
                    var priceStr = priceMatch.Groups[1].Value.Replace(",", ".");
                    decimal.TryParse(priceStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var price);
                    var itemName = Regex.Replace(text, @"^\d+\s*g\s*", "");
                    itemName = Regex.Replace(itemName, @"^\d+\.\s*", "");
                    itemName = Regex.Replace(itemName, @"\d+[.,]\d+\s*EUR$", "").Trim();
                    items.Add(new MenuItem(itemName, price, null, false));
                }
            }
            current = current.NextSibling;
        }

        if (items.Count == 0) return null;
        return new DailyMenu(restaurantId, DateOnly.FromDateTime(today).ToString("yyyy-MM-dd"), soup, items, DateTime.Now.ToString("HH:mm"));
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
