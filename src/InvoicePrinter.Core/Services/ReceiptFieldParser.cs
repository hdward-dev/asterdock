using System.Globalization;
using System.Text.RegularExpressions;

namespace InvoicePrinter.Core.Services;

public sealed record ReceiptFields(string? Category, decimal? Amount, string? SerialNumber);

public static partial class ReceiptFieldParser
{
    [GeneratedRegex(@"(?:价税合计|合计金额|支付金额|收款金额|实付金额|应付金额|总金额|总计金额|总计|合计|实付|应付|金额)[^\d¥￥]{0,12}[¥￥]?\s*([0-9][0-9,]*(?:\.[0-9]+)?)")]
    private static partial Regex KeywordAmount();

    [GeneratedRegex(@"[¥￥]\s*([0-9][0-9,]*(?:\.[0-9]+)?)")]
    private static partial Regex CurrencyAmount();

    [GeneratedRegex(@"(?:发票号码|票据号码|票号|凭证号|单号|编号)\s*[:：]?\s*([0-9A-Za-z\-]{4,30})")]
    private static partial Regex KeywordSerial();

    [GeneratedRegex(@"\bNO[.：:#]?\s*([0-9A-Za-z\-]{3,30})", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixSerial();

    [GeneratedRegex(@"类[型别]\s*[:：]\s*([A-Za-z])")]
    private static partial Regex FieldCategory();

    [GeneratedRegex(@"([A-Za-z])\s*类")]
    private static partial Regex SuffixCategory();

    public static ReceiptFields Parse(string text)
    {
        return new ReceiptFields(FindCategory(text), FindAmount(text), FindSerial(text));
    }

    private static string? FindCategory(string text)
    {
        var field = FieldCategory().Match(text);
        if (field.Success) return field.Groups[1].Value.ToUpperInvariant();
        var suffix = SuffixCategory().Match(text);
        return suffix.Success ? suffix.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static decimal? FindAmount(string text)
    {
        var keyword = KeywordAmount().Match(text);
        if (keyword.Success && TryParseAmount(keyword.Groups[1].Value, out var amount)) return amount;

        var found = false;
        decimal best = 0;
        foreach (Match match in CurrencyAmount().Matches(text))
        {
            if (!TryParseAmount(match.Groups[1].Value, out var value)) continue;
            if (!found || value > best) best = value;
            found = true;
        }
        return found ? best : null;
    }

    private static string? FindSerial(string text)
    {
        var keyword = KeywordSerial().Match(text);
        if (keyword.Success) return keyword.Groups[1].Value;
        var prefix = PrefixSerial().Match(text);
        return prefix.Success ? prefix.Groups[1].Value : null;
    }

    private static bool TryParseAmount(string candidate, out decimal amount)
    {
        if (decimal.TryParse(candidate.Replace(",", string.Empty).Replace("，", string.Empty), NumberStyles.Number, CultureInfo.InvariantCulture, out amount) && amount > 0)
            return true;
        amount = 0;
        return false;
    }
}
