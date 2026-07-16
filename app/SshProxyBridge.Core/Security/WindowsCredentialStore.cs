using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;

namespace SshProxyBridge.Core.Security;

public sealed class WindowsCredentialStore : ICredentialStore
{
    private const int CredentialTypeGeneric = 1;
    private const int CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumCredentialBlobSize = 5 * 512;

    public Task SaveAsync(
        CredentialReference reference,
        string userName,
        string secret,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(reference, userName, secret);

        var secretBytes = Encoding.Unicode.GetBytes(secret);
        if (secretBytes.Length > MaximumCredentialBlobSize)
            throw new ArgumentException("密码超过 Windows Credential Manager 支持的最大长度。", nameof(secret));

        var blobPointer = IntPtr.Zero;
        try
        {
            blobPointer = Marshal.AllocCoTaskMem(secretBytes.Length);
            Marshal.Copy(secretBytes, 0, blobPointer, secretBytes.Length);

            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = reference.TargetName,
                CredentialBlobSize = secretBytes.Length,
                CredentialBlob = blobPointer,
                Persist = CredentialPersistLocalMachine,
                UserName = userName
            };

            if (!CredWriteW(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "保存 Windows 凭据失败。");

            return Task.CompletedTask;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            if (blobPointer != IntPtr.Zero)
            {
                for (var index = 0; index < secretBytes.Length; index++)
                    Marshal.WriteByte(blobPointer, index, 0);
                Marshal.FreeCoTaskMem(blobPointer);
            }
        }
    }

    public async Task<StoredCredential?> ReadAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReference(reference);

        var credential = ReadCore(reference);
        if (credential is not null || !reference.TryGetLegacyEquivalent(out var legacyReference))
            return credential;

        var legacyCredential = ReadCore(legacyReference);
        if (legacyCredential is null)
            return null;

        await SaveAsync(
            reference,
            legacyCredential.UserName,
            legacyCredential.Secret,
            cancellationToken);
        return legacyCredential;
    }

    public Task DeleteAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReference(reference);

        DeleteCore(reference);
        if (reference.TryGetLegacyEquivalent(out var legacyReference))
            DeleteCore(legacyReference);

        return Task.CompletedTask;
    }

    private static StoredCredential? ReadCore(CredentialReference reference)
    {

        if (!CredReadW(reference.TargetName, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
                return null;

            throw new Win32Exception(error, "读取 Windows 凭据失败。");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            var secretBytes = new byte[credential.CredentialBlobSize];

            try
            {
                if (secretBytes.Length > 0)
                    Marshal.Copy(credential.CredentialBlob, secretBytes, 0, secretBytes.Length);

                var secret = Encoding.Unicode.GetString(secretBytes);
                return new StoredCredential(
                    credential.UserName ?? string.Empty,
                    secret);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secretBytes);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    private static void DeleteCore(CredentialReference reference)
    {
        if (!CredDeleteW(reference.TargetName, CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
                throw new Win32Exception(error, "删除 Windows 凭据失败。");
        }
    }

    private static void Validate(
        CredentialReference reference,
        string userName,
        string secret)
    {
        ValidateReference(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrEmpty(secret);

        if (userName.Contains('\0') || secret.Contains('\0'))
            throw new ArgumentException("用户名和密码不能包含空字符。");
    }

    private static void ValidateReference(CredentialReference reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.TargetName);
        if (reference.TargetName.Contains('\0'))
            throw new ArgumentException("凭据引用不能包含空字符。", nameof(reference));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;

        public FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW([In] ref NativeCredential credential, int flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(
        string target,
        int type,
        int reservedFlag,
        out IntPtr credentialPointer);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, int type, int flags);

    [DllImport("Advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr credentialPointer);
}
