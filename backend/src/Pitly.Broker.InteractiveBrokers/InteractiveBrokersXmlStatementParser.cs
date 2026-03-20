using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Pitly.Core.Models;
using Pitly.Core.Parsing;

namespace Pitly.Broker.InteractiveBrokers;

public partial class InteractiveBrokersXmlStatementParser : IStatementParser
{
    private readonly ILogger<InteractiveBrokersXmlStatementParser> _logger;

    public InteractiveBrokersXmlStatementParser(ILogger<InteractiveBrokersXmlStatementParser> logger)
    {
        _logger = logger;
    }

    public ParsedStatement Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new FormatException("File is empty.");
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(content);
        }
        catch (Exception ex)
        {
            throw new FormatException($"Failed to parse XML content: {ex.Message}", ex);
        }

        var trades = new List<Trade>();
        var dividends = new List<RawDividend>();
        var withholdingTaxes = new List<RawWithholdingTax>();

        // Find <Trade> elements (Order-level Flex Query format)
        var tradeElements = doc.Descendants("Trade");
        foreach (var tradeEl in tradeElements)
        {
            TryParseTrade(tradeEl, trades);
        }

        // Find <Lot> elements (CLOSED_LOT Flex Query format)
        // These represent pre-matched closed lots that already include cost basis.
        // We generate synthetic Buy + Sell trades so the FIFO engine can process them.
        var lotElements = doc.Descendants("Lot");
        foreach (var lotEl in lotElements)
        {
            TryParseClosedLot(lotEl, trades);
        }

        // Find all CashTransactions (Dividends and Withholding Taxes)
        var cashTransactions = doc.Descendants("CashTransaction");
        foreach (var cashEl in cashTransactions)
        {
            var typeAttr = cashEl.Attribute("type")?.Value ?? string.Empty;
            if (typeAttr.Contains("Dividend", StringComparison.OrdinalIgnoreCase) && 
                !typeAttr.Contains("Withholding", StringComparison.OrdinalIgnoreCase))
            {
                TryParseDividend(cashEl, dividends);
            }
            else if (typeAttr.Contains("Withholding Tax", StringComparison.OrdinalIgnoreCase))
            {
                TryParseWithholdingTax(cashEl, withholdingTaxes);
            }
        }

        if (trades.Count == 0 && dividends.Count == 0 && withholdingTaxes.Count == 0)
        {
            _logger.LogWarning("File parsed successfully as XML but no relevant data was found (no Trades, Lots, or CashTransactions).");
        }

        return new ParsedStatement(trades, dividends, withholdingTaxes);
    }

    private void TryParseTrade(XElement element, List<Trade> trades)
    {
        var dataDiscriminator = element.Attribute("dataDiscriminator")?.Value ?? element.Attribute("DataDiscriminator")?.Value ?? "Order";
        if (dataDiscriminator == "Header" || dataDiscriminator == "SubTotal" || dataDiscriminator == "Total") return;

        var assetCategory = element.Attribute("assetCategory")?.Value;
        // Accept STK, Stocks, ETF, or EQ
        if (assetCategory != "Stocks" && assetCategory != "STK" && assetCategory != "ETF" && assetCategory != "EQ") return;

        var currency = element.Attribute("currency")?.Value ?? "USD";
        var symbol = element.Attribute("symbol")?.Value ?? string.Empty;
        var description = element.Attribute("description")?.Value ?? string.Empty;
        var dateTimeStr = element.Attribute("dateTime")?.Value ?? element.Attribute("tradeDate")?.Value ?? string.Empty;
        var quantityStr = element.Attribute("quantity")?.Value ?? "0";
        var priceStr = element.Attribute("tradePrice")?.Value ?? "0";
        var proceedsStr = element.Attribute("tradeMoney")?.Value ?? element.Attribute("proceeds")?.Value ?? "0";
        var commissionStr = element.Attribute("ibCommission")?.Value ?? element.Attribute("commission")?.Value ?? "0";
        var realizedPnlStr = element.Attribute("realizedMtm")?.Value ?? element.Attribute("realizedMtmPercent")?.Value ?? "0";

        if (!TryParseDateTime(dateTimeStr, out var dateTime))
        {
            _logger.LogWarning("Skipping XML trade row for {Symbol}: could not parse date '{DateStr}'", symbol, dateTimeStr);
            return;
        }

        if (!TryParseDecimal(quantityStr, out var quantity)) return;
        if (!TryParseDecimal(priceStr, out var price)) return;
        if (!TryParseDecimal(proceedsStr, out var proceeds)) return;
        if (!TryParseDecimal(commissionStr, out var commission)) return;
        
        TryParseDecimal(realizedPnlStr, out var realizedPnl);

        if (quantity == 0) return; // Ignore zero quantity trades 

        var buySellStr = element.Attribute("buySell")?.Value ?? element.Attribute("buy/sell")?.Value ?? string.Empty;
        TradeType tradeType;
        if (buySellStr.StartsWith("BUY", StringComparison.OrdinalIgnoreCase))
        {
            tradeType = TradeType.Buy;
        }
        else if (buySellStr.StartsWith("SELL", StringComparison.OrdinalIgnoreCase))
        {
            tradeType = TradeType.Sell;
        }
        else
        {
            tradeType = quantity > 0 ? TradeType.Buy : TradeType.Sell;
        }

        quantity = Math.Abs(quantity);
        proceeds = Math.Abs(proceeds);

        trades.Add(new Trade(symbol, currency, dateTime, quantity, price, proceeds,
            Math.Abs(commission), realizedPnl, tradeType, description));
    }

    /// <summary>
    /// Handles IB Flex Query CLOSED_LOT format. Each &lt;Lot&gt; represents a single
    /// sell matched against a specific original buy lot. We generate a synthetic
    /// Buy trade (from openDateTime + cost) and a Sell trade (from dateTime + computed proceeds).
    ///
    /// IMPORTANT: In CLOSED_LOT format:
    ///   - tradePrice = original purchase price per share (NOT the sell price!)
    ///   - cost = total cost basis
    ///   - fifoPnlRealized = realized profit/loss
    ///   - proceeds / tradeMoney = EMPTY
    ///   - Sell proceeds = cost + fifoPnlRealized
    /// </summary>
    private void TryParseClosedLot(XElement element, List<Trade> trades)
    {
        var levelOfDetail = element.Attribute("levelOfDetail")?.Value ?? string.Empty;
        if (levelOfDetail != "CLOSED_LOT" && levelOfDetail != string.Empty) return;

        var assetCategory = element.Attribute("assetCategory")?.Value;
        if (assetCategory != "Stocks" && assetCategory != "STK" && assetCategory != "ETF" && assetCategory != "EQ") return;

        var currency = element.Attribute("currency")?.Value ?? "USD";
        var symbol = element.Attribute("symbol")?.Value ?? string.Empty;
        var description = element.Attribute("description")?.Value ?? string.Empty;

        // Sell side: dateTime is when the position was closed (sold)
        var sellDateStr = element.Attribute("dateTime")?.Value ?? element.Attribute("tradeDate")?.Value ?? string.Empty;
        // Buy side: openDateTime is when the position was originally opened (bought)
        var buyDateStr = element.Attribute("openDateTime")?.Value ?? string.Empty;

        var quantityStr = element.Attribute("quantity")?.Value ?? "0";
        var costStr = element.Attribute("cost")?.Value ?? "0";
        var fifoPnlStr = element.Attribute("fifoPnlRealized")?.Value ?? "0";
        var commissionStr = element.Attribute("ibCommission")?.Value ?? element.Attribute("commission")?.Value ?? "0";

        if (!TryParseDateTime(sellDateStr, out var sellDate))
        {
            _logger.LogWarning("Skipping XML closed lot for {Symbol}: could not parse sell date '{DateStr}'", symbol, sellDateStr);
            return;
        }

        if (!TryParseDateTime(buyDateStr, out var buyDate))
        {
            _logger.LogWarning("Skipping XML closed lot for {Symbol}: could not parse buy date '{DateStr}'", symbol, buyDateStr);
            return;
        }

        if (!TryParseDecimal(quantityStr, out var quantity) || quantity == 0) return;
        if (!TryParseDecimal(costStr, out var totalCost)) return;
        if (!TryParseDecimal(fifoPnlStr, out var fifoPnl)) return;
        TryParseDecimal(commissionStr, out var commission);

        quantity = Math.Abs(quantity);
        totalCost = Math.Abs(totalCost);
        commission = Math.Abs(commission);

        // In CLOSED_LOT: sell proceeds = cost basis + realized P&L
        var sellProceeds = totalCost + fifoPnl;
        var buyPricePerShare = totalCost / quantity;
        var sellPricePerShare = sellProceeds / quantity;

        // Generate synthetic Buy trade (original purchase)
        trades.Add(new Trade(
            Symbol: symbol,
            Currency: currency,
            DateTime: buyDate,
            Quantity: quantity,
            Price: buyPricePerShare,
            Proceeds: 0,
            Commission: 0, // Commission is on the sell side for closed lots
            RealizedPnL: 0,
            Type: TradeType.Buy,
            Description: description));

        // Generate actual Sell trade
        trades.Add(new Trade(
            Symbol: symbol,
            Currency: currency,
            DateTime: sellDate,
            Quantity: quantity,
            Price: sellPricePerShare,
            Proceeds: sellProceeds,
            Commission: commission,
            RealizedPnL: fifoPnl,
            Type: TradeType.Sell,
            Description: description));
    }

    private void TryParseDividend(XElement element, List<RawDividend> dividends)
    {
        var row = TryParseCashTransaction(element, "dividend", skipReversals: true);
        if (row is null) return;
        var (symbol, currency, date, amount) = row.Value;
        dividends.Add(new RawDividend(symbol, currency, date, amount));
    }

    private void TryParseWithholdingTax(XElement element, List<RawWithholdingTax> taxes)
    {
        var row = TryParseCashTransaction(element, "withholding tax", skipReversals: false);
        if (row is null) return;
        var (symbol, currency, date, amount) = row.Value;
        taxes.Add(new RawWithholdingTax(symbol, currency, date, amount));
    }

    private (string Symbol, string Currency, DateTime Date, decimal Amount)? TryParseCashTransaction(
        XElement element, string sectionName, bool skipReversals)
    {
        var levelOfDetail = element.Attribute("levelOfDetail")?.Value ?? "DETAIL";
        if (levelOfDetail != "DETAIL" && levelOfDetail != "Data") return null;

        var description = element.Attribute("description")?.Value ?? string.Empty;
        if (description.Contains("Total", StringComparison.OrdinalIgnoreCase)) return null;
        if (skipReversals && description.Contains("Reversal", StringComparison.OrdinalIgnoreCase)) return null;

        var symbol = element.Attribute("symbol")?.Value ?? ExtractSymbolFromDescription(description);
        if (string.IsNullOrEmpty(symbol))
        {
            _logger.LogWarning("Skipping XML {Section} row: could not determine symbol from attribute or '{Description}'", sectionName, description);
            return null;
        }

        // Use dateTime first: corrections/reversals have a different reportDate
        // but the same dateTime as the original transaction.
        var dateStr = element.Attribute("dateTime")?.Value ?? element.Attribute("reportDate")?.Value ?? element.Attribute("settleDate")?.Value ?? string.Empty;
        var amountStr = element.Attribute("amount")?.Value ?? "0";
        var currency = element.Attribute("currency")?.Value ?? "USD";

        if (!TryParseDate(dateStr, out var date))
        {
            _logger.LogWarning("Skipping XML {Section} row for {Symbol}: could not parse date '{DateStr}'", sectionName, symbol, dateStr);
            return null;
        }

        if (!TryParseDecimal(amountStr, out var amount))
        {
            _logger.LogWarning("Skipping XML {Section} row for {Symbol} on {Date}: could not parse amount '{AmountStr}'", sectionName, symbol, dateStr, amountStr);
            return null;
        }

        if (amount == 0) return null;

        return (symbol, currency, date, amount);
    }

    private static string? ExtractSymbolFromDescription(string description)
    {
        var match = SymbolRegex().Match(description);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^(\w+)\s*\(")]
    private static partial Regex SymbolRegex();

    private static bool TryParseDateTime(string s, out DateTime result)
    {
        // IB XML dates usually come in "yyyyMMdd;HHmmss" or "yyyyMMdd" or "yyyy-MM-dd" FORMAT
        s = s.Split(';')[0].Trim();
        var formats = new[] { "yyyyMMdd", "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd, HH:mm:ss" };
        return DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out result);
    }

    private static bool TryParseDate(string s, out DateTime result)
    {
        s = s.Split(';')[0].Trim();
        var formats = new[] { "yyyyMMdd", "yyyy-MM-dd", "MM/dd/yyyy" };
        return DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out result);
    }

    private static bool TryParseDecimal(string s, out decimal result)
    {
        return decimal.TryParse(s.Replace(",", ""), NumberStyles.Any,
            CultureInfo.InvariantCulture, out result);
    }
}
