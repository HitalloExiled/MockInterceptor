using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace MockInterceptor.Generator.Tests;

public static partial class Extensions
{
    [GeneratedRegex("(\\[global::System\\.Runtime\\.CompilerServices\\.InterceptsLocationAttribute\\(1, \")([^\"]+)(\"\\)\\])")]
    private static partial Regex CreateInterceptionPattern();

    private static readonly Regex interceptionPattern = CreateInterceptionPattern();

    extension(System.Collections.Immutable.ImmutableArray<SyntaxTree> syntaxes)
    {
        public SourceGenerated[] GetAllEntries() =>
            [.. syntaxes.Select(x => new SourceGenerated(Path.GetFileName(x.FilePath), x.GetText().ToString()))];

        public SourceGenerated[] GetInterceptors() =>
            [.. syntaxes.Skip(3).Select(x => new SourceGenerated(Path.GetFileName(x.FilePath), x.GetText().ToString()))];
    }

    extension(string value)
    {
        public string SanitizeInterceptionData() =>
            interceptionPattern.Replace(value.ReplaceLineEndings(Environment.NewLine), "$1...$3");
    }
}
