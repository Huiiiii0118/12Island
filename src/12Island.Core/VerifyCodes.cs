using System.Text.RegularExpressions;

namespace TwelveIsland.Core;

// The one-time code a notice carries. Banks, carriers and websites send it in a mail or a message, and it is the one thing the
// reader needs from that notice: the capsule shows the digits on their own rather than the sender and a count, so there is
// nothing to open and nothing to copy out of a line that has been shortened.
public static class VerifyCodes
{
    // The words a notice puts around a code. The digits must sit close to one of them, so an order number, an amount or a
    // phone number in the same message is not read as a code. English words are matched whole, so "postcode" is not "code".
    private static readonly string[] Chinese =
        ["验证码", "校验码", "动态码", "确认码", "安全码", "动态密码", "激活码", "口令", "验证码为", "验证码是"];

    // How far from the word its digits may sit, in characters: "您的验证码是 823914，5 分钟内有效" is well inside this.
    private const int Reach = 28;

    // The digits before or after any of the words, and nothing when the notice carries none.
    private static readonly Regex Digits = new(@"(?<!\d)\d{4,8}(?!\d)", RegexOptions.CultureInvariant);
    private static readonly Regex Word = new(@"\b(?:verification|verify|confirmation|security|one-time|one time)?\s*(?:code|password|passcode|otp)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The code in a notice, given what it calls its sender and what it says.
    public static string? Of(string title, string body)
    {
        string text = $"{title} {body}".Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length == 0) return null;
        // A notice that is nothing but the digits (a code arriving on its own) needs no wording to be read as one.
        string bare = text.Trim(' ', '.', ',', ';', ':', '!', '?', '。', '，', '；', '：', '！', '？', '、');
        if (bare.Length is >= 4 and <= 8 && bare.All(char.IsAsciiDigit)) return bare;
        foreach (Match match in Digits.Matches(text))
            if (Near(text, match.Index, match.Length)) return match.Value;
        return null;
    }

    // Whether one of the words stands within Reach of the digits that start at index.
    private static bool Near(string text, int index, int length)
    {
        int from = Math.Max(0, index - Reach), to = Math.Min(text.Length, index + length + Reach);
        string window = text[from..to];
        foreach (var word in Chinese)
            if (window.Contains(word, StringComparison.Ordinal)) return true;
        return Word.IsMatch(window);
    }
}
