using System.Security.Cryptography;
using System.Text;

namespace Vantage.Infrastructure.Parsing;

/// <summary>
/// Content identity for change detection. Only a byte-order mark and line-ending style are
/// normalized away — any substantive edit must still change the hash.
/// </summary>
public static class SemanticHash
{
    public static string Compute(string content)
    {
        var normalized = Normalize(content);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(bytes);
    }

    public static string Normalize(string content)
    {
        var text = content;
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    continue;
                }

                builder.Append('\n');
                continue;
            }

            builder.Append(text[i]);
        }

        return builder.ToString();
    }
}
