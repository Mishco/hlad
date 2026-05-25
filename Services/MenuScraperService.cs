using System.Globalization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using LunchMenu.Models;

namespace LunchMenu.Services;

public class MenuScraperService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MenuScraperService> _logger;

    public MenuScraperService(HttpClient httpClient, ILogger<MenuScraperService> logger)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _logger = logger;
    }

    /// <summary>
    /// Scrapes daily menu from restauracie.sme.sk
    /// Structure: h2 contains day+date, then table-like rows with portions, items, prices
    /// </summary>
    public async Task<DailyMenu?> ScrapeRestauracieSmeSk(int restaurantId, string url)
    {
        try
        {
            var html = await _httpClient.GetStringAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var today = DateTime.Today;
            var todayStr = today.ToString("dd.MM.yyyy");

            // Find the h2 that contains today's date
            var headings = doc.DocumentNode.SelectNodes("//h2");
            if (headings == null) return null;

            HtmlNode? todaySection = null;
            foreach (var h2 in headings)
            {
                if (h2.InnerText.Contains(todayStr))
                {
                    todaySection = h2;
                    break;
                }
            }

            if (todaySection == null) return null;

            var menu = new DailyMenu
            {
                RestaurantId = restaurantId,
                Date = DateOnly.FromDateTime(today)
            };

            // Iterate sibling elements after the h2 until next h2
            var current = todaySection.NextSibling;
            bool foundSoup = false;
            while (current != null)
            {
                if (current.Name == "h2") break;

                var text = HtmlEntity.DeEntitize(current.InnerText).Trim();

                if (string.IsNullOrWhiteSpace(text))
                {
                    current = current.NextSibling;
                    continue;
                }

                // On restauracie.sme.sk, the soup line typically has "0,33 l" prefix
                // and meal lines have a price in EUR
                if (!foundSoup && text.Contains("0,33 l"))
                {
                    // Soup line - extract text after "0,33 l"
                    var soupText = text.Replace("0,33 l", "").Trim();
                    menu.SoupOfTheDay = soupText;
                    foundSoup = true;
                }
                else if (text.Contains("EUR"))
                {
                    // Menu item line: "150 g  1. Name (allergens)  8,10 EUR"
                    var priceMatch = Regex.Match(text, @"(\d+[.,]\d+)\s*EUR");
                    if (priceMatch.Success)
                    {
                        var priceStr = priceMatch.Groups[1].Value.Replace(",", ".");
                        decimal.TryParse(priceStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var price);

                        // Get item name - remove portion prefix (e.g., "150 g"), number prefix, and price
                        var itemName = Regex.Replace(text, @"^\d+\s*g\s*", "");
                        itemName = Regex.Replace(itemName, @"^\d+\.\s*", "");
                        itemName = Regex.Replace(itemName, @"\d+[.,]\d+\s*EUR$", "").Trim();

                        menu.Items.Add(new MenuItem
                        {
                            Name = itemName,
                            Price = price
                        });
                    }
                }

                current = current.NextSibling;
            }

            return menu.Items.Count > 0 ? menu : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scrape {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Scrapes daily menu from forumpoprad.sk
    /// Structure: WordPress content with <strong> for day headers, <p> for soups, <ol> for items
    /// </summary>
    public async Task<DailyMenu?> ScrapeForumPoprad(int restaurantId, string url)
    {
        try
        {
            var html = await _httpClient.GetStringAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var today = DateTime.Today;
            var contentNode = doc.DocumentNode.SelectSingleNode("//section[@id='offerDetail']//div[@class='text']");
            if (contentNode == null) return null;

            // Find today's day name in Slovak
            var dayNames = new Dictionary<DayOfWeek, string>
            {
                { DayOfWeek.Monday, "Pondelok" },
                { DayOfWeek.Tuesday, "Utorok" },
                { DayOfWeek.Wednesday, "Streda" },
                { DayOfWeek.Thursday, "Štvrtok" },
                { DayOfWeek.Friday, "Piatok" }
            };

            if (!dayNames.TryGetValue(today.DayOfWeek, out var todayName))
                return null; // Weekend

            var menu = new DailyMenu
            {
                RestaurantId = restaurantId,
                Date = DateOnly.FromDateTime(today)
            };

            // Find the <strong> or <p><strong> with today's day
            var allNodes = contentNode.ChildNodes.ToList();
            bool inTodaySection = false;
            bool foundSoup = false;

            for (int i = 0; i < allNodes.Count; i++)
            {
                var node = allNodes[i];
                var text = HtmlEntity.DeEntitize(node.InnerText).Trim();

                if (text.Contains(todayName) && node.InnerHtml.Contains("<strong>"))
                {
                    inTodaySection = true;
                    continue;
                }

                // If we hit the next day, stop
                if (inTodaySection && node.InnerHtml != null && node.InnerHtml.Contains("<strong>")
                    && dayNames.Values.Any(d => text.Contains(d)) && !text.Contains(todayName))
                {
                    break;
                }

                if (!inTodaySection) continue;

                // Soup line starts with "Polievka:"
                if (!foundSoup && text.StartsWith("Polievka:"))
                {
                    menu.SoupOfTheDay = text.Replace("Polievka:", "").Trim();
                    foundSoup = true;
                }

                // Items are in <ol> lists
                if (node.Name == "ol")
                {
                    var lis = node.SelectNodes(".//li");
                    if (lis == null) continue;

                    foreach (var li in lis)
                    {
                        var itemText = HtmlEntity.DeEntitize(li.InnerText).Trim();
                        // Extract price if in parentheses at end: (9,90€)
                        var priceMatch = Regex.Match(itemText, @"\((\d+[.,]\d+)€\)\s*$");
                        decimal price = 8.90m; // default menu price
                        if (priceMatch.Success)
                        {
                            var priceStr = priceMatch.Groups[1].Value.Replace(",", ".");
                            decimal.TryParse(priceStr, NumberStyles.Any, CultureInfo.InvariantCulture, out price);
                            itemText = itemText[..itemText.LastIndexOf('(')].Trim();
                        }

                        menu.Items.Add(new MenuItem
                        {
                            Name = itemText,
                            Price = price
                        });
                    }
                }
            }

            return menu.Items.Count > 0 ? menu : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scrape {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Scrapes daily menu from menucka.sk
    /// Structure: div.day-title for day name, then div.col-xs-10 for items, div.col-xs-2.price for prices
    /// </summary>
    public async Task<DailyMenu?> ScrapeMenuckaSk(int restaurantId, string url)
    {
        try
        {
            var html = await _httpClient.GetStringAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var today = DateTime.Today;
            var todayStr = today.ToString("dd.MM.yyyy");

            // Find day-title div containing today's date
            var dayTitles = doc.DocumentNode.SelectNodes("//div[@class='day-title']");
            if (dayTitles == null) return null;

            HtmlNode? todayTitle = null;
            foreach (var dt in dayTitles)
            {
                var text = HtmlEntity.DeEntitize(dt.InnerText).Trim();
                if (text.Contains(todayStr) || text.Contains(today.ToString("dd.MM.yyyy")))
                {
                    todayTitle = dt;
                    break;
                }

                // Also match format like "25.05.2026" in parentheses
                var dayNames = new Dictionary<DayOfWeek, string>
                {
                    { DayOfWeek.Monday, "Pondelok" },
                    { DayOfWeek.Tuesday, "Utorok" },
                    { DayOfWeek.Wednesday, "Streda" },
                    { DayOfWeek.Thursday, "Štvrtok" },
                    { DayOfWeek.Friday, "Piatok" }
                };
                if (dayNames.TryGetValue(today.DayOfWeek, out var dayName) && text.Contains(dayName))
                {
                    todayTitle = dt;
                    break;
                }
            }

            if (todayTitle == null) return null;

            var menu = new DailyMenu
            {
                RestaurantId = restaurantId,
                Date = DateOnly.FromDateTime(today)
            };

            // The day-title is inside a col-xs-12. Items follow as pairs of col-xs-10 (name) + col-xs-2 (price)
            // Navigate from the parent of day-title through next siblings
            var container = todayTitle.ParentNode; // col-xs-12 containing day-title
            var current = container?.NextSibling;
            bool firstItem = true;

            while (current != null)
            {
                // Stop at next day-title
                if (current.SelectSingleNode(".//div[@class='day-title']") != null)
                    break;

                var classes = current.GetAttributeValue("class", "");
                var text = HtmlEntity.DeEntitize(current.InnerText).Trim();

                if (classes.Contains("col-xs-10") && !string.IsNullOrWhiteSpace(text))
                {
                    // This is a menu item name - peek at next sibling for price
                    var priceNode = current.NextSibling;
                    while (priceNode != null && priceNode.NodeType != HtmlNodeType.Element)
                        priceNode = priceNode.NextSibling;

                    decimal price = 0;
                    if (priceNode != null)
                    {
                        var priceText = HtmlEntity.DeEntitize(priceNode.InnerText).Trim()
                            .Replace("€", "").Replace("&nbsp;", "").Replace("\u00a0", "").Trim();
                        priceText = priceText.Replace(",", ".");
                        decimal.TryParse(priceText, NumberStyles.Any, CultureInfo.InvariantCulture, out price);
                    }

                    // First items with low price (1.00€) are soups
                    if (firstItem && price <= 1.50m && price > 0)
                    {
                        menu.SoupOfTheDay = text;
                        firstItem = false;
                    }
                    else if (price > 0)
                    {
                        menu.Items.Add(new MenuItem
                        {
                            Name = text,
                            Price = price
                        });
                        firstItem = false;
                    }
                }

                current = current.NextSibling;
            }

            return menu.Items.Count > 0 ? menu : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scrape {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Mamut menu is served as images - return null to fall back to static data.
    /// </summary>
    public async Task<DailyMenu?> ScrapeMamut(int restaurantId, string url)
    {
        _logger.LogInformation("Mamut menu is in image format (menupp.united.sk), using static data");
        return null;
    }

    /// <summary>
    /// Pho King has a fixed PDF menu - return null to fall back to static data.
    /// </summary>
    public async Task<DailyMenu?> ScrapePhokKing(int restaurantId, string url)
    {
        _logger.LogInformation("Pho King menu is a fixed PDF, using static data");
        return null;
    }

    /// <summary>
    /// Scrapes Aquacity menu page - extracts PDF link for reference.
    /// The actual menu is in a PDF/image, so we return null to fall back to static data.
    /// </summary>
    public async Task<DailyMenu?> ScrapeAquaCity(int restaurantId, string url)
    {
        try
        {
            // The menu is published as a PDF/image - we can't easily parse it.
            // Return null to use static/hardcoded data.
            // In a production app, you'd use OCR or manual data entry.
            _logger.LogInformation("AquaCity menu is in PDF format, using static data");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scrape AquaCity {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Scrapes the evergreen menu from angrychef.sk (not daily, but permanent menu).
    /// </summary>
    public async Task<DailyMenu?> ScrapeAngryChef(int restaurantId, string url)
    {
        try
        {
            var html = await _httpClient.GetStringAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var items = new List<MenuItem>();

            // Menu items are in h4 elements with price info in following paragraphs
            var h4Nodes = doc.DocumentNode.SelectNodes("//h4");
            if (h4Nodes == null) return null;

            foreach (var h4 in h4Nodes)
            {
                var name = h4.InnerText.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                // Find price in sibling/following text - look for €
                var parent = h4.ParentNode;
                var parentText = parent?.InnerText ?? "";

                // Extract prices like "7,00€" or "13,00€"
                var priceMatch = Regex.Match(parentText, @"(\d+[,\.]\d{2})\s*€", RegexOptions.RightToLeft);
                decimal price = 0;
                if (priceMatch.Success)
                {
                    decimal.TryParse(priceMatch.Groups[1].Value.Replace(',', '.'),
                        NumberStyles.Any, CultureInfo.InvariantCulture, out price);
                }

                if (price > 0)
                {
                    // Get description from text after h4
                    var descNode = h4.NextSibling;
                    string? description = null;
                    while (descNode != null)
                    {
                        var text = descNode.InnerText.Trim();
                        if (!string.IsNullOrWhiteSpace(text) && !Regex.IsMatch(text, @"^\d+[,\.]\d{2}\s*€"))
                        {
                            description = text.Split('\n').FirstOrDefault()?.Trim();
                            break;
                        }
                        descNode = descNode.NextSibling;
                    }

                    items.Add(new MenuItem
                    {
                        Name = name,
                        Price = price,
                        Description = description,
                        IsVegetarian = name.Contains("tofu", StringComparison.OrdinalIgnoreCase) ||
                                       name.Contains("VEGAN", StringComparison.OrdinalIgnoreCase)
                    });
                }
            }

            if (items.Count == 0) return null;

            return new DailyMenu
            {
                RestaurantId = restaurantId,
                Date = DateOnly.FromDateTime(DateTime.Today),
                SoupOfTheDay = null,
                Items = items,
                LastUpdated = DateTime.Now
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scrape Angry Chef {Url}", url);
            return null;
        }
    }
}
