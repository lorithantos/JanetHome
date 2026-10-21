using Xunit;

namespace Janet.Tests;

/// <summary>
/// Every test class that sets a process environment variable, run with nothing else alongside.
/// </summary>
/// <remarks>
/// The environment is one table per process, and xUnit runs collections in parallel with each
/// other, so a class that sets a variable and restores it afterwards still leaks the value to
/// whatever runs in that window. Sharing a collection only queues the setters behind each other;
/// the readers are anywhere -- every thread write reads JANET_NOTES_BUDGET, every MCP result
/// reads JANET_RESULT_BUDGET, every dotnet child inherits Platform -- so the setters have to be
/// kept away from all of them, the reasoning WriteLockTimeoutCollection gives for LockTimeout.
/// <para>
/// This was ResultBudgetCollection until 2026-10-21, a plain shared collection for the two
/// classes that set JANET_RESULT_BUDGET. Widened rather than joined by a second concept when
/// DotnetCheckStartInfoTests and DotnetCheckPlatformTests were found setting Platform with no
/// collection at all. A class that calls Environment.SetEnvironmentVariable belongs here.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}
