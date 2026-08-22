using System.Globalization;
using System.Text;

namespace BackdropForCodex.Core.Media;

internal static class WallpaperDisplayNameSanitizer
{
    public static string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length);
        var separatorPending = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || IsUnsafeFormattingCharacter(character))
            {
                separatorPending = builder.Length != 0;
                continue;
            }

            if (separatorPending)
            {
                builder.Append(' ');
                separatorPending = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static bool IsUnsafeFormattingCharacter(char character)
    {
        var category = CharUnicodeInfo.GetUnicodeCategory(character);
        return category == UnicodeCategory.Control ||
            (category == UnicodeCategory.Format && character is not '\u200c' and not '\u200d');
    }
}
