using System.Security.Cryptography;
using System.Text;

namespace Astm.Runtime.Sessions;

/// <summary>Stable IDs within an explicit reception supplied and retained by the host.
/// Content hashes alone are not identities of clinical measurements.</summary>
public static class ReceptionIdentity
{
    public static Guid Derive(Guid receptionId, string scope, int ordinal)
    {
        if (receptionId == Guid.Empty) throw new ArgumentException("A reception identity is required.", nameof(receptionId));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{receptionId:D}/{scope}/{ordinal}"));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x80); // UUIDv8, application-defined SHA-256 namespace.
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
