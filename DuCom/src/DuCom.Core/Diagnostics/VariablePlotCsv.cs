using System.Globalization;
using System.Text;

namespace DuCom.Core.Diagnostics;

public static class VariablePlotCsv
{
    public static string ToLongTable(VariablePlotSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        StringBuilder builder = new("TimestampUtc,TimestampLocal,RuleId,Series,Port,Value,Unit,MatchCount\r\n");
        foreach (VariableSeriesSnapshot series in snapshot.Series)
        {
            foreach (VariableNumericSample sample in series.Samples)
            {
                Append(builder, sample.SampledAtUtc.ToString("O", CultureInfo.InvariantCulture));
                Append(builder, sample.SampledAtUtc.ToLocalTime().ToString("O", CultureInfo.InvariantCulture));
                Append(builder, sample.RuleId.ToString("D", CultureInfo.InvariantCulture));
                Append(builder, series.Rule.Name);
                Append(builder, sample.PortName ?? string.Empty);
                Append(builder, sample.NumericValue.ToString("R", CultureInfo.InvariantCulture));
                Append(builder, series.Rule.Unit ?? string.Empty);
                builder.Append(sample.MatchCount.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n'))
        {
            builder.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }
        else
        {
            builder.Append(value);
        }

        builder.Append(',');
    }
}
