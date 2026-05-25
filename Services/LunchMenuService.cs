using LunchMenu.Models;
using Microsoft.Extensions.Caching.Memory;

namespace LunchMenu.Services;

public class LunchMenuService
{
    private readonly List<Restaurant> _restaurants;
    private readonly MenuScraperService _scraper;
    private readonly IMemoryCache _cache;
    private readonly ILogger<LunchMenuService> _logger;

    public LunchMenuService(MenuScraperService scraper, IMemoryCache cache, ILogger<LunchMenuService> logger)
    {
        _restaurants = GetSampleRestaurants();
        _scraper = scraper;
        _cache = cache;
        _logger = logger;
    }

    public List<Restaurant> GetAllRestaurants()
    {
        return _restaurants;
    }

    public Restaurant? GetRestaurantById(int id)
    {
        return _restaurants.FirstOrDefault(r => r.Id == id);
    }

    public async Task<DailyMenu?> GetMenuForDateAsync(int restaurantId, int dayOffset = 0)
    {
        var targetDate = DateTime.Today.AddDays(dayOffset);
        var cacheKey = $"menu_{restaurantId}_{targetDate:yyyy-MM-dd}";

        if (_cache.TryGetValue(cacheKey, out DailyMenu? cached))
            return cached;

        var restaurant = GetRestaurantById(restaurantId);
        if (restaurant?.WebsiteUrl == null) return null;

        DailyMenu? menu = null;

        // Only scrape for today (offset 0); for other days use static data
        if (dayOffset == 0)
        {
            try
            {
                menu = restaurant.WebsiteUrl switch
                {
                    var u when u.Contains("restauracie.sme.sk") =>
                        await _scraper.ScrapeRestauracieSmeSk(restaurantId, u),
                    var u when u.Contains("forumpoprad.sk") =>
                        await _scraper.ScrapeForumPoprad(restaurantId, u),
                    var u when u.Contains("menucka.sk") =>
                        await _scraper.ScrapeMenuckaSk(restaurantId, u),
                    var u when u.Contains("angrychef.sk") =>
                        await _scraper.ScrapeAngryChef(restaurantId, u),
                    var u when u.Contains("aquacity.sk") =>
                        await _scraper.ScrapeAquaCity(restaurantId, u),
                    var u when u.Contains("mamutpoprad.sk") =>
                        await _scraper.ScrapeMamut(restaurantId, u),
                    var u when u.Contains("phoking.sk") =>
                        await _scraper.ScrapePhokKing(restaurantId, u),
                    _ => null
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scraping failed for restaurant {Id}", restaurantId);
            }
        }

        // Fall back to static data
        menu ??= GetSampleMenus(dayOffset)
            .FirstOrDefault(m => m.RestaurantId == restaurantId && m.Date == DateOnly.FromDateTime(targetDate));

        // Cache for 1 hour
        var cacheOptions = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(TimeSpan.FromHours(1));
        _cache.Set(cacheKey, menu, cacheOptions);

        return menu;
    }

    public async Task<List<(Restaurant Restaurant, DailyMenu? Menu)>> GetTodaysOverviewAsync(
        string? search = null, int dayOffset = 0)
    {
        var results = new List<(Restaurant, DailyMenu?)>();
        foreach (var r in _restaurants)
        {
            var menu = await GetMenuForDateAsync(r.Id, dayOffset);

            if (menu != null)
            {
                // Filter by search query
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var filtered = menu.Items
                        .Where(i => i.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (filtered.Count == 0 &&
                        !(menu.SoupOfTheDay?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
                    {
                        menu = null;
                    }
                    else if (filtered.Count > 0)
                    {
                        menu = new DailyMenu { RestaurantId = menu.RestaurantId, Date = menu.Date, SoupOfTheDay = menu.SoupOfTheDay, Items = filtered, LastUpdated = menu.LastUpdated };
                    }
                }
            }

            results.Add((r, menu));
        }

        // Sort: restaurants with menus first, then by rating descending, then by name
        return results
            .OrderByDescending(r => r.Item2 != null)
            .ThenByDescending(r => r.Item1.Rating)
            .ThenBy(r => r.Item1.Name)
            .ToList();
    }

    public async Task<List<(Restaurant Restaurant, MenuItem Item)>> SearchMenusAsync(string query)
    {
        var results = new List<(Restaurant, MenuItem)>();
        foreach (var r in _restaurants)
        {
            var menu = await GetMenuForDateAsync(r.Id);
            if (menu == null) continue;

            var matches = menu.Items
                .Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var item in matches)
                results.Add((r, item!));
        }
        return results;
    }

    private static List<Restaurant> GetSampleRestaurants()
    {
        return new()
        {
            new Restaurant
            {
                Id = 1,
                Name = "Popradská Plzeňka",
                Address = "Poprad",
                WebsiteUrl = "https://restauracie.sme.sk/restauracia/popradska-plzenka_12459-poprad_2660/denne-menu",
                PhoneNumber = null,
                Rating = 4.9,
                Tags = new() { "Slovenská", "Tradičná", "Obedy" }
            },
            new Restaurant
            {
                Id = 2,
                Name = "Aquacity Poprad - High Tatras",
                Address = "Športová 1397/1, 058 01 Poprad",
                WebsiteUrl = "https://aquacity.sk/sluzby/menu/",
                PhoneNumber = null,
                Rating = 3.8,
                Tags = new() { "Hotel", "Chef's menu", "Medzinárodná" }
            },
            new Restaurant
            {
                Id = 3,
                Name = "Rock'n'Roll Steak Pub (Forum Poprad)",
                Address = "Námestie sv. Egídia 3290/124, Poprad",
                WebsiteUrl = "https://forumpoprad.sk/ponuka/obedove-menu/",
                PhoneNumber = "+421 948 007 051",
                Rating = 4.8,
                Tags = new() { "Steaky", "Burgre", "Pub" }
            },
            new Restaurant
            {
                Id = 4,
                Name = "Barn Club",
                Address = "Francisciho 19, 058 01 Poprad",
                WebsiteUrl = "https://menucka.sk/denne-menu/poprad/barn-club",
                PhoneNumber = "052/772 12 00",
                Rating = 4.7,
                Tags = new() { "Slovenská", "Pub", "Obedy" }
            },
            new Restaurant
            {
                Id = 5,
                Name = "Angry Chef",
                Address = "Námestie svätého Egídia 10/23, 058 01 Poprad",
                WebsiteUrl = "https://www.angrychef.sk/sk/sk-menu/",
                PhoneNumber = "+421 910 565 685",
                Rating = 4.5,
                Tags = new() { "Ázijská", "Street Food", "Bao", "Bowls" }
            },
            new Restaurant
            {
                Id = 6,
                Name = "Mamut Pub & Restaurant",
                Address = "Moyzesova 5400/28, 058 01 Poprad",
                WebsiteUrl = "https://mamutpoprad.sk/denne-menu/",
                PhoneNumber = "+421 919 300 300",
                Rating = 4.4,
                Tags = new() { "Moderná", "Pub", "Obedy" }
            },
            new Restaurant
            {
                Id = 7,
                Name = "Pho King",
                Address = "Poprad",
                WebsiteUrl = "https://www.phoking.sk/",
                PhoneNumber = "+421 949 698 790",
                Rating = 4.6,
                Tags = new() { "Vietnamská", "Ázijská", "Pho", "Bistro" }
            }
        };
    }

    private static List<DailyMenu> GetSampleMenus(int dayOffset = 0)
    {
        var targetDate = DateTime.Today.AddDays(dayOffset);
        var today = DateOnly.FromDateTime(targetDate);
        var dayOfWeek = targetDate.DayOfWeek;

        var menus = new List<DailyMenu>();

        // Popradská Plzeňka - Monday menu (25.05.2026)
        if (dayOfWeek == DayOfWeek.Monday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 1,
                Date = today,
                SoupOfTheDay = "Jemná krémová polievka s fazuľou a mrkvou (1, 7)",
                Items = new()
                {
                    new MenuItem { Name = "Bravčová vypražaná fašírka, zemiakové pyré so smotanou, šalát z kyslej kapusty", Price = 8.10m, Description = "150g (1, 3, 7)" },
                    new MenuItem { Name = "Hydinové srbské soté, ryža, hranolky", Price = 8.10m, Description = "150g" },
                    new MenuItem { Name = "Špagety Carbonara s parmezánom", Price = 7.80m, Description = "300g (1, 3, 7)" },
                    new MenuItem { Name = "Miešaný listový šalát s kuracími nugetkami, cocktailový dresing, bylinková bageta", Price = 9.10m, Description = "300g (1, 3, 7)" },
                    new MenuItem { Name = "Vyprážaný bravčový rezeň, zemiakový šalát s majonézou", Price = 8.10m, Description = "150g (1, 3, 7, 10)" }
                }
            });
        }
        else if (dayOfWeek == DayOfWeek.Tuesday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 1,
                Date = today,
                SoupOfTheDay = "Falošná gulášová s klobásou (1, 9)",
                Items = new()
                {
                    new MenuItem { Name = "Bravčový steak s bylinkovo-cesnakovou omáčkou, gratinované zemiaky", Price = 8.10m, Description = "150g (3, 7)" },
                    new MenuItem { Name = "Morčacie prsia na grilovanej zelenine, ryža", Price = 9.10m, Description = "150g" },
                    new MenuItem { Name = "Hrachový prívarok s volským okom, pečenou špekáčkou, chlieb", Price = 7.80m, Description = "300g (1, 3)" },
                    new MenuItem { Name = "Miešaný listový šalát s restovanými šampiňónmi a slaninou, vinaigrette, bylinková bageta", Price = 9.10m, Description = "300g (1, 3, 7)" },
                    new MenuItem { Name = "Vyprážaný bravčový rezeň, zemiakový šalát s majonézou", Price = 8.10m, Description = "150g (1, 3, 7, 10)" }
                }
            });
        }
        else if (dayOfWeek == DayOfWeek.Wednesday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 1,
                Date = today,
                SoupOfTheDay = "Cesnaková číra s vajíčkom a opečeným chlebom (1, 3)",
                Items = new()
                {
                    new MenuItem { Name = "Bravčové stehno na znojemský spôsob, slovenská ryža", Price = 8.10m, Description = "150g (1, 3, 7, 10)" },
                    new MenuItem { Name = "Kuracie medajlónky na hríbovom ragú, ryža, opekané zemiaky", Price = 8.10m, Description = "150g (7)" },
                    new MenuItem { Name = "Lasagne so zeleninou, paradajková omáčka s bazalkou", Price = 7.80m, Description = "300g (1, 3, 7)", IsVegetarian = true },
                    new MenuItem { Name = "Miešaný listový šalát s kuracími nugetkami, cocktailový dresing, bylinková bageta", Price = 9.10m, Description = "300g (1, 3, 7)" },
                    new MenuItem { Name = "Vyprážaný bravčový rezeň, zemiakový šalát s majonézou", Price = 8.10m, Description = "150g (1, 3, 7, 10)" }
                }
            });
        }
        else if (dayOfWeek == DayOfWeek.Thursday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 1,
                Date = today,
                SoupOfTheDay = "Šošovícová s kyslou kapustou (1, 7)",
                Items = new()
                {
                    new MenuItem { Name = "Guľky z mletého mäsa so smotanovo-horčicovou omáčkou, tlačené zemiaky, šalát", Price = 8.10m, Description = "150g (1, 3, 7, 10)" },
                    new MenuItem { Name = "Kurací závitok so šunkou, eidamom a kápiou, dusená ryža", Price = 8.10m, Description = "150g (7)" },
                    new MenuItem { Name = "Zeleninový cous-cous s opečeným údeným tofu a bazalkovým pestom", Price = 7.80m, Description = "300g (1, 7)", IsVegetarian = true },
                    new MenuItem { Name = "Miešaný listový šalát s restovanými šampiňónmi a slaninou, vinaigrette, bylinková bageta", Price = 9.10m, Description = "300g (1, 3, 7)" },
                    new MenuItem { Name = "Vyprážaný bravčový rezeň, zemiakový šalát s majonézou", Price = 8.10m, Description = "150g (1, 3, 7, 10)" }
                }
            });
        }
        else if (dayOfWeek == DayOfWeek.Friday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 1,
                Date = today,
                SoupOfTheDay = "Hubová polievka so zemiakmi (1)",
                Items = new()
                {
                    new MenuItem { Name = "Bratislavské pliecko na smotane, cestovina", Price = 8.10m, Description = "150g (1, 3, 7)" },
                    new MenuItem { Name = "Vyprážané vykostené kuracie stehno, zemiaková kaša, tatranský šalát", Price = 8.10m, Description = "150g (1, 3, 7)" },
                    new MenuItem { Name = "Tvarohové pirôžky s kakaom a rozpusteným maslom", Price = 7.80m, Description = "300g (1, 3, 7)", IsVegetarian = true },
                    new MenuItem { Name = "Miešaný listový šalát s kuracími nugetkami, cocktailový dresing, bylinková bageta", Price = 9.10m, Description = "300g (1, 3, 7)" },
                    new MenuItem { Name = "Vyprážaný bravčový rezeň, zemiakový šalát s majonézou", Price = 8.10m, Description = "150g (1, 3, 7, 10)" }
                }
            });
        }

        // Aquacity - Chef's menu (25.5-29.5.2026)
        if (dayOfWeek == DayOfWeek.Monday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 2,
                Date = today,
                SoupOfTheDay = "Slepačí vývar s rezancami (1, 3, 7, 9)",
                Items = new()
                {
                    new MenuItem { Name = "Bravčový rezeň v trojobale, zemiaková kaša, šalát", Price = 8.90m, Description = "150/200/50g (1, 3, 7)" },
                    new MenuItem { Name = "Grilovaný losos na špenátovom lôžku s ryžou", Price = 10.90m, Description = "150/150/50g (4, 7)" },
                    new MenuItem { Name = "Zeleninové rizoto s parmezánom", Price = 7.90m, Description = "350g (7)", IsVegetarian = true }
                },
                LastUpdated = DateTime.Now
            });
        }
        else if (dayOfWeek == DayOfWeek.Tuesday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 2,
                Date = today,
                SoupOfTheDay = "Krémová paradajková polievka s bazalkou (7)",
                Items = new()
                {
                    new MenuItem { Name = "Kuracie stehno na paprike, halušky", Price = 8.90m, Description = "200/200g (1, 3, 7)" },
                    new MenuItem { Name = "Hovädzie ragú s gnocchi", Price = 10.90m, Description = "250/150g (1, 3, 7)" },
                    new MenuItem { Name = "Šošovicový dhal s naan chlebom", Price = 7.90m, Description = "300/80g (1)", IsVegetarian = true }
                },
                LastUpdated = DateTime.Now
            });
        }
        else if (dayOfWeek == DayOfWeek.Wednesday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 2,
                Date = today,
                SoupOfTheDay = "Hubová polievka s kôprom (7)",
                Items = new()
                {
                    new MenuItem { Name = "Pečená kačica, lokše, červená kapusta", Price = 10.90m, Description = "200/150/80g (1, 7)" },
                    new MenuItem { Name = "Morčacie prsia na grile, grilovaná zelenina, bylinkové maslo", Price = 9.90m, Description = "180/150/20g (7)" },
                    new MenuItem { Name = "Caprese šalát s mozzarellou a pestom", Price = 7.90m, Description = "250g (7)", IsVegetarian = true }
                },
                LastUpdated = DateTime.Now
            });
        }
        else if (dayOfWeek == DayOfWeek.Thursday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 2,
                Date = today,
                SoupOfTheDay = "Gulášová polievka (1, 7)",
                Items = new()
                {
                    new MenuItem { Name = "Vyprážaný syr, hranolky, tatárska omáčka", Price = 8.90m, Description = "150/200/30g (1, 3, 7)", IsVegetarian = true },
                    new MenuItem { Name = "Grilovaný bravčový steak, pečené zemiaky, coleslaw", Price = 9.90m, Description = "180/200/50g (7, 10)" },
                    new MenuItem { Name = "Ázijská miska s tofu a ryžovými rezancami", Price = 7.90m, Description = "350g (6, 11)", IsVegetarian = true }
                },
                LastUpdated = DateTime.Now
            });
        }
        else if (dayOfWeek == DayOfWeek.Friday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 2,
                Date = today,
                SoupOfTheDay = "Rybacia polievka (4, 7, 9)",
                Items = new()
                {
                    new MenuItem { Name = "Pstruh na masle s mandľami, varené zemiaky", Price = 10.90m, Description = "200/200g (4, 7, 8)" },
                    new MenuItem { Name = "Hovädzí burger, hranolky, BBQ omáčka", Price = 9.90m, Description = "200/150/30g (1, 3, 7, 10)" },
                    new MenuItem { Name = "Špagety aglio olio s cherry paradajkami", Price = 7.90m, Description = "350g (1)", IsVegetarian = true }
                },
                LastUpdated = DateTime.Now
            });
        }

        // Rock'n'Roll Steak Pub (Forum Poprad) - Monday menu
        if (dayOfWeek == DayOfWeek.Monday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 3,
                Date = today,
                SoupOfTheDay = "Zemiaková na kyslo s vajíčkom a kôprom / Kurací vývar s koreňovou zeleninou",
                Items = new()
                {
                    new MenuItem { Name = "Kurací steak s rukolou a grilovanou repou, horčicový dip, ryža", Price = 8.90m },
                    new MenuItem { Name = "Zapekané zemiaky s kukuricou, brokolicou a so smotanou", Price = 8.90m, IsVegetarian = true },
                    new MenuItem { Name = "Pečené bravčové karé, hráškovo-zemiakové pyré", Price = 9.90m },
                    new MenuItem { Name = "Šalát s grilovaným hermelínom (ľadový šalát, mango, granátové jadierka, horčicovo-medový dip)", Price = 9.90m, IsVegetarian = true },
                    new MenuItem { Name = "Vyprážaný pastiersky syr, hranolky, tatárska omáčka", Price = 10.90m, IsVegetarian = true },
                    new MenuItem { Name = "Classic burger s hranolkami a tatárskou omáčkou", Price = 9.90m },
                    new MenuItem { Name = "Medailónky z bravčovej panenky na hubovej omáčke, opekané zemiaky s cesnakom", Price = 14.90m },
                    new MenuItem { Name = "Rib eye steak z vysokej roštenky z býka, bylinkové maslo, zelená fazuľa, grilovaná kukurica, opekané zemiaky", Price = 18.90m }
                }
            });
        }
        else if (dayOfWeek == DayOfWeek.Tuesday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 3,
                Date = today,
                SoupOfTheDay = "Krúpová s údeným mäsom / Kurací vývar s koreňovou zeleninou",
                Items = new()
                {
                    new MenuItem { Name = "Kurací steak na listovom šaláte, karamelizovaná hruška, granátové jadierka, tekvicový olej", Price = 8.90m },
                    new MenuItem { Name = "Tagliatelle s kuracím mäsom a pestom, cherry paradajky, parmezán", Price = 8.90m },
                    new MenuItem { Name = "Bravčový stroganov, 1/2 ryža, 1/2 hranolky", Price = 9.90m },
                    new MenuItem { Name = "Šalát s grilovaným hermelínom (ľadový šalát, mango, granátové jadierka, horčicovo-medový dip)", Price = 9.90m, IsVegetarian = true },
                    new MenuItem { Name = "Vyprážaný pastiersky syr, hranolky, tatárska omáčka", Price = 10.90m, IsVegetarian = true },
                    new MenuItem { Name = "Classic burger s hranolkami a tatárskou omáčkou", Price = 9.90m },
                    new MenuItem { Name = "Medailónky z bravčovej panenky na hubovej omáčke, opekané zemiaky s cesnakom", Price = 14.90m },
                    new MenuItem { Name = "Rib eye steak z vysokej roštenky z býka, bylinkové maslo, zelená fazuľa, grilovaná kukurica, opekané zemiaky", Price = 18.90m }
                }
            });
        }
        else if (dayOfWeek == DayOfWeek.Wednesday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 3,
                Date = today,
                SoupOfTheDay = "Paradajková s parmezánom / Kurací vývar s koreňovou zeleninou",
                Items = new()
                {
                    new MenuItem { Name = "Kurací steak na zelenej fazuli, dusená ryža", Price = 8.90m },
                    new MenuItem { Name = "Makové šúľance s maslom a jahodový dip", Price = 8.90m, IsVegetarian = true },
                    new MenuItem { Name = "Živánska pochúťka", Price = 9.90m },
                    new MenuItem { Name = "Šalát s grilovaným hermelínom (ľadový šalát, mango, granátové jadierka, horčicovo-medový dip)", Price = 9.90m, IsVegetarian = true },
                    new MenuItem { Name = "Vyprážaný pastiersky syr, hranolky, tatárska omáčka", Price = 10.90m, IsVegetarian = true },
                    new MenuItem { Name = "Classic burger s hranolkami a tatárskou omáčkou", Price = 9.90m },
                    new MenuItem { Name = "Medailónky z bravčovej panenky na hubovej omáčke, opekané zemiaky s cesnakom", Price = 14.90m },
                    new MenuItem { Name = "Rib eye steak z vysokej roštenky z býka, bylinkové maslo, zelená fazuľa, grilovaná kukurica, opekané zemiaky", Price = 18.90m }
                }
            });
        }
        else if (dayOfWeek == DayOfWeek.Thursday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 3,
                Date = today,
                SoupOfTheDay = "Hrášková krémová s krutónmi / Kurací vývar s koreňovou zeleninou",
                Items = new()
                {
                    new MenuItem { Name = "Kačací šalát s brusnicovým dressingom (mango, karamel, hruška)", Price = 8.90m },
                    new MenuItem { Name = "Šafránové rizoto s kuracím mäsom, parmezán", Price = 8.90m },
                    new MenuItem { Name = "Bravčové dusené s kyslou kapustou, zemiaková placka", Price = 9.90m },
                    new MenuItem { Name = "Šalát s grilovaným hermelínom (ľadový šalát, mango, granátové jadierka, horčicovo-medový dip)", Price = 9.90m, IsVegetarian = true },
                    new MenuItem { Name = "Vyprážaný pastiersky syr, hranolky, tatárska omáčka", Price = 10.90m, IsVegetarian = true },
                    new MenuItem { Name = "Classic burger s hranolkami a tatárskou omáčkou", Price = 9.90m },
                    new MenuItem { Name = "Medailónky z bravčovej panenky na hubovej omáčke, opekané zemiaky s cesnakom", Price = 14.90m },
                    new MenuItem { Name = "Rib eye steak z vysokej roštenky z býka, bylinkové maslo, zelená fazuľa, grilovaná kukurica, opekané zemiaky", Price = 18.90m }
                }
            });
        }
        else if (dayOfWeek == DayOfWeek.Friday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 3,
                Date = today,
                SoupOfTheDay = "Číra cesnaková s vajíčkom a syrom / Kurací vývar s koreňovou zeleninou",
                Items = new()
                {
                    new MenuItem { Name = "Kurací steak s opekanými zemiakmi s cesnakom, špargľa, syr", Price = 8.90m },
                    new MenuItem { Name = "Krémové rizoto s kuracím mäsom, sušená paradajka, parmezán", Price = 8.90m },
                    new MenuItem { Name = "Sviečková na smotane, domáca parená knedľa", Price = 9.90m },
                    new MenuItem { Name = "Šalát s grilovaným hermelínom (ľadový šalát, mango, granátové jadierka, horčicovo-medový dip)", Price = 9.90m, IsVegetarian = true },
                    new MenuItem { Name = "Vyprážaný pastiersky syr, hranolky, tatárska omáčka", Price = 10.90m, IsVegetarian = true },
                    new MenuItem { Name = "Classic burger s hranolkami a tatárskou omáčkou", Price = 9.90m },
                    new MenuItem { Name = "Medailónky z bravčovej panenky na hubovej omáčke, opekané zemiaky s cesnakom", Price = 14.90m },
                    new MenuItem { Name = "Rib eye steak z vysokej roštenky z býka, bylinkové maslo, zelená fazuľa, grilovaná kukurica, opekané zemiaky", Price = 18.90m }
                }
            });
        }

        // Barn Club - Monday menu
        if (dayOfWeek == DayOfWeek.Monday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 4,
                Date = today,
                SoupOfTheDay = "Zemiakovo-cesnaková (1) - 1,00 € / Fazuľovica s údeným a klobáskou, chlieb (1) - 4,90 €",
                Items = new()
                {
                    new MenuItem { Name = "Kuracie stripsy v panko strúhanke s hranolkami a dom. tatár. omáčkou", Price = 6.90m, Description = "130g (1,3,7,10)" },
                    new MenuItem { Name = "Francúzske zemiaky s klobásou, vajíčkami a kyslou uhorkou", Price = 6.90m, Description = "400g (1,3,7,10)" },
                    new MenuItem { Name = "Miešaný šalát s kuracím mäsom a cesnakovým dressingom, toust", Price = 6.90m, Description = "300g (1,10)" },
                    new MenuItem { Name = "Vyprážaný Encián s hranolkami a domácou tatárskou omáčkou", Price = 6.90m, Description = "110g (1,3,7,10)", IsVegetarian = true },
                    new MenuItem { Name = "Vyprážaný bravčový rezeň so zemiakmi a kyslou uhorkou", Price = 7.50m, Description = "150g (1,3,7,10)" }
                }
            });
        }
        else if (dayOfWeek == DayOfWeek.Tuesday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 4,
                Date = today,
                SoupOfTheDay = "Špenátová mliečna s vajíčkom (1,3,7) - 1,00 € / Fazuľovica s údeným a klobáskou, chlieb (1) - 4,90 €",
                Items = new()
                {
                    new MenuItem { Name = "Pečené kuracie stehno s dusenou ryžou a prírodnou šťavou, uhorkový šalát", Price = 6.90m, Description = "240g (1,3,7)" },
                    new MenuItem { Name = "Bratislavské špikované bravčové stehno s kolienkami", Price = 6.90m, Description = "130g (1,3,7,10)" },
                    new MenuItem { Name = "Miešaný šalát s kuracím mäsom a cesnakovým dressingom, toust", Price = 6.90m, Description = "300g (1,10)" },
                    new MenuItem { Name = "Vyprážaný Encián s hranolkami a domácou tatárskou omáčkou", Price = 6.90m, Description = "110g (1,3,7,10)", IsVegetarian = true },
                    new MenuItem { Name = "Vyprážaný bravčový rezeň so zemiakmi a kyslou uhorkou", Price = 7.50m, Description = "150g (1,3,7,10)" }
                }
            });
        }
        else if (dayOfWeek == DayOfWeek.Wednesday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 4,
                Date = today,
                SoupOfTheDay = "Zeleninová (9) - 1,00 € / Fazuľovica s údeným a klobáskou, chlieb (1) - 4,90 €",
                Items = new()
                {
                    new MenuItem { Name = "1/4 Pečená kačka s dusenou červenou kapustou na karameli a par. knedľou", Price = 8.60m, Description = "(1,3,7)" },
                    new MenuItem { Name = "Kurací plátok na prírodno s tarhoňou, kompót", Price = 6.90m, Description = "130g (1)" },
                    new MenuItem { Name = "Miešaný šalát s kuracím mäsom a cesnakovým dressingom, toust", Price = 6.90m, Description = "300g (1,10)" },
                    new MenuItem { Name = "Vyprážaný Encián s hranolkami a domácou tatárskou omáčkou", Price = 6.90m, Description = "110g (1,3,7,10)", IsVegetarian = true },
                    new MenuItem { Name = "Vyprážaný bravčový rezeň so zemiakmi a kyslou uhorkou", Price = 7.50m, Description = "150g (1,3,7,10)" }
                }
            });
        }
        else if (dayOfWeek == DayOfWeek.Thursday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 4,
                Date = today,
                SoupOfTheDay = "Kačací vývar s rezancami (1,3,9) - 1,00 € / Fazuľovica s údeným a klobáskou, chlieb (1) - 4,90 €",
                Items = new()
                {
                    new MenuItem { Name = "Bravčová panenka so smotanovo-cheddarovou omáčkou a pečenými zemiakmi", Price = 7.90m, Description = "130g" },
                    new MenuItem { Name = "Kuracie kúsky na karí s ananásom a ryžou", Price = 6.90m, Description = "130g (3,7)" },
                    new MenuItem { Name = "Miešaný šalát s kuracím mäsom a cesnakovým dressingom, toust", Price = 6.90m, Description = "300g (1,10)" },
                    new MenuItem { Name = "Vyprážaný Encián s hranolkami a domácou tatárskou omáčkou", Price = 6.90m, Description = "110g (1,3,7,10)", IsVegetarian = true },
                    new MenuItem { Name = "Vyprážaný bravčový rezeň so zemiakmi a kyslou uhorkou", Price = 7.50m, Description = "150g (1,3,7,10)" }
                }
            });
        }
        else if (dayOfWeek == DayOfWeek.Friday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 4,
                Date = today,
                SoupOfTheDay = "Tradičná cibuľačka s hriankou a syrom (1,3,7) - 1,00 € / Fazuľovica s údeným a klobáskou, chlieb (1) - 4,90 €",
                Items = new()
                {
                    new MenuItem { Name = "Pečený bôčik s kyslou kapustou a parenou knedľou", Price = 6.90m, Description = "130g (1,3,7,10)" },
                    new MenuItem { Name = "Tagliatelle s Nivovo-smotanovou omáčkou", Price = 6.90m, Description = "350g (1,3,7)" },
                    new MenuItem { Name = "Miešaný šalát s kuracím mäsom a cesnakovým dressingom, toust", Price = 6.90m, Description = "300g (1,10)" },
                    new MenuItem { Name = "Vyprážaný Encián s hranolkami a domácou tatárskou omáčkou", Price = 6.90m, Description = "110g (1,3,7,10)", IsVegetarian = true },
                    new MenuItem { Name = "Vyprážaný bravčový rezeň so zemiakmi a kyslou uhorkou", Price = 7.50m, Description = "150g (1,3,7,10)" }
                }
            });
        }

        // Angry Chef - evergreen menu (available every day Mon-Sat)
        if (dayOfWeek != DayOfWeek.Sunday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 5,
                Date = today,
                SoupOfTheDay = "Tom Yum s morčacím mäsom (250ml - 4,00€ / 400ml - 7,00€)",
                Items = new()
                {
                    new MenuItem { Name = "Bao s trhaným bravčovým", Price = 6.00m, Description = "Kimchi majonéza, domáca čalamáda, koriander (1,6)" },
                    new MenuItem { Name = "Bao s trhaným hovädzím", Price = 7.00m, Description = "Mangový dressing, nakladaná redkvička, koriander (1,6)" },
                    new MenuItem { Name = "Bao s krevetami", Price = 7.00m, Description = "Spicy mayo, nakladaná redkvička, koriander (1,6)" },
                    new MenuItem { Name = "Bao s chrumkavým bôčikom", Price = 6.00m, Description = "Arašidová satay omáčka, domáca čalamáda, koriander (1,5,6)" },
                    new MenuItem { Name = "Bao s údeným tofu", Price = 6.00m, Description = "Hlivové teriyaki, nakladaná uhorka, sezam, koriander (1,6,11)", IsVegetarian = true },
                    new MenuItem { Name = "Bowl s krevetami", Price = 13.00m, Description = "Jasmínová ryža, spicy mayo, pakchoi, mrkva, edamame (2,3,6,11)" },
                    new MenuItem { Name = "Bowl s trhaným hovädzím", Price = 13.00m, Description = "Jasmínová ryža, mangový dressing, nakladaná zelenina, kimchi (1,5,6)" },
                    new MenuItem { Name = "Bowl s trhaným bravčovým", Price = 11.00m, Description = "Jasmínová ryža, kimchi majonéza, nakladaná zelenina (1,5,6)" },
                    new MenuItem { Name = "Bowl s chrumkavým bôčikom", Price = 12.00m, Description = "Jasmínová ryža, arašidová satay omáčka, nakladaná zelenina (1,5,6)" },
                    new MenuItem { Name = "Bowl s údeným tofu", Price = 10.00m, Description = "Jasmínová ryža, hlivové teriyaki, nakladaná zelenina (1,5,6)", IsVegetarian = true },
                    new MenuItem { Name = "VEGAN Šošovicový dhal", Price = 7.50m, Description = "Kari z červenej šošovice, paradajky, jasmínová ryža, koriander (1,11)", IsVegetarian = true },
                    new MenuItem { Name = "Ázijské bravčové rebrá", Price = 14.50m, Description = "Jasmínová ryža, BBQ omáčka, kimchi, koriander (1,6,10)" }
                },
                LastUpdated = DateTime.Now
            });
        }

        // Mamut Pub & Restaurant - daily menu (25.5-29.5.2026)
        if (dayOfWeek == DayOfWeek.Monday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 6,
                Date = today,
                SoupOfTheDay = "Romanesco krém, krutóny (1,3,7) / Slepačí vývar s domácimi rezancami a koreňovou zeleninou (1,3,9)",
                Items = new()
                {
                    new MenuItem { Name = "Kuracie stripsy v corn flakes", Price = 8.95m, Description = "parmezánová kaša, údená mayo (1,3,7)" },
                    new MenuItem { Name = "Bulgur s grilovanou zeleninou", Price = 8.95m, Description = "quinoa, halloumi, medovo limetkový vinaigrette (1)", IsVegetarian = true },
                    new MenuItem { Name = "Fish and chips", Price = 10.95m, Description = "hráškové pyré (1,3,4)" },
                    new MenuItem { Name = "Panenka sous vide", Price = 12.95m, Description = "karfiolové pyré, špargľa, pálené zemiaky, demi glacé (7)" }
                },
                LastUpdated = DateTime.Now
            });
        }
        else if (dayOfWeek == DayOfWeek.Tuesday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 6,
                Date = today,
                SoupOfTheDay = "Špenátová s vajíčkom (3) / Slepačí vývar s domácimi rezancami a koreňovou zeleninou (1,3,9)",
                Items = new()
                {
                    new MenuItem { Name = "Restovaná kuracia pečeň s panenkou", Price = 8.95m, Description = "farebná paprika, ryža s hráškom" },
                    new MenuItem { Name = "Vyprážaná mozzarella", Price = 8.95m, Description = "batátové pyré, brusnice, mix šalát (1,3,7)", IsVegetarian = true },
                    new MenuItem { Name = "Fish and chips", Price = 10.95m, Description = "hráškové pyré (1,3,4)" },
                    new MenuItem { Name = "Panenka sous vide", Price = 12.95m, Description = "karfiolové pyré, špargľa, pálené zemiaky, demi glacé (7)" }
                },
                LastUpdated = DateTime.Now
            });
        }
        else if (dayOfWeek == DayOfWeek.Wednesday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 6,
                Date = today,
                SoupOfTheDay = "Hubová na paprike s mrvenicou a zemiakami (1) / Slepačí vývar s domácimi rezancami a koreňovou zeleninou (1,3,9)",
                Items = new()
                {
                    new MenuItem { Name = "Bravčová roláda s prosciuttom a špenátom", Price = 8.95m, Description = "štuchané zemiaky, chrumkavá cibuľa, demi glacé (1)" },
                    new MenuItem { Name = "Penne puttanesca", Price = 8.95m, Description = "(1,3,4)" },
                    new MenuItem { Name = "Fish and chips", Price = 10.95m, Description = "hráškové pyré (1,3,4)" },
                    new MenuItem { Name = "Panenka sous vide", Price = 12.95m, Description = "karfiolové pyré, špargľa, pálené zemiaky, demi glacé (7)" }
                },
                LastUpdated = DateTime.Now
            });
        }
        else if (dayOfWeek == DayOfWeek.Thursday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 6,
                Date = today,
                SoupOfTheDay = "Frankfurtská / Slepačí vývar s domácimi rezancami a koreňovou zeleninou (1,3,9)",
                Items = new()
                {
                    new MenuItem { Name = "Kurací rezeň v bylinkovo parmezánovej strúhanke", Price = 8.95m, Description = "mrkvovo zemiakové pyré, šalát (1,3,7)" },
                    new MenuItem { Name = "Gnocchi Hokkaido", Price = 8.95m, Description = "semiačka, tempeh, rukola (1,3,6)", IsVegetarian = true },
                    new MenuItem { Name = "Fish and chips", Price = 10.95m, Description = "hráškové pyré (1,3,4)" },
                    new MenuItem { Name = "Panenka sous vide", Price = 12.95m, Description = "karfiolové pyré, špargľa, pálené zemiaky, demi glacé (7)" }
                },
                LastUpdated = DateTime.Now
            });
        }
        else if (dayOfWeek == DayOfWeek.Friday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 6,
                Date = today,
                SoupOfTheDay = "Šampiňónový krém, krutóny (1,3,7) / Slepačí vývar s domácimi rezancami a koreňovou zeleninou (1,3,9)",
                Items = new()
                {
                    new MenuItem { Name = "Lasagne bolognese", Price = 8.95m, Description = "paradajková omáčka, rukola (1,3,7)" },
                    new MenuItem { Name = "Domáce šišky", Price = 8.95m, Description = "pistáciové mascarpone, čokoláda (1,3,7,8)" },
                    new MenuItem { Name = "Fish and chips", Price = 10.95m, Description = "hráškové pyré (1,3,4)" },
                    new MenuItem { Name = "Panenka sous vide", Price = 12.95m, Description = "karfiolové pyré, špargľa, pálené zemiaky, demi glacé (7)" }
                },
                LastUpdated = DateTime.Now
            });
        }

        // Pho King - fixed menu (available every day)
        if (dayOfWeek != DayOfWeek.Sunday)
        {
            menus.Add(new DailyMenu
            {
                RestaurantId = 7,
                Date = today,
                SoupOfTheDay = null,
                Items = new()
                {
                    new MenuItem { Name = "Pho Bo (hovädzí vývar)", Price = 8.90m, Description = "ryžové rezance, hovädzie mäso, bylinky" },
                    new MenuItem { Name = "Pho Ga (slepačí vývar)", Price = 7.90m, Description = "ryžové rezance, kuracie mäso, bylinky" },
                    new MenuItem { Name = "Bun Bo Nam Bo", Price = 8.90m, Description = "ryžové rezance, hovädzie mäso, arašidy, zelenina" },
                    new MenuItem { Name = "Com Rang (vyprážaná ryža)", Price = 7.90m, Description = "s kuracím mäsom a zeleninou" },
                    new MenuItem { Name = "Mi Xao (vyprážané rezance)", Price = 7.90m, Description = "s kuracím mäsom a zeleninou" },
                    new MenuItem { Name = "Curi Ga (kuracie kari)", Price = 8.50m, Description = "kokosové mlieko, zelenina, ryža" },
                    new MenuItem { Name = "Udon s krevetami", Price = 9.90m, Description = "udon rezance, krevety, zelenina, teriyaki" },
                    new MenuItem { Name = "Jarné rolky (2ks)", Price = 3.90m, Description = "s bravčovým mäsom" }
                },
                LastUpdated = DateTime.Now
            });
        }

        return menus;
    }
}
