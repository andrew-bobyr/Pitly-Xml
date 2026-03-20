namespace Pitly.Core.Parsing;

public interface IStatementParserFactory
{
    IStatementParser GetParser(string fileName);
}
