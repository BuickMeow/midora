using System.Security.Cryptography;
using System.Text;

namespace Midora.Common;

/// <summary>
/// Builds named-pipe names that are valid on every release platform.
/// </summary>
/// <remarks>
/// Windows names a kernel object and accepts long names. On Unix a named pipe is a Unix domain
/// socket created as <c>Path.GetTempPath()/CoreFxPipe_&lt;name&gt;</c>, and macOS rejects socket
/// paths longer than about 104 bytes, so an over-long logical name is replaced by a short
/// deterministic hash of itself.
/// </remarks>
public static class MidoraInterprocessPipeName
{
    private const int UnixSocketPathLimit = 104;

    // The platform counts the terminating NUL against the socket path budget.
    private const int UnixSocketNullTerminatorBytes = 1;
    private const string UnixSocketPrefix = "CoreFxPipe_";
    private const string ShortPrefix = "midora-";

    public static string Create(string logicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        if (OperatingSystem.IsWindows())
        {
            return logicalName;
        }

        int budget = UnixSocketPathLimit
            - UnixSocketNullTerminatorBytes
            - UnixSocketPrefix.Length
            - Path.GetTempPath().Length
            - ShortPrefix.Length;
        if (budget >= logicalName.Length)
        {
            return logicalName;
        }

        string hex = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(logicalName)));
        int suffixLength = Math.Clamp(budget, 16, hex.Length);
        return ShortPrefix + hex[..suffixLength];
    }
}
