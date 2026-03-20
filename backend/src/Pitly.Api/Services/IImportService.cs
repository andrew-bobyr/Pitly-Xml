using Pitly.Core.Models;

namespace Pitly.Api.Services;

public interface IImportService
{
    Task<ImportResult> ImportStatementAsync(Stream fileStream, string fileName);
}

public record ImportResult(Guid SessionId, TaxSummary Summary);
