using System;
using System.IO;
using Pitly.Broker.InteractiveBrokers;

using Pitly.Core.Parsing;

namespace Pitly.Broker.InteractiveBrokers;

public class StatementParserFactory : IStatementParserFactory
{
    private readonly InteractiveBrokersStatementParser _csvParser;
    private readonly InteractiveBrokersXmlStatementParser _xmlParser;

    public StatementParserFactory(
        InteractiveBrokersStatementParser csvParser,
        InteractiveBrokersXmlStatementParser xmlParser)
    {
        _csvParser = csvParser;
        _xmlParser = xmlParser;
    }

    public IStatementParser GetParser(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            // Default to CSV if no file name is found (should not happen in proper usage)
            return _csvParser;
        }

        var extension = Path.GetExtension(fileName);
        
        if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return _csvParser;
        }
        
        if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return _xmlParser;
        }

        throw new NotSupportedException($"File extension '{extension}' is not supported. Please upload a .csv or .xml file.");
    }
}
