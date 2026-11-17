using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DatabaseBuilder.Services
{
    public static class Slug
    {
        public static string From(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s.Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);

            var t = sb.ToString().ToLowerInvariant()
                .Replace("ł", "l").Replace("đ", "d").Replace("ø", "o").Replace("ß", "ss")
                .Replace("æ", "ae").Replace("œ", "oe").Replace("ı", "i").Replace("ð", "d");
            return Regex.Replace(t, @"[^a-z0-9]+", "-").Trim('-');
        }
    }
}