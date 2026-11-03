using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Astm.Service;

public static class LocalAccess
{
    /// <summary>Dedicated local directory; never store trace data or credentials here.</summary>
    public static string PrivateDirectory(string path)
    {
        path = Path.GetFullPath(path);
        if (new DirectoryInfo(path).LinkTarget is not null) throw new ServiceException("ipc.directory.symlink");
        if (OperatingSystem.IsWindows()) PrepareWindowsDirectory(path);
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new ServiceException("ipc.directory.permissions");
        }
        return path;
    }
    public static IDisposable Claim(string directory, string identity)
    {
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        try { return new FileStream(Path.Combine(directory, name + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new ServiceException("ownership.busy"); }
        // Lock files are deliberately retained. Unlinking one would permit two different locked inodes.
    }
    [SupportedOSPlatform("windows")]
    private static void PrepareWindowsDirectory(string path)
    {
        var sid = WindowsIdentity.GetCurrent().User ?? throw new ServiceException("ipc.identity.required");
        if (!Directory.Exists(path))
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false); security.SetOwner(sid);
            security.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            FileSystemAclExtensions.Create(new DirectoryInfo(path), security);
        }
        var acl = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path));
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !rule.IdentityReference.Equals(sid)
                && !rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null))
                && !rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)))
                throw new ServiceException("ipc.directory.permissions");
    }
}

/// <summary>Local-only OS-authenticated IPC. No TCP listener and no bearer secrets.</summary>
public sealed class LocalIpcListener : IAsyncDisposable
{
    private readonly string _endpoint;
    private readonly IDisposable _claim;
    private readonly Socket? _socket;
    private NamedPipeServerStream? _pendingPipe;
    public LocalIpcListener(string endpoint, string ownershipDirectory)
    {
        _endpoint = endpoint;
        var directory = LocalAccess.PrivateDirectory(ownershipDirectory);
        if (OperatingSystem.IsWindows())
        {
            if (endpoint.Length is < 1 or > 100 || endpoint.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
                throw new ServiceException("ipc.pipe.name");
        }
        else
        {
            if (!Path.IsPathFullyQualified(endpoint) || Encoding.UTF8.GetByteCount(endpoint) > 100) throw new ServiceException("ipc.socket.path");
            LocalAccess.PrivateDirectory(Path.GetDirectoryName(endpoint)!);
        }
        _claim = LocalAccess.Claim(directory, "endpoint:" + endpoint);
        if (OperatingSystem.IsWindows())
        {
            try { _pendingPipe = WindowsPipe.Create(endpoint, firstInstance: true); }
            catch { _claim.Dispose(); throw; }
        }
        else
        {
            try
            {
                // The configured endpoint is reserved to this service, under the private directory and owner lock.
                if (new FileInfo(endpoint).LinkTarget is not null) throw new ServiceException("ipc.socket.symlink");
                File.Delete(endpoint);
                _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                _socket.Bind(new UnixDomainSocketEndPoint(endpoint));
                File.SetUnixFileMode(endpoint, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                _socket.Listen(8);
            }
            catch { _socket?.Dispose(); _claim.Dispose(); throw; }
        }
    }
    public async ValueTask<Stream> AcceptAsync(CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = _pendingPipe ?? throw new ServiceException("ipc.listener.closed");
            try
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                // Keep the pipe namespace reserved under this DACL before handing off the connected instance.
                _pendingPipe = WindowsPipe.Create(_endpoint);
                return pipe;
            }
            catch { _pendingPipe = null; await pipe.DisposeAsync().ConfigureAwait(false); throw; }
        }
        var socket = await _socket!.AcceptAsync(token).ConfigureAwait(false);
        return new NetworkStream(socket, ownsSocket: true);
    }
    public ValueTask DisposeAsync()
    {
        _socket?.Dispose();
        _pendingPipe?.Dispose();
        if (_socket is not null) File.Delete(_endpoint);
        _claim.Dispose();
        return ValueTask.CompletedTask;
    }
}

[SupportedOSPlatform("windows")]
internal static class WindowsPipe
{
    // PIPE_REJECT_REMOTE_CLIENTS is required in addition to the current-user DACL.
    public static NamedPipeServerStream Create(string name, bool firstInstance = false)
    {
        var sid = WindowsIdentity.GetCurrent().User ?? throw new ServiceException("ipc.identity.required");
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("D:P(A;;GA;;;" + sid.Value + ")", 1, out var descriptor, out _))
            throw new ServiceException("ipc.pipe.security");
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipe("\\\\.\\pipe\\" + name, 3 | 0x40000000u | (firstInstance ? 0x00080000u : 0), 8, 16, 65536, 65536, 0, ref attributes);
            if (handle.IsInvalid) { handle.Dispose(); throw new ServiceException("ipc.pipe.create"); }
            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally { LocalFree(descriptor); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint mode, uint pipeMode, uint instances, uint output, uint input, uint timeout, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
