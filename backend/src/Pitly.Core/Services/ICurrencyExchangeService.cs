namespace Pitly.Core.Services;

public interface ICurrencyExchangeService
{
    Task<decimal> GetRateAsync(string currency, DateTime transactionDate);
}
