using System.Security.Cryptography;
using System.Text;

namespace Backup.Shared.Validation;

public static class ShareCodeHelper
{
    // 31 unambiguous alphanumeric characters (omitting 0, O, 1, I, L)
    public const string AllowedAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int RawLength = 8;

    private static readonly HashSet<char> AllowedChars = new(AllowedAlphabet);

    /// <summary>
    /// Generates a cryptographically secure, human-readable share code in XXXX-XXXX format.
    /// </summary>
    public static string Generate()
    {
        var raw = new char[RawLength];
        for (int i = 0; i < RawLength; i++)
        {
            int idx = RandomNumberGenerator.GetInt32(AllowedAlphabet.Length);
            raw[i] = AllowedAlphabet[idx];
        }

        return $"{new string(raw, 0, 4)}-{new string(raw, 4, 4)}";
    }

    /// <summary>
    /// Validates and normalizes user input into canonical XXXX-XXXX uppercase format.
    /// Accepts inputs with or without hyphens and spaces (e.g., "7H4K92QX", "7h4k-92qx", "  7H4K - 92QX  ").
    /// </summary>
    public static bool TryNormalize(string? input, out string normalized, out string? errorMessage)
    {
        normalized = string.Empty;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            errorMessage = "ShareCode cannot be empty.";
            return false;
        }

        var sb = new StringBuilder(RawLength);
        foreach (char c in input)
        {
            if (char.IsWhiteSpace(c) || c == '-')
            {
                continue;
            }

            char upper = char.ToUpperInvariant(c);
            if (!AllowedChars.Contains(upper))
            {
                errorMessage = $"ShareCode contains invalid character '{c}'. Allowed characters: {AllowedAlphabet}";
                return false;
            }

            sb.Append(upper);
        }

        if (sb.Length != RawLength)
        {
            errorMessage = $"ShareCode must contain exactly {RawLength} characters (excluding hyphens/spaces), got {sb.Length}.";
            return false;
        }

        string rawClean = sb.ToString();
        normalized = $"{rawClean.Substring(0, 4)}-{rawClean.Substring(4, 4)}";
        return true;
    }

    /// <summary>
    /// Checks if a string is a valid normalized share code.
    /// </summary>
    public static bool IsValid(string? input)
    {
        return TryNormalize(input, out _, out _);
    }
}
