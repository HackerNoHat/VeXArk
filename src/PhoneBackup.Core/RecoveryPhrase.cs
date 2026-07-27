using System.Security.Cryptography;

namespace PhoneBackup.Core;

/// <summary>
/// Encodes 256 bits plus an 8-bit checksum as 24 deterministic 11-bit words.
/// The syllabic word list is generated in code, avoiding a hidden external dictionary.
/// </summary>
public static class RecoveryPhrase
{
    private static readonly string[] Prefixes =
    [
        "ba", "be", "bi", "bo", "bu", "da", "de", "di",
        "do", "du", "fa", "fe", "fi", "fo", "fu", "ga",
        "ge", "gi", "go", "gu", "ka", "ke", "ki", "ko",
        "ku", "la", "le", "li", "lo", "lu", "ma", "me"
    ];

    private static readonly string[] Suffixes =
    [
        "ban", "bel", "bin", "bor", "bun", "dan", "del", "din",
        "dor", "dun", "fan", "fel", "fin", "for", "fun", "gan",
        "gel", "gin", "gor", "gun", "kan", "kel", "kin", "kor",
        "kun", "lan", "lel", "lin", "lor", "lun", "man", "mel",
        "min", "mor", "mun", "nan", "nel", "nin", "nor", "nun",
        "pan", "pel", "pin", "por", "pun", "ran", "rel", "rin",
        "ror", "run", "san", "sel", "sin", "sor", "sun", "tan",
        "tel", "tin", "tor", "tun", "van", "vel", "vin", "vor"
    ];

    public static string Encode(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32) throw new ArgumentOutOfRangeException(nameof(key));
        Span<byte> payload = stackalloc byte[33];
        key.CopyTo(payload);
        payload[32] = SHA256.HashData(key)[0];
        var words = new string[24];
        var accumulator = 0;
        var bits = 0;
        var offset = 0;
        foreach (var value in payload)
        {
            accumulator = (accumulator << 8) | value;
            bits += 8;
            while (bits >= 11)
            {
                bits -= 11;
                words[offset++] = Word((accumulator >> bits) & 0x7ff);
                accumulator &= (1 << bits) - 1;
            }
        }
        return string.Join(' ', words);
    }

    public static byte[] Decode(string phrase)
    {
        var words = phrase.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != 24)
            throw new CryptographicException("Recovery key must contain exactly 24 words.");
        var payload = new byte[33];
        var accumulator = 0;
        var bits = 0;
        var offset = 0;
        foreach (var word in words)
        {
            var value = Index(word);
            if (value < 0) throw new CryptographicException($"Unknown recovery word: {word}");
            accumulator = (accumulator << 11) | value;
            bits += 11;
            while (bits >= 8)
            {
                bits -= 8;
                payload[offset++] = (byte)(accumulator >> bits);
                accumulator &= (1 << bits) - 1;
            }
        }
        var key = payload[..32];
        if (payload[32] != SHA256.HashData(key)[0])
            throw new CryptographicException("Recovery key checksum is invalid.");
        return key;
    }

    private static string Word(int index) => Prefixes[index >> 6] + Suffixes[index & 63];

    private static int Index(string word)
    {
        for (var prefix = 0; prefix < Prefixes.Length; prefix++)
        {
            if (!word.StartsWith(Prefixes[prefix], StringComparison.Ordinal)) continue;
            var suffixText = word[Prefixes[prefix].Length..];
            for (var suffix = 0; suffix < Suffixes.Length; suffix++)
                if (string.Equals(suffixText, Suffixes[suffix], StringComparison.Ordinal))
                    return (prefix << 6) | suffix;
        }
        return -1;
    }
}
