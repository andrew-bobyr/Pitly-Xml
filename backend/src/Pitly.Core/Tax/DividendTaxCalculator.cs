using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Pitly.Core.Models;
using Pitly.Core.Services;

namespace Pitly.Core.Tax;

public interface IDividendTaxCalculator
{
    Task<List<Dividend>> CalculateAsync(List<RawDividend> dividends, List<RawWithholdingTax> withholdingTaxes);
}

public class DividendTaxCalculator : IDividendTaxCalculator
{
    private readonly ICurrencyExchangeService _exchangeRateService;

    public DividendTaxCalculator(ICurrencyExchangeService exchangeRateService)
    {
        _exchangeRateService = exchangeRateService;
    }

    public async Task<List<Dividend>> CalculateAsync(
        List<RawDividend> rawDividends,
        List<RawWithholdingTax> rawWithholdingTaxes)
    {
        var results = new List<Dividend>();

        foreach (var div in rawDividends)
        {
            // Sum ALL matching withholding tax entries for the same symbol and date.
            // IB often issues corrections: original charge (-$0.37), reversal (+$0.37),
            // and corrected charge (-$0.18). Summing gives the net tax paid.
            var withholdingAmount = Math.Abs(rawWithholdingTaxes
                .Where(t => t.Symbol == div.Symbol && t.Date == div.Date)
                .Sum(t => t.Amount));

            var rate = await _exchangeRateService.GetRateAsync(div.Currency, div.Date);

            var amountPln = div.Amount * rate;
            var withholdingPln = withholdingAmount * rate;

            results.Add(new Dividend(
                Symbol: div.Symbol,
                Currency: div.Currency,
                Date: div.Date,
                AmountOriginal: div.Amount,
                WithholdingTaxOriginal: withholdingAmount,
                AmountPln: amountPln,
                WithholdingTaxPln: withholdingPln,
                ExchangeRate: rate));
        }

        return results;
    }
}
