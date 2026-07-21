# 下载与文件校验

这份说明适合希望确认下载来源和文件完整性的用户。第一次使用时，最重要的是从正确的 Releases 页面下载，并完整解压 ZIP。

## 下载正确的文件

1. 打开项目的 [Releases 页面](https://github.com/zhourunnan1210/ssh-proxy-bridge/releases/latest)。
2. 在版本页面底部展开 **Assets**。
3. 下载名称类似下面的 Windows 发行包：

   ```text
   SSH-Proxy-Bridge-v0.2.0-win-x64.zip
   ```

不要下载 GitHub 自动生成的 `Source code (zip)` 或 `Source code (tar.gz)`。它们只包含源码，不能作为便携版直接运行。

## 完整解压

右键 ZIP 并选择“全部解压”，或者在 PowerShell 中执行：

```powershell
Expand-Archive .\SSH-Proxy-Bridge-v0.2.0-win-x64.zip -DestinationPath .\SSH-Proxy-Bridge-v0.2.0
```

不要在压缩包预览窗口中直接运行 EXE，也不要只复制 `SshProxyBridge.exe`。EXE、PowerShell 运行引擎、用户手册和其他随包文件需要保留在同一目录。

## 校验 ZIP 的 SHA-256

每个 Release 都会同时提供 ZIP 和对应的 `.sha256` 文件。下载后，在 ZIP 所在目录打开 PowerShell：

```powershell
Get-FileHash .\SSH-Proxy-Bridge-v0.2.0-win-x64.zip -Algorithm SHA256
Get-Content .\SSH-Proxy-Bridge-v0.2.0-win-x64.zip.sha256
```

两处显示的 SHA-256 应完全一致。如果不一致，请删除文件并重新从本项目 Releases 页面下载。

## 校验解压后的 EXE

发行包中包含 `SHA256SUMS.txt`。进入解压目录后执行：

```powershell
Get-FileHash .\SshProxyBridge.exe -Algorithm SHA256
Get-Content .\SHA256SUMS.txt
```

两处显示的 `SshProxyBridge.exe` SHA-256 应完全一致。

## Windows SmartScreen

当前版本没有商业代码签名证书，因此 Windows 可能显示“未知发布者”。

请先确认：

- 下载地址来自 `github.com/zhourunnan1210/ssh-proxy-bridge`。
- ZIP 的 SHA-256 与 Release 中的校验文件一致。
- 解压后的 EXE 与 `SHA256SUMS.txt` 一致。

确认后，可以在 SmartScreen 窗口中点击“更多信息”→“仍要运行”。如果下载来源或校验结果不明确，请不要运行。

## 发行包内容

正常发行包至少包含：

```text
SshProxyBridge.exe
ssh-proxy-bridge.ps1
USER_GUIDE.md
PORTABLE_README.txt
config.example.json
LICENSE
THIRD_PARTY_NOTICES.md
SHA256SUMS.txt
```

发行包已经包含 .NET 运行时，目标电脑不需要安装 .NET SDK。
