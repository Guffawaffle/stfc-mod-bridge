using System.Text;
using System.Text.RegularExpressions;

namespace STFCCommunityMod.Launcher.Core;

/// <summary>
/// A deliberately small, source-preserving TOML editor. It understands enough TOML
/// structure to edit ordinary dotted keys without reserializing the rest of the file.
/// </summary>
public sealed partial class SparseTomlDocument
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] Utf8Bom = [0xef, 0xbb, 0xbf];

    private readonly string text;
    private readonly bool hasBom;
    private readonly byte[] originalContents;

    private SparseTomlDocument(string text, bool hasBom, byte[] originalContents)
    {
        this.text = text;
        this.hasBom = hasBom;
        this.originalContents = originalContents;
    }

    public static SparseTomlEditResult Load(
        byte[] contents,
        out SparseTomlDocument? document)
    {
        ArgumentNullException.ThrowIfNull(contents);
        document = null;

        var hasBom = contents.AsSpan().StartsWith(Utf8Bom);
        var body = hasBom ? contents.AsSpan(Utf8Bom.Length) : contents.AsSpan();
        try
        {
            var text = StrictUtf8.GetString(body);
            if (text.Contains('\0', StringComparison.Ordinal))
            {
                return SparseTomlEditResult.Invalid(
                    new(
                        SparseTomlErrorCode.UnsupportedDocument,
                        "The configuration contains a NUL character and cannot be edited safely."));
            }

            document = new(text, hasBom, [.. contents]);
            return SparseTomlEditResult.Unchanged([.. contents]);
        }
        catch (DecoderFallbackException exception)
        {
            return SparseTomlEditResult.Invalid(
                new(
                    SparseTomlErrorCode.InvalidUtf8,
                    $"The configuration is not valid UTF-8: {exception.Message}"));
        }
    }

    public SparseTomlEditResult ValidateForMutation()
    {
        var analysis = Analyze(targetPath: null);
        return analysis.Error is null
            ? SparseTomlEditResult.Unchanged([.. originalContents])
            : SparseTomlEditResult.Invalid(analysis.Error);
    }

    public SparseTomlEditResult SetOverride(string canonicalPath, string renderedTomlValue)
    {
        var pathResult = ParseCanonicalPath(canonicalPath);
        if (pathResult.Error is not null)
        {
            return SparseTomlEditResult.Invalid(pathResult.Error);
        }

        var valueError = ValidateRenderedValue(renderedTomlValue);
        if (valueError is not null)
        {
            return SparseTomlEditResult.Invalid(valueError);
        }

        var path = pathResult.Segments!;
        var analysis = Analyze(path);
        if (analysis.Error is not null)
        {
            return SparseTomlEditResult.Invalid(analysis.Error);
        }

        if (analysis.TargetAssignments.Count > 1)
        {
            return SparseTomlEditResult.Invalid(
                new(
                    SparseTomlErrorCode.DuplicateTarget,
                    $"'{canonicalPath}' is assigned more than once; no change was written.",
                    analysis.TargetAssignments[1].Line.Number));
        }

        if (analysis.TargetAssignments.Count == 1)
        {
            var assignment = analysis.TargetAssignments[0];
            var updatedText = string.Concat(
                text.AsSpan(0, assignment.ValueStart),
                renderedTomlValue,
                text.AsSpan(assignment.ValueEnd));
            return BuildResult(updatedText);
        }

        var namespaceError = FindNamespaceCollision(path, analysis);
        if (namespaceError is not null)
        {
            return SparseTomlEditResult.Invalid(namespaceError);
        }

        var lineEnding = DetectLineEnding();
        var key = path[^1];
        var tablePath = path[..^1];
        var assignmentText = $"{key} = {renderedTomlValue}";
        string appendedText;

        if (tablePath.Length == 0)
        {
            var firstHeader = analysis.Sections.FirstOrDefault();
            if (firstHeader is null)
            {
                appendedText = AppendLine(text, assignmentText, lineEnding);
            }
            else
            {
                appendedText = text.Insert(
                    firstHeader.HeaderLine.Start,
                    assignmentText + lineEnding);
            }
        }
        else
        {
            var matchingSections = analysis.Sections
                .Where(section => section.Path.SequenceEqual(tablePath, StringComparer.Ordinal))
                .ToArray();
            if (matchingSections.Length > 1)
            {
                return SparseTomlEditResult.Invalid(
                    new(
                        SparseTomlErrorCode.DuplicateTarget,
                        $"Table '[{string.Join('.', tablePath)}]' is declared more than once; no change was written.",
                        matchingSections[1].HeaderLine.Number));
            }

            if (matchingSections.Length == 1)
            {
                var section = matchingSections[0];
                appendedText = InsertLine(text, section.End, assignmentText, lineEnding);
            }
            else
            {
                var tableCollision = FindNewTableDeclarationCollision(tablePath, analysis);
                if (tableCollision is not null)
                {
                    return SparseTomlEditResult.Invalid(tableCollision);
                }

                var newSection = $"[{string.Join('.', tablePath)}]{lineEnding}{assignmentText}";
                appendedText = AppendBlock(text, newSection, lineEnding);
            }
        }

        return BuildResult(appendedText);
    }

    public SparseTomlEditResult RemoveOverride(string canonicalPath)
    {
        var pathResult = ParseCanonicalPath(canonicalPath);
        if (pathResult.Error is not null)
        {
            return SparseTomlEditResult.Invalid(pathResult.Error);
        }

        var analysis = Analyze(pathResult.Segments);
        if (analysis.Error is not null)
        {
            return SparseTomlEditResult.Invalid(analysis.Error);
        }

        if (analysis.TargetAssignments.Count > 1)
        {
            return SparseTomlEditResult.Invalid(
                new(
                    SparseTomlErrorCode.DuplicateTarget,
                    $"'{canonicalPath}' is assigned more than once; no change was written.",
                    analysis.TargetAssignments[1].Line.Number));
        }

        if (analysis.TargetAssignments.Count == 0)
        {
            return SparseTomlEditResult.Unchanged([.. originalContents]);
        }

        var line = analysis.TargetAssignments[0].Line;
        var updatedText = text.Remove(line.Start, line.End - line.Start);
        return BuildResult(updatedText);
    }

    private Analysis Analyze(string[]? targetPath)
    {
        var lines = SplitLines();
        var assignments = new List<Assignment>();
        var allAssignments = new List<Assignment>();
        var sections = new List<Section>();
        var currentTable = Array.Empty<string>();
        var continuation = ValueScanState.Start;
        var continuationActive = false;
        var continuationLine = 0;

        foreach (var line in lines)
        {
            if (continuationActive)
            {
                continuation = ScanValue(line.Content, continuation);
                if (continuation.IsInvalid)
                {
                    return Analysis.Invalid(
                        new(
                            SparseTomlErrorCode.UnsupportedDocument,
                            "A multiline TOML value has unbalanced containers.",
                            continuationLine));
                }

                if (!continuation.IsComplete)
                {
                    continue;
                }

                continuationActive = false;
                continue;
            }

            var trimmed = line.Content.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            if (trimmed.StartsWith("[[", StringComparison.Ordinal))
            {
                return Analysis.Invalid(
                    new(
                        SparseTomlErrorCode.UnsupportedDocument,
                        "Array-of-table syntax is intentionally unsupported by the source-preserving editor.",
                        line.Number));
            }

            if (trimmed[0] == '[')
            {
                var match = SimpleTableHeaderRegex().Match(line.Content);
                if (!match.Success)
                {
                    return Analysis.Invalid(
                        new(
                            SparseTomlErrorCode.UnsupportedDocument,
                            "A table header uses quoted, malformed, or otherwise unsupported syntax.",
                            line.Number));
                }

                currentTable = match.Groups["path"].Value.Split('.');
                if (sections.Count > 0)
                {
                    sections[^1].End = line.Start;
                }

                sections.Add(new(currentTable, line, text.Length));
                continue;
            }

            var equalsIndex = FindAssignmentEquals(line.Content);
            if (equalsIndex < 0)
            {
                return Analysis.Invalid(
                    new(
                        SparseTomlErrorCode.UnsupportedDocument,
                        "A non-comment TOML statement is not a recognizable assignment or table header.",
                        line.Number));
            }

            var left = line.Content[..equalsIndex].Trim();
            if (!TryParseBareDottedPath(left, out var keyPath))
            {
                if (targetPath is not null
                    && left.Contains(targetPath[^1], StringComparison.Ordinal))
                {
                    return Analysis.Invalid(
                        new(
                            SparseTomlErrorCode.UnsupportedTarget,
                            $"A possible assignment for '{string.Join('.', targetPath)}' uses unsupported key syntax.",
                            line.Number));
                }

                return Analysis.Invalid(
                    new(
                        SparseTomlErrorCode.UnsupportedDocument,
                        ConservativeKeyRegex().IsMatch(left)
                            ? "Quoted TOML keys cannot be mapped safely by the source-preserving editor."
                            : "An assignment uses malformed or unsupported key syntax.",
                        line.Number));
            }

            var fullPath = currentTable.Concat(keyPath).ToArray();
            var valueStart = equalsIndex + 1;
            while (valueStart < line.Content.Length && char.IsWhiteSpace(line.Content[valueStart]))
            {
                valueStart++;
            }

            var scan = ScanValue(line.Content[valueStart..], ValueScanState.Start);
            if (scan.IsInvalid)
            {
                return Analysis.Invalid(
                    new(
                        SparseTomlErrorCode.UnsupportedDocument,
                        "An assignment has unbalanced TOML containers.",
                        line.Number));
            }

            if (!scan.IsComplete)
            {
                if (scan.Quote != '\0' && !scan.Triple)
                {
                    return Analysis.Invalid(
                        new(
                            targetPath is not null
                                && fullPath.SequenceEqual(targetPath, StringComparer.Ordinal)
                                    ? SparseTomlErrorCode.UnsupportedTarget
                                    : SparseTomlErrorCode.UnsupportedDocument,
                            "A single-line TOML string is unterminated.",
                            line.Number));
                }

                if (targetPath is not null
                    && fullPath.SequenceEqual(targetPath, StringComparer.Ordinal))
                {
                    return Analysis.Invalid(
                        new(
                            SparseTomlErrorCode.UnsupportedTarget,
                            $"'{string.Join('.', targetPath)}' uses a multiline value that cannot be edited safely.",
                            line.Number));
                }

                continuation = scan;
                continuationActive = true;
                continuationLine = line.Number;
                continue;
            }

            if (scan.ValueEnd < 0)
            {
                return Analysis.Invalid(
                    new(
                        SparseTomlErrorCode.UnsupportedDocument,
                        "An assignment has no readable TOML value.",
                        line.Number));
            }

            var rawValue = line.Content[valueStart..(valueStart + scan.ValueEnd)];
            if (!IsConservativeSingleLineValue(rawValue))
            {
                return Analysis.Invalid(
                    new(
                        targetPath is not null
                            && fullPath.SequenceEqual(targetPath, StringComparer.Ordinal)
                                ? SparseTomlErrorCode.UnsupportedTarget
                                : SparseTomlErrorCode.UnsupportedDocument,
                        "An assignment value is not in the conservative TOML grammar supported by the editor.",
                        line.Number));
            }

            if (targetPath is not null
                && fullPath.SequenceEqual(targetPath, StringComparer.Ordinal))
            {
                assignments.Add(new(
                    fullPath,
                    line,
                    line.Start + valueStart,
                    line.Start + valueStart + scan.ValueEnd));
            }

            allAssignments.Add(new(
                fullPath,
                line,
                line.Start + valueStart,
                line.Start + valueStart + scan.ValueEnd));
        }

        if (continuationActive)
        {
            return Analysis.Invalid(
                new(
                    SparseTomlErrorCode.UnsupportedDocument,
                    "A multiline TOML value is unterminated.",
                    continuationLine));
        }

        var duplicate = allAssignments
            .GroupBy(assignment => string.Join('\0', assignment.Path), StringComparer.Ordinal)
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicate is not null)
        {
            var duplicateAssignment = duplicate.Skip(1).First();
            return Analysis.Invalid(
                new(
                    SparseTomlErrorCode.DuplicateTarget,
                    $"'{string.Join('.', duplicateAssignment.Path)}' is assigned more than once.",
                    duplicateAssignment.Line.Number));
        }

        var duplicateSection = sections
            .GroupBy(section => string.Join('\0', section.Path), StringComparer.Ordinal)
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicateSection is not null)
        {
            var repeatedSection = duplicateSection.Skip(1).First();
            return Analysis.Invalid(
                new(
                    SparseTomlErrorCode.UnsupportedDocument,
                    $"Table '[{string.Join('.', repeatedSection.Path)}]' is declared more than once.",
                    repeatedSection.HeaderLine.Number));
        }

        var existingCollision = FindExistingNamespaceCollision(allAssignments, sections);
        return existingCollision is null
            ? new(assignments, allAssignments, sections, null)
            : Analysis.Invalid(existingCollision);
    }

    private static SparseTomlError? FindNamespaceCollision(
        string[] targetPath,
        Analysis analysis)
    {
        foreach (var assignment in analysis.AllAssignments)
        {
            if (IsStrictPrefix(assignment.Path, targetPath))
            {
                return new(
                    SparseTomlErrorCode.UnsupportedTarget,
                    $"Cannot add '{string.Join('.', targetPath)}' because '{string.Join('.', assignment.Path)}' is already a scalar value.",
                    assignment.Line.Number);
            }

            if (IsStrictPrefix(targetPath, assignment.Path))
            {
                return new(
                    SparseTomlErrorCode.UnsupportedTarget,
                    $"Cannot add '{string.Join('.', targetPath)}' because that path is already a namespace containing '{string.Join('.', assignment.Path)}'.",
                    assignment.Line.Number);
            }
        }

        foreach (var section in analysis.Sections)
        {
            if (IsPrefix(targetPath, section.Path))
            {
                return new(
                    SparseTomlErrorCode.UnsupportedTarget,
                    $"Cannot add '{string.Join('.', targetPath)}' because that path is already the table namespace '[{string.Join('.', section.Path)}]'.",
                    section.HeaderLine.Number);
            }
        }

        return null;
    }

    private static SparseTomlError? FindNewTableDeclarationCollision(
        string[] tablePath,
        Analysis analysis)
    {
        foreach (var assignment in analysis.AllAssignments)
        {
            if (IsPrefix(tablePath, assignment.Path))
            {
                return new(
                    SparseTomlErrorCode.UnsupportedTarget,
                    $"Table '[{string.Join('.', tablePath)}]' already exists implicitly through '{string.Join('.', assignment.Path)}'; the editor will not redeclare it.",
                    assignment.Line.Number);
            }
        }

        foreach (var section in analysis.Sections)
        {
            if (IsPrefix(tablePath, section.Path))
            {
                return new(
                    SparseTomlErrorCode.UnsupportedTarget,
                    $"Table '[{string.Join('.', tablePath)}]' already exists implicitly through '[{string.Join('.', section.Path)}]'; the editor will not redeclare it.",
                    section.HeaderLine.Number);
            }
        }

        return null;
    }

    private static SparseTomlError? FindExistingNamespaceCollision(
        List<Assignment> assignments,
        List<Section> sections)
    {
        for (var firstIndex = 0; firstIndex < assignments.Count; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < assignments.Count; secondIndex++)
            {
                var first = assignments[firstIndex];
                var second = assignments[secondIndex];
                if (IsStrictPrefix(first.Path, second.Path)
                    || IsStrictPrefix(second.Path, first.Path))
                {
                    return new(
                        SparseTomlErrorCode.UnsupportedDocument,
                        $"Scalar assignments '{string.Join('.', first.Path)}' and '{string.Join('.', second.Path)}' have a namespace collision.",
                        second.Line.Number);
                }
            }
        }

        foreach (var assignment in assignments)
        {
            foreach (var section in sections)
            {
                if (IsPrefix(assignment.Path, section.Path))
                {
                    return new(
                        SparseTomlErrorCode.UnsupportedDocument,
                        $"Scalar assignment '{string.Join('.', assignment.Path)}' collides with table '[{string.Join('.', section.Path)}]'.",
                        section.HeaderLine.Number);
                }
            }
        }

        return null;
    }

    private static bool IsPrefix(string[] prefix, string[] path) =>
        prefix.Length <= path.Length
        && prefix.SequenceEqual(path[..prefix.Length], StringComparer.Ordinal);

    private static bool IsStrictPrefix(string[] prefix, string[] path) =>
        prefix.Length < path.Length && IsPrefix(prefix, path);

    private SparseTomlEditResult BuildResult(string updatedText)
    {
        var body = StrictUtf8.GetBytes(updatedText);
        var updatedContents = hasBom ? Utf8Bom.Concat(body).ToArray() : body;
        return updatedContents.AsSpan().SequenceEqual(originalContents)
            ? SparseTomlEditResult.Unchanged(updatedContents)
            : SparseTomlEditResult.Updated(updatedContents);
    }

    private static PathParseResult ParseCanonicalPath(string canonicalPath)
    {
        if (string.IsNullOrWhiteSpace(canonicalPath)
            || !TryParseBareDottedPath(canonicalPath, out var segments))
        {
            return new(
                null,
                new(
                    SparseTomlErrorCode.InvalidPath,
                    "A canonical path must contain only dot-separated bare TOML keys."));
        }

        return new(segments, null);
    }

    private static SparseTomlError? ValidateRenderedValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.IndexOfAny(['\r', '\n']) >= 0)
        {
            return new(
                SparseTomlErrorCode.InvalidValue,
                "A rendered TOML value must be a non-empty, single-line value.");
        }

        var scan = ScanValue(value, ValueScanState.Start);
        if (scan.IsInvalid || !scan.IsComplete || scan.ValueEnd < 0)
        {
            return new(
                SparseTomlErrorCode.InvalidValue,
                "The rendered TOML value has unbalanced quotes or containers.");
        }

        if (!IsConservativeSingleLineValue(value[..scan.ValueEnd]))
        {
            return new(
                SparseTomlErrorCode.InvalidValue,
                "The rendered value is outside the conservative TOML value grammar.");
        }

        var trailing = value[scan.ValueEnd..].TrimStart();
        if (trailing.Length > 0)
        {
            return new(
                SparseTomlErrorCode.InvalidValue,
                "A rendered TOML value cannot include a trailing comment or extra tokens.");
        }

        return null;
    }

    private static bool IsConservativeSingleLineValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed[0] == '"')
        {
            return trimmed.Length >= 2 && trimmed[^1] == '"';
        }

        if (trimmed[0] == '\'')
        {
            return trimmed.Length >= 2 && trimmed[^1] == '\'';
        }

        if (trimmed[0] == '[')
        {
            return trimmed[^1] == ']';
        }

        if (trimmed[0] == '{')
        {
            return trimmed[^1] == '}';
        }

        return ConservativeScalarRegex().IsMatch(trimmed);
    }

    private static bool TryParseBareDottedPath(string value, out string[] segments)
    {
        segments = value.Split('.');
        return segments.Length > 0
            && segments.All(segment =>
                segment.Length > 0
                && segment.All(character =>
                    char.IsAsciiLetterOrDigit(character)
                    || character is '_' or '-'));
    }

    private static int FindAssignmentEquals(string line)
    {
        var quote = '\0';
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quote != '\0')
            {
                if (quote == '"' && !escaped && character == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (!escaped && character == quote)
                {
                    quote = '\0';
                }

                escaped = false;
                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
            }
            else if (character == '#')
            {
                return -1;
            }
            else if (character == '=')
            {
                return index;
            }
        }

        return -1;
    }

    private static ValueScanState ScanValue(string value, ValueScanState state)
    {
        var quote = state.Quote;
        var triple = state.Triple;
        var escaped = state.Escaped;
        var squareDepth = state.SquareDepth;
        var braceDepth = state.BraceDepth;
        var sawContent = state.SawContent;
        var valueEnd = -1;

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote != '\0')
            {
                if (triple
                    && index + 2 < value.Length
                    && value[index] == quote
                    && value[index + 1] == quote
                    && value[index + 2] == quote
                    && !(quote == '"' && escaped))
                {
                    quote = '\0';
                    triple = false;
                    index += 2;
                    valueEnd = index + 1;
                    continue;
                }

                if (!triple && quote == '"' && !escaped && character == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (!triple && !escaped && character == quote)
                {
                    quote = '\0';
                    valueEnd = index + 1;
                }

                escaped = false;
                continue;
            }

            if (character == '#')
            {
                break;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                triple = index + 2 < value.Length
                    && value[index + 1] == character
                    && value[index + 2] == character;
                if (triple)
                {
                    index += 2;
                }

                sawContent = true;
                valueEnd = index + 1;
                continue;
            }

            if (character == '[')
            {
                squareDepth++;
            }
            else if (character == ']')
            {
                squareDepth--;
                if (squareDepth < 0)
                {
                    return ValueScanState.Invalid;
                }
            }
            else if (character == '{')
            {
                braceDepth++;
            }
            else if (character == '}')
            {
                braceDepth--;
                if (braceDepth < 0)
                {
                    return ValueScanState.Invalid;
                }
            }

            if (!char.IsWhiteSpace(character))
            {
                sawContent = true;
                valueEnd = index + 1;
            }
        }

        return new(quote, triple, escaped, squareDepth, braceDepth, sawContent, valueEnd);
    }

    private List<PhysicalLine> SplitLines()
    {
        var lines = new List<PhysicalLine>();
        var start = 0;
        var number = 1;
        while (start < text.Length)
        {
            var index = start;
            while (index < text.Length && text[index] is not '\r' and not '\n')
            {
                index++;
            }

            var contentEnd = index;
            if (index < text.Length && text[index] == '\r')
            {
                index++;
                if (index < text.Length && text[index] == '\n')
                {
                    index++;
                }
            }
            else if (index < text.Length)
            {
                index++;
            }

            lines.Add(new(number++, start, contentEnd, index, text[start..contentEnd]));
            start = index;
        }

        if (text.Length == 0)
        {
            lines.Add(new(1, 0, 0, 0, string.Empty));
        }

        return lines;
    }

    private string DetectLineEnding()
    {
        var newline = text.IndexOfAny(['\r', '\n']);
        if (newline < 0)
        {
            return Environment.NewLine;
        }

        return text[newline] == '\r'
            && newline + 1 < text.Length
            && text[newline + 1] == '\n'
                ? "\r\n"
                : text[newline].ToString();
    }

    private static string AppendLine(string source, string line, string newline)
    {
        if (source.Length == 0)
        {
            return line;
        }

        var endedWithNewline = source.EndsWith('\r') || source.EndsWith('\n');
        return endedWithNewline
            ? source + line + newline
            : source + newline + line;
    }

    private static string AppendBlock(string source, string block, string newline)
    {
        if (source.Length == 0)
        {
            return block;
        }

        var endedWithNewline = source.EndsWith('\r') || source.EndsWith('\n');
        return endedWithNewline
            ? source + block + newline
            : source + newline + block;
    }

    private static string InsertLine(string source, int offset, string line, string newline)
    {
        if (offset == source.Length)
        {
            return AppendLine(source, line, newline);
        }

        return source.Insert(offset, line + newline);
    }

    [GeneratedRegex(
        @"^\s*\[(?<path>[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)*)\]\s*(?:#.*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SimpleTableHeaderRegex();

    [GeneratedRegex(
        """^(?:(?:[A-Za-z0-9_-]+|"(?:[^"\\]|\\.)*"|'[^']*')(?:\s*\.\s*(?:[A-Za-z0-9_-]+|"(?:[^"\\]|\\.)*"|'[^']*'))*)$""",
        RegexOptions.CultureInvariant)]
    private static partial Regex ConservativeKeyRegex();

    [GeneratedRegex(
        @"^(?:true|false|[+-]?(?:inf|nan)|[+-]?(?:0|[1-9](?:_?[0-9])*)|[+-]?0x[0-9A-Fa-f](?:_?[0-9A-Fa-f])*|[+-]?0o[0-7](?:_?[0-7])*|[+-]?0b[01](?:_?[01])*|[+-]?(?:0|[1-9](?:_?[0-9])*)\.[0-9](?:_?[0-9])*(?:[eE][+-]?[0-9](?:_?[0-9])*)?|[+-]?(?:0|[1-9](?:_?[0-9])*)[eE][+-]?[0-9](?:_?[0-9])*|[0-9]{4}-[0-9]{2}-[0-9]{2}(?:[Tt ][0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)?(?:[Zz]|[+-][0-9]{2}:[0-9]{2})?)?|[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)?)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ConservativeScalarRegex();

    private sealed record PhysicalLine(
        int Number,
        int Start,
        int ContentEnd,
        int End,
        string Content);

    private sealed record Assignment(
        string[] Path,
        PhysicalLine Line,
        int ValueStart,
        int ValueEnd);

    private sealed class Section(string[] path, PhysicalLine headerLine, int end)
    {
        public string[] Path { get; } = path;

        public PhysicalLine HeaderLine { get; } = headerLine;

        public int End { get; set; } = end;
    }

    private sealed record Analysis(
        List<Assignment> TargetAssignments,
        List<Assignment> AllAssignments,
        List<Section> Sections,
        SparseTomlError? Error)
    {
        public static Analysis Invalid(SparseTomlError error) =>
            new([], [], [], error);
    }

    private sealed record PathParseResult(
        string[]? Segments,
        SparseTomlError? Error);

    private readonly record struct ValueScanState(
        char Quote,
        bool Triple,
        bool Escaped,
        int SquareDepth,
        int BraceDepth,
        bool SawContent,
        int ValueEnd)
    {
        public static ValueScanState Start => new('\0', false, false, 0, 0, false, -1);

        public static ValueScanState Invalid => new('\0', false, false, -1, -1, false, -1);

        public bool IsInvalid => SquareDepth < 0 || BraceDepth < 0;

        public bool IsComplete =>
            !IsInvalid
            &&
            Quote == '\0'
            && SquareDepth == 0
            && BraceDepth == 0
            && SawContent;
    }
}
