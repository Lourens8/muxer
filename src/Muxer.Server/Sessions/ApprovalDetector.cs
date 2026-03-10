using System.Text.RegularExpressions;

namespace Muxer.Server.Sessions;

public static partial class ApprovalDetector
{
    [GeneratedRegex(@"\x1B\[[0-9;]*[a-zA-Z]|\x1B\].*?\x07|\x1B\(B", RegexOptions.Compiled)]
    private static partial Regex AnsiRegex();

    // Match the numbered approval options Claude Code presents
    [GeneratedRegex(@"^\s*1[\.\)]\s+.+$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex Option1Regex();

    [GeneratedRegex(@"^\s*2[\.\)]\s+.+$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex Option2Regex();

    [GeneratedRegex(@"^\s*3[\.\)]\s+.+$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex Option3Regex();

    public static ApprovalInfo? Detect(string rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            return null;

        var clean = AnsiRegex().Replace(rawOutput, "");
        var lines = clean.Split('\n', StringSplitOptions.None);

        // Find a cluster of lines matching "1. ...", "2. ...", "3. ..." pattern
        // that looks like Claude's approval prompt
        for (int i = 0; i < lines.Length; i++)
        {
            if (!Option1Regex().IsMatch(lines[i])) continue;

            // Look for options 2 and 3 within the next few lines
            bool found2 = false, found3 = false;
            int endLine = Math.Min(i + 6, lines.Length);

            for (int j = i + 1; j < endLine; j++)
            {
                if (Option2Regex().IsMatch(lines[j])) found2 = true;
                if (Option3Regex().IsMatch(lines[j])) found3 = true;
            }

            if (!found2 || !found3) continue;

            // Extract context: grab the non-empty lines above the options
            var contextLines = lines
                .Take(i)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .TakeLast(8)
                .ToArray();

            // Extract the option text
            var options = new List<string>();
            for (int j = i; j < endLine; j++)
            {
                var trimmed = lines[j].Trim();
                if (trimmed.StartsWith("1.") || trimmed.StartsWith("1)") ||
                    trimmed.StartsWith("2.") || trimmed.StartsWith("2)") ||
                    trimmed.StartsWith("3.") || trimmed.StartsWith("3)"))
                {
                    options.Add(trimmed);
                }
            }

            return new ApprovalInfo
            {
                Context = string.Join("\n", contextLines),
                Options = options.ToArray()
            };
        }

        return null;
    }
}

public class ApprovalInfo
{
    public required string Context { get; init; }
    public required string[] Options { get; init; }
}
