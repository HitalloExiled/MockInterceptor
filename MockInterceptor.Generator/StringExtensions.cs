using System.Text.RegularExpressions;

namespace MockInterceptor.Generator;

internal static class StringExtensions
{
    private static readonly Regex newLineRegex = new(@"\r\n?|\n|\u0085|\u2028|\u2029|\u000C", RegexOptions.Compiled);

    public static string ReplaceLineEndings(this string value, string replacementText) =>
        string.IsNullOrEmpty(value) ? value : newLineRegex.Replace(value, replacementText);
}

internal static class SpanExtensions
{
    public static bool SequenceEqual<T>(this ReadOnlySpan<T> source, ReadOnlySpan<T> other) where T : IEquatable<T>
    {
        if (source.Length != other.Length)
        {
            return false;
        }

        for (var i = 0; i < source.Length; i++)
        {
            if (!source[i].Equals(other[i]))
            {
                return false;
            }
        }

        return true;
    }
}
