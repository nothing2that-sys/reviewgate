using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ReviewGate.Contracts;

namespace ReviewGate.Transport.Http;

// Generic credentials in Windows Credential Manager (DPAPI-protected for the current Windows user).
// Any program running as the same user can read them; the real damage limit is the provider-side
// project key and spending cap.
[SupportedOSPlatform("windows")]
public sealed partial class WindowsCredentialStore : ICredentialStore
{
    const uint GenericType = 1, PersistLocalMachine = 2, MaxBlobBytes = 2560;
    const int ErrorNotFound = 1168;
    public static readonly string[] Providers = ["openai", "together", "google"];
    public static string Target(string provider)
    {
        Data.Require(Providers.Contains(provider), "unsupported_provider");
        return "ReviewGate/live/" + provider;
    }
    public string? Read(string provider)
    {
        if (!CredRead(Target(provider), GenericType, 0, out var pointer))
        {
            if (Marshal.GetLastPInvokeError() == ErrorNotFound) return null;
            throw new ReviewError(21, "credential_store_unavailable");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.BlobSize == 0 || credential.Blob == IntPtr.Zero) return null;
            var bytes = new byte[credential.BlobSize];
            Marshal.Copy(credential.Blob, bytes, 0, bytes.Length);
            return Data.Utf8.GetString(bytes);
        }
        finally { CredFree(pointer); }
    }
    public void Write(string provider, string secret)
    {
        secret = secret.Trim();
        Data.Require(secret.Length is > 0 and < 1024 && !secret.Any(char.IsWhiteSpace), "invalid_credential_value");
        var bytes = Data.Utf8.GetBytes(secret);
        Data.Require(bytes.Length <= MaxBlobBytes, "invalid_credential_value");
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential { Type = GenericType, TargetName = Target(provider), BlobSize = (uint)bytes.Length, Blob = blob, Persist = PersistLocalMachine, UserName = "reviewgate", Comment = "ReviewGate live API key" };
            if (!CredWrite(ref credential, 0)) throw new ReviewError(21, "credential_store_unavailable");
        }
        finally { Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length); Marshal.FreeHGlobal(blob); Array.Clear(bytes); }
    }
    public bool Delete(string provider)
    {
        if (CredDelete(Target(provider), GenericType, 0)) return true;
        if (Marshal.GetLastPInvokeError() == ErrorNotFound) return false;
        throw new ReviewError(21, "credential_store_unavailable");
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
    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr buffer);
}
