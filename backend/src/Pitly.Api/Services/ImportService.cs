using Pitly.Api.Data;
using Pitly.Api.Mapping;
using Pitly.Core.Models;
using Pitly.Core.Parsing;
using Pitly.Core.Tax;

namespace Pitly.Api.Services;

public class ImportService : IImportService
{
    private readonly IStatementParserFactory _parserFactory;
    private readonly ITaxCalculator _calculator;
    private readonly AppDbContext _db;
    private readonly ILogger<ImportService> _logger;

    public ImportService(IStatementParserFactory parserFactory, ITaxCalculator calculator, AppDbContext db, ILogger<ImportService> logger)
    {
        _parserFactory = parserFactory;
        _calculator = calculator;
        _db = db;
        _logger = logger;
    }

    public async Task<ImportResult> ImportStatementAsync(Stream fileStream, string fileName)
    {
        using var reader = new StreamReader(fileStream);
        var content = await reader.ReadToEndAsync();

        _logger.LogInformation("Parsing statement ({Length} chars)", content.Length);
        var parser = _parserFactory.GetParser(fileName);
        var parsed = parser.Parse(content);
        _logger.LogInformation("Parsed {Trades} trades, {Dividends} dividends, {Taxes} withholding taxes",
            parsed.Trades.Count, parsed.Dividends.Count, parsed.WithholdingTaxes.Count);

        var summary = await _calculator.CalculateAsync(parsed);
        _logger.LogInformation("Tax calculation complete for year {Year}: capital gain {Gain} PLN, dividend tax owed {DivTax} PLN",
            summary.Year, summary.CapitalGainPln, summary.DividendTaxOwedPln);

        var session = EntityMapper.ToSessionEntity(summary);
        _db.Sessions.Add(session);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Session {SessionId} saved", session.Id);
        return new ImportResult(session.Id, summary);
    }
}
