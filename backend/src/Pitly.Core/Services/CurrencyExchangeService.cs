using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Pitly.Core.Services;

public class CurrencyExchangeService : ICurrencyExchangeService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CurrencyExchangeService> _logger;
    private readonly ConcurrentDictionary<string, decimal> _cache = new();

    public CurrencyExchangeService(HttpClient httpClient, ILogger<CurrencyExchangeService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<decimal> GetRateAsync(string currency, DateTime transactionDate)
    {
        if (currency.Equals("PLN", StringComparison.OrdinalIgnoreCase))
            return 1m;

        // "Date of dividend - 1 business day"
        var cacheKeyTransaction = $"{currency.ToUpperInvariant()}_TX_{transactionDate:yyyy-MM-dd}";
        if (_cache.TryGetValue(cacheKeyTransaction, out var transactionCached))
            return transactionCached;

        var effectiveDate = GetPreviousBusinessDay(transactionDate);

        var dateStr = effectiveDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var cacheKeyEffective = $"{currency.ToUpperInvariant()}_{dateStr}";

        if (_cache.TryGetValue(cacheKeyEffective, out var effectiveCached))
        {
            _cache.TryAdd(cacheKeyTransaction, effectiveCached);
            return effectiveCached;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var url = $"https://api.nbp.pl/api/exchangerates/rates/A/{currency.ToUpperInvariant()}/{dateStr}/?format=json";
                var response = await _httpClient.GetAsync(url);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("NBP rate not found for {Currency} on calculated effective date {Date}. NBP might be closed. Trying previous business day.", currency, dateStr);
                    effectiveDate = GetPreviousBusinessDay(effectiveDate);
                    dateStr = effectiveDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    cacheKeyEffective = $"{currency.ToUpperInvariant()}_{dateStr}";
                    
                    if (_cache.TryGetValue(cacheKeyEffective, out var effectiveCachedRetry))
                    {
                        _cache.TryAdd(cacheKeyTransaction, effectiveCachedRetry);
                        return effectiveCachedRetry;
                    }

                    continue;
                }

                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var rate = doc.RootElement
                    .GetProperty("rates")[0]
                    .GetProperty("mid")
                    .GetDecimal();

                _cache.TryAdd(cacheKeyEffective, rate);
                _cache.TryAdd(cacheKeyTransaction, rate);
                _logger.LogDebug("NBP rate for {Currency} on {Date}: {Rate}", currency, dateStr, rate);
                return rate;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "NBP API request failed for {Currency} on {Date} (attempt {Attempt}/3), retrying",
                    currency, dateStr, attempt + 1);
                await Task.Delay(500);
            }
        }

        _logger.LogError("Failed to get NBP rate for {Currency} near {Date}", currency, transactionDate);
        throw new InvalidOperationException(
            $"Could not find NBP exchange rate for {currency} near {transactionDate:yyyy-MM-dd}.");
    }

    private DateTime GetPreviousBusinessDay(DateTime date)
    {
        date = date.AddDays(-1);
        while (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday || IsPolishHoliday(date))
        {
            date = date.AddDays(-1);
        }
        return date;
    }

    private static bool IsPolishHoliday(DateTime date)
    {
        int y = date.Year;
        int a = y % 19;
        int b = y / 100;
        int c = y % 100;
        int d = b / 4;
        int e = b % 4;
        int f = (b + 8) / 25;
        int g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4;
        int k = c % 4;
        int l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451;
        int easterMonth = (h + l - 7 * m + 114) / 31;
        int easterDay = ((h + l - 7 * m + 114) % 31) + 1;

        DateTime easter = new DateTime(y, easterMonth, easterDay);
        DateTime easterMonday = easter.AddDays(1);
        DateTime corpusChristi = easter.AddDays(60);

        return (date.Month == 1 && date.Day == 1) ||
               (date.Month == 1 && date.Day == 6) ||
               (date.Month == 5 && date.Day == 1) ||
               (date.Month == 5 && date.Day == 3) ||
               (date.Month == 8 && date.Day == 15) ||
               (date.Month == 11 && date.Day == 1) ||
               (date.Month == 11 && date.Day == 11) ||
               (date.Month == 12 && date.Day == 25) ||
               (date.Month == 12 && date.Day == 26) ||
               date.Date == easterMonday.Date ||
               date.Date == corpusChristi.Date;
    }
}
