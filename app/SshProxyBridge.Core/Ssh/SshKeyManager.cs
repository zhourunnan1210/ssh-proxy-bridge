using System.Diagnostics;
using System.Security.Principal;

namespace SshProxyBridge.Core.Ssh;

public sealed class SshKeyManager
{
    public async Task<SshKeyMaterial> EnsureOneClickKeyAsync(
        string configuredPrivateKeyPath,
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPrivateKeyPath);

        var privateKeyPath = Environment.ExpandEnvironmentVariables(configuredPrivateKeyPath);
        var publicKeyPath = $"{privateKeyPath}.pub";
        var directory = Path.GetDirectoryName(privateKeyPath)
            ?? throw new InvalidOperationException("SSH 私钥路径缺少父目录。");
        Directory.CreateDirectory(directory);

        if (!File.Exists(privateKeyPath))
        {
            if (File.Exists(publicKeyPath))
                throw new InvalidOperationException("发现孤立的 SSH 公钥，但对应私钥不存在。请更换 Key 路径。");

            var result = await RunAsync(
                FindOpenSshExecutable("ssh-keygen.exe"),
                [
                    "-q",
                    "-t", "ed25519",
                    "-a", "64",
                    "-N", string.Empty,
                    "-C", $"ssh-proxy-bridge:{profileId:D}",
                    "-f", privateKeyPath
                ],
                cancellationToken);

            EnsureSuccess(result, "生成 ED25519 SSH Key 失败");
        }

        await RestrictPrivateKeyAclAsync(privateKeyPath, cancellationToken);

        if (!File.Exists(publicKeyPath))
        {
            var result = await RunAsync(
                FindOpenSshExecutable("ssh-keygen.exe"),
                ["-y", "-f", privateKeyPath],
                cancellationToken);
            EnsureSuccess(result, "从私钥恢复 SSH 公钥失败");
            await File.WriteAllTextAsync(
                publicKeyPath,
                $"{result.StandardOutput.Trim()} ssh-proxy-bridge:{profileId:D}{Environment.NewLine}",
                cancellationToken);
        }

        var publicKey = (await File.ReadAllTextAsync(publicKeyPath, cancellationToken)).Trim();
        if (!publicKey.StartsWith("ssh-", StringComparison.Ordinal)
            || publicKey.Contains('\n')
            || publicKey.Contains('\r'))
        {
            throw new InvalidDataException("生成的 SSH 公钥格式无效。");
        }

        return new SshKeyMaterial(privateKeyPath, publicKeyPath, publicKey);
    }

    private static async Task RestrictPrivateKeyAclAsync(
        string privateKeyPath,
        CancellationToken cancellationToken)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("无法读取当前 Windows 用户 SID。");
        var icacls = Path.Combine(Environment.SystemDirectory, "icacls.exe");

        var result = await RunAsync(
            icacls,
            [privateKeyPath, "/inheritance:r", "/grant:r", $"*{sid}:F"],
            cancellationToken);
        EnsureSuccess(result, "设置 SSH 私钥 NTFS ACL 失败");
    }

    private static string FindOpenSshExecutable(string name)
    {
        var systemPath = Path.Combine(Environment.SystemDirectory, "OpenSSH", name);
        return File.Exists(systemPath) ? systemPath : name;
    }

    private static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private static void EnsureSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode == 0)
            return;

        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        throw new InvalidOperationException($"{operation}（退出码 {result.ExitCode}）：{detail}");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}

public sealed record SshKeyMaterial(
    string PrivateKeyPath,
    string PublicKeyPath,
    string PublicKey);
