namespace MyAgent.Sandbox;

/// <summary>
/// Environment hygiene shared by every shell execution path (confined and unconfined).
/// Long agent sessions run dozens of `dotnet build` calls; by default each leaves MSBuild
/// worker nodes and the Roslyn compiler server alive for ~15 minutes ("node reuse"), so
/// the process list fills with lingering dotnet.exe instances holding hundreds of MB each.
/// Disabling reuse makes every build clean up its own workers when it exits.
/// </summary>
public static class ShellEnvironment
{
    public static void ApplyBuildHygiene(IDictionary<string, string> entries)
    {
        // MSBuild: exit worker nodes when the build finishes instead of parking them.
        entries["MSBUILDDISABLENODEREUSE"] = "1";
        // dotnet CLI: do not spawn/attach the persistent MSBuild server.
        entries["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
        // Roslyn: compile in-proc; no lingering VBCSCompiler server. (MSBuild reads
        // environment variables as properties, so this reaches the Csc task.)
        entries["UseSharedCompilation"] = "false";
        entries["DOTNET_NOLOGO"] = "1";
        entries["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
    }
}
