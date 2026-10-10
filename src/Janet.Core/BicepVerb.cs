namespace Janet.Core;

/// <summary>
/// `janet bicep`: the bicep_check envelope, for hooks and shells that cannot speak MCP.
/// </summary>
/// <remarks>
/// Here rather than in Janet.Cli's Program.cs because nothing references Janet.Cli, so a verb
/// written there has no test. Program.cs only picks the path and the flags out of its arguments
/// and calls Run.
///
/// The same Bicep.Check and the same BicepJson.Serialize the bicep_check tool returns, so the two
/// arms cannot disagree about anything except durationSeconds. The exit code mirrors janet check:
/// 0 exactly when succeeded is true, 1 otherwise. A file that is missing or is not Bicep, and a
/// Bicep CLI that will not run, throw GraphException before anything reaches the writer -- the CLI
/// prints the message on stderr and exits 1 -- so a failure never reads as an empty success.
/// </remarks>
public static class BicepVerb
{
    public static int Run(string? path, bool text, bool pretty, TextWriter output)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("bicep needs a path: janet bicep <file.bicep|file.bicepparam>.");
        }

        return Write(Bicep.Check(path), text, pretty, output);
    }

    /// <summary>Writes one finished check and returns the exit code it maps to.</summary>
    public static int Write(BicepCheckResult result, bool text, bool pretty, TextWriter output)
    {
        output.Write(text
            ? BicepJson.Render(result)
            : BicepJson.Serialize(result, pretty) + Environment.NewLine);

        return result.Succeeded ? 0 : 1;
    }
}
