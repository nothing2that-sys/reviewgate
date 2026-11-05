using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ReviewGate.Contracts;

namespace ReviewGate.Transport.Http;

// Generic credentials in Windows Credential Manager (DPAPI-protected for the current Windows user).
// Any program running as the same user can read them; the real damage limit is the provider-side
// project key and spending cap.
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialVault : ICredentialVault
{
    const uint GenericType = 1, PersistLocalMachine = 2, MaxBlobBytes = 2560;
    const int ErrorNotFound = 1168;
    public string? Read(string target)
    {
        if (!CredRead(target, GenericType, 0, out var pointer))
        {
            if (Marshal.GetLastPInvokeError() == ErrorNotFound) return null;
            throw new ReviewError(21, "credential_store_unavailable");
        }
        try { return Secret(Marshal.PtrToStructure<Credential>(pointer)); }
        finally { CredFree(pointer); }
    }
    public void Write(string target, string userName, string secret)
    {
        var bytes = Data.Utf8.GetBytes(secret);
        Data.Require(bytes.Length is > 0 and <= (int)MaxBlobBytes, "invalid_credential_value");
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential { Type = GenericType, TargetName = target, BlobSize = (uint)bytes.Length, Blob = blob, Persist = PersistLocalMachine, UserName = userName, Comment = "ReviewGate" };
            if (!CredWrite(ref credential, 0)) throw new ReviewError(21, "credential_store_unavailable");
        }
        finally { Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length); Marshal.FreeHGlobal(blob); Array.Clear(bytes); }
    }
    public bool Delete(string target)
    {
        if (CredDelete(target, GenericType, 0)) return true;
        if (Marshal.GetLastPInvokeError() == ErrorNotFound) return false;
        throw new ReviewError(21, "credential_store_unavailable");
    }
    // Lists targets and display names only; secrets are not copied out of the enumeration.
    public IReadOnlyList<VaultItem> Enumerate(string prefix)
    {
        if (!CredEnumerate(prefix + "*", 0, out uint count, out var array))
        {
            if (Marshal.GetLastPInvokeError() == ErrorNotFound) return [];
            throw new ReviewError(21, "credential_store_unavailable");
        }
        try
        {
            var items = new List<VaultItem>();
            for (int i = 0; i < count; i++)
            {
                var credential = Marshal.PtrToStructure<Credential>(Marshal.ReadIntPtr(array, i * IntPtr.Size));
                if (credential.Type != GenericType || !credential.TargetName.StartsWith(prefix, StringComparison.Ordinal)) continue;
                long ticks = ((long)credential.LastWritten.dwHighDateTime << 32) | (uint)credential.LastWritten.dwLowDateTime;
                items.Add(new(credential.TargetName, credential.UserName ?? "", ticks > 0 ? DateTimeOffset.FromFileTime(ticks) : null));
            }
            return items;
        }
        finally { CredFree(array); }
    }
    static string? Secret(Credential credential)
    {
        if (credential.BlobSize == 0 || credential.Blob == IntPtr.Zero) return null;
        var bytes = new byte[credential.BlobSize];
        Marshal.Copy(credential.Blob, bytes, 0, bytes.Length);
        try { return Data.Utf8.GetString(bytes); } finally { Array.Clear(bytes); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredEnumerate(string? filter, uint flags, out uint count, out IntPtr credentials);
    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr buffer);
}
