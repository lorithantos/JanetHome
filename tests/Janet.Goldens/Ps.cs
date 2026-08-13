using System.Diagnostics;
using System.Text;

namespace Janet.Goldens;

/// <summary>
/// Runs a PowerShell script and returns what it printed.
/// </summary>
/// <remarks>
/// Lives in the generator, not in the test project: the tests must run on a machine with no
/// PowerShell at all, which is the portability the whole port is for. Capturing the answers
/// once, here, is what buys that.
/// </remarks>
public static class Ps
{
    /// <summary>
    /// Builds a command line rather than using -File.
    /// </summary>
    /// <remarks>
    /// -File hands each argv element to the binder literally, so "a,b" binds to a [string[]]
    /// parameter as ONE element containing a comma -- silently, as an empty result rather than
    /// an error. Only the PowerShell parser turns a,b into a two-element array, and -Command is
    /// what runs the text through it.
    ///
    /// Repeated flags are folded into that comma syntax: PowerShell rejects a repeated
    /// parameter outright, so `-Tag a -Tag b` has to become `-Tag 'a','b'`. The case list is
    /// written with repeats because that is the form the C# side binds directly.
    /// </remarks>
    public static string BuildCommand(string script, IReadOnlyList<string> args)
    {
        List<(string Flag, List<string> Values)> parameters = [];

        foreach (string arg in args)
        {
            if (arg.StartsWith('-') && !char.IsDigit(arg.Length > 1 ? arg[1] : '-'))
            {
                if (!parameters.Any(p => p.Flag == arg))
                {
                    parameters.Add((arg, []));
                }

                continue;
            }

            if (parameters.Count == 0)
            {
                throw new ArgumentException($"positional argument with no flag: {arg}");
            }

            parameters[^1].Values.Add(arg);
        }

        StringBuilder command = new();
        command.Append("& ").Append(Quote(script));

        foreach ((string flag, List<string> values) in parameters)
        {
            command.Append(' ').Append(flag);

            if (values.Count > 0)
            {
                command.Append(' ').Append(string.Join(',', values.Select(Quote)));
            }
        }

        return command.ToString();
    }

    public static string Quote(string value) => $"'{value.Replace("'", "''")}'";

    /// <summary>Runs a script and returns stdout, throwing on a non-zero exit.</summary>
    public static string Run(string script, IReadOnlyList<string> args, string workingDirectory) =>
        Invoke(BuildCommand(script, args), workingDirectory);

    /// <summary>Runs an arbitrary command, for the few things that are not a script call.</summary>
    public static string Invoke(string command, string workingDirectory)
    {
        ProcessStartInfo info = new()
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,

            // Pinned on both sides rather than left to the console code page, so a golden
            // captured on one machine is the same bytes as a golden captured on another.
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add("[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); " + command);

        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException("could not start pwsh");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"pwsh failed ({process.ExitCode}): {command}\n{stderr}");
        }

        return stdout;
    }
}
