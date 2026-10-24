using System.Security.Cryptography;

namespace Atsm.Diagnostics.Windows;

public sealed class WindowsCaseProtection : IDiagnosticCaseProtection
{
    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
}
