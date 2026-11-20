using MyAgent.Core;

namespace MyAgent.Sandbox;

/// <summary>
/// File-effect policy vocabulary (mirrors dsh-sandbox SandboxMode):
/// read-only permits no writes; workspace-write also permits the workspace,
/// explicit extra roots, and platform temp areas; danger-full-access bypasses confinement.
/// Network and process visibility are outside this vocabulary.
/// </summary>
public enum SandboxMode
{
    ReadOnly,
    WorkspaceWrite,
    DangerFullAccess,
}

public static class SandboxModes
{
    public const string ReadOnlyWire = "read-only";
    public const string WorkspaceWriteWire = "workspace-write";
    public const string DangerFullAccessWire = "danger-full-access";

    public static readonly IReadOnlyList<SandboxMode> All =
        [SandboxMode.ReadOnly, SandboxMode.WorkspaceWrite, SandboxMode.DangerFullAccess];

    public static string Wire(this SandboxMode mode) => mode switch
    {
        SandboxMode.ReadOnly => ReadOnlyWire,
        SandboxMode.WorkspaceWrite => WorkspaceWriteWire,
        SandboxMode.DangerFullAccess => DangerFullAccessWire,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static SandboxMode Parse(string value) => value switch
    {
        ReadOnlyWire => SandboxMode.ReadOnly,
        WorkspaceWriteWire => SandboxMode.WorkspaceWrite,
        DangerFullAccessWire => SandboxMode.DangerFullAccess,
        _ => throw new HarnessError("invalid_sandbox_mode", $"unknown sandbox mode \"{value}\""),
    };

    public static bool TryParse(string? value, out SandboxMode mode)
    {
        switch (value)
        {
            case ReadOnlyWire: mode = SandboxMode.ReadOnly; return true;
            case WorkspaceWriteWire: mode = SandboxMode.WorkspaceWrite; return true;
            case DangerFullAccessWire: mode = SandboxMode.DangerFullAccess; return true;
            default: mode = SandboxMode.ReadOnly; return false;
        }
    }
}

/// <summary>
/// The complete file-effect policy resolved for ONE capability call (mirrors
/// SandboxExecutionPolicy). The root is carried even under modes that do not consume it.
/// </summary>
public sealed record SandboxExecutionPolicy(
    SandboxMode Mode,
    string WorkspaceRoot,
    IReadOnlyList<string>? AdditionalWritableRoots = null,
    string? SessionId = null);

/// <summary>Sandbox error codes shared by every enforcing family (mirrors the dsh-sandbox seam).</summary>
public static class SandboxErrorCodes
{
    public const string Unavailable = "SANDBOX_UNAVAILABLE";
    public const string PreparationFailed = "SANDBOX_PREPARATION_FAILED";
    public const string InvalidTempRoot = "SANDBOX_INVALID_TEMP_ROOT";
    public const string InvalidRoot = "SANDBOX_INVALID_ROOT";
    public const string FsDenied = "FS_SANDBOX_DENIED";
    public const string CommandDenied = "SANDBOX_DENIED";

    public static HarnessError UnavailableError(SandboxMode mode, string? detail = null) => new(
        Unavailable,
        $"sandbox mode \"{mode.Wire()}\" is requested but no sandbox backend is usable on this host; "
        + "refusing to run the command unconfined. Ensure the ACL restricted-token runner can start (Windows) "
        + "— otherwise switch the consumer to danger-full-access."
        + (detail is null ? "" : $" Runner failure: {detail}"));

    public static HarnessError InvalidRootError(string path, string detail, string subject = "additional writable directory") =>
        new(InvalidRoot, $"Invalid {subject} \"{path}\": {detail}");
}
