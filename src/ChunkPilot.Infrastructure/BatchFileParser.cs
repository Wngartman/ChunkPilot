using System.Text.RegularExpressions;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed partial class BatchFileParser
{
    public BatchParseResult Parse(string scriptPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        var fullPath = Path.GetFullPath(scriptPath);
        var text = File.ReadAllText(fullPath);
        var logicalLines = JoinContinuations(text);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        var java = "";
        var arguments = "";
        var invokesScript = false;
        var detaches = false;

        foreach (var rawLine in logicalLines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("::", StringComparison.Ordinal))
                continue;

            var setMatch = SetRegex().Match(line);
            if (setMatch.Success)
            {
                environment[setMatch.Groups["key"].Value] = setMatch.Groups["value"].Value.Trim('"');
                continue;
            }

            var expandedLine = ExpandKnownVariables(line, environment);
            if (StartRegex().IsMatch(expandedLine))
            {
                detaches = true;
                expandedLine = StartPrefixRegex().Replace(expandedLine, "");
            }

            if (CallRegex().IsMatch(line) || ScriptInvocationRegex().IsMatch(line))
                invokesScript = true;

            var javaMatch = JavaCommandRegex().Match(expandedLine);
            if (!javaMatch.Success)
                continue;

            java = javaMatch.Groups["exe"].Value.Trim('"');
            arguments = javaMatch.Groups["args"].Value.Trim();
        }

        if (detaches)
            problems.Add("The script uses START and may detach Java, preventing reliable console capture.");
        if (string.IsNullOrWhiteSpace(java))
            problems.Add("No Java command could be parsed; ChunkPilot can still run the script as written.");
        if (invokesScript)
            problems.Add("The script invokes another script; review the complete launch chain before import.");

        return new BatchParseResult(java, arguments, environment, detaches, invokesScript, problems);
    }

    private static IReadOnlyList<string> JoinContinuations(string text)
    {
        var result = new List<string>();
        var current = "";
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.TrimEnd();
            current += trimmed.EndsWith('^') ? trimmed[..^1] + " " : trimmed;
            if (!trimmed.EndsWith('^'))
            {
                result.Add(current);
                current = "";
            }
        }
        if (current.Length > 0)
            result.Add(current);
        return result;
    }

    private static string ExpandKnownVariables(string value, IReadOnlyDictionary<string, string> environment)
    {
        foreach (var pair in environment)
            value = value.Replace($"%{pair.Key}%", pair.Value, StringComparison.OrdinalIgnoreCase);
        return value;
    }

    [GeneratedRegex(@"^\s*set\s+""?(?<key>[^=""\s]+)=(?<value>.*?)""?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SetRegex();

    [GeneratedRegex(@"^\s*(?:call\s+)?(?<exe>""[^""]*java(?:w)?(?:\.exe)?""|[^\s""]*java(?:w)?(?:\.exe)?|java(?:w)?(?:\.exe)?)\s+(?<args>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex JavaCommandRegex();

    [GeneratedRegex(@"^\s*start(?:\s+""""|\s+""[^""]*"")?\s+", RegexOptions.IgnoreCase)]
    private static partial Regex StartRegex();

    [GeneratedRegex(@"^\s*start(?:\s+""""|\s+""[^""]*"")?\s+", RegexOptions.IgnoreCase)]
    private static partial Regex StartPrefixRegex();

    [GeneratedRegex(@"^\s*call\s+.+\.(?:bat|cmd)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CallRegex();

    [GeneratedRegex(@"\.(?:bat|cmd)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptInvocationRegex();
}

public sealed record BatchParseResult(
    string JavaExecutable,
    string Arguments,
    IReadOnlyDictionary<string, string> Environment,
    bool Detaches,
    bool InvokesAnotherScript,
    IReadOnlyList<string> Problems);
