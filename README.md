# SSH Proxy Bridge

[![CI](https://github.com/zhourunnan1210/ssh-proxy-bridge/actions/workflows/ci.yml/badge.svg)](https://github.com/zhourunnan1210/ssh-proxy-bridge/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

把 Windows 本地代理带到 VS Code Remote-SSH 远程开发环境。

SSH Proxy Bridge 是一个 Windows 桌面工具。它通过受管 SSH 反向隧道，将只监听在 Windows 本机的 HTTP/HTTPS 代理提供给远程 Linux 服务器，并用 VS Code Remote-SSH 打开指定目录。运行在远程扩展主机中的 Codex 等扩展，以及遵循代理环境变量的远程命令行工具，因而可以使用本地代理联网。

> SSH Proxy Bridge 是独立的开源项目，与 Microsoft 或 OpenAI 没有关联，也未获得其认可或背书。VS Code、Remote-SSH、OpenAI 和 Codex 是其各自权利人的商标或产品名称。

## 工作原理

```text
VS Code 远程扩展 / 远程 CLI
              ↓
Linux 127.0.0.1:<remote-port>
              ↓
     SSH 反向隧道（加密）
              ↓
Windows 127.0.0.1:<local-proxy-port>
              ↓
       本地代理软件 → Internet
```

服务器侧代理入口默认只绑定 `127.0.0.1`，不会直接暴露给服务器外部网络。

## 主要功能

- 单窗口 WPF 界面，管理多个服务器 Profile。
- 检测 Windows 本地代理端口并显示实时状态。
- 首次连接确认 SSH SHA256 主机指纹。
- 可选将服务器密码保存到 Windows Credential Manager。
- 为每个 Profile 生成独立 ED25519 Key，并限制私钥 NTFS ACL。
- 建立、检查和停止受管 SSH 反向隧道。
- 用 VS Code Remote-SSH 直接打开指定 Linux 目录。
- 内嵌用户手册、分层诊断和便携式自包含发行包。
- 兼容旧版 Codex Remote Bridge 数据、凭据和 SSH 托管标记。

## 使用要求

- Windows 10/11 x64。
- Windows OpenSSH Client。
- VS Code 及 Remote - SSH 扩展。
- 能够通过密码或已有密钥登录的 Linux SSH 服务器。
- 服务器允许公钥认证和 TCP 端口转发。
- Windows 上已运行的 HTTP、HTTPS 或 mixed 代理。

## 快速开始

### 使用发行包

#### 1. 下载正确的文件

1. 打开项目的 [Releases 页面](https://github.com/zhourunnan1210/ssh-proxy-bridge/releases)。
2. 选择需要的版本。第一次使用建议下载页面顶部最新的稳定版；标有 `Pre-release` 的版本属于公开预览版，功能可用，但仍可能调整。
3. 在版本页面底部展开 **Assets**，下载名称类似下面的 Windows 发行包：

   ```text
   SSH-Proxy-Bridge-v<版本号>-win-x64.zip
   ```

   不要下载 GitHub 自动生成的 `Source code (zip)` 或 `Source code (tar.gz)`；它们只包含源码，不能直接运行。

#### 2. 校验下载文件（推荐）

每个 Release 的说明中都会列出发行包的 SHA-256。下载完成后，在 ZIP 所在目录打开 PowerShell，例如对 `v0.1.0` 执行：

```powershell
Get-FileHash .\SSH-Proxy-Bridge-v0.1.0-win-x64.zip -Algorithm SHA256
```

确认输出的 `Hash` 与 Release 页面公布的 ZIP SHA-256 完全一致。解压后还可以校验 EXE：

```powershell
Get-FileHash .\SshProxyBridge.exe -Algorithm SHA256
Get-Content .\SHA256SUMS.txt
```

两处的 EXE SHA-256 应当一致。如果校验值不一致，请删除文件并从本项目 Releases 页面重新下载。

#### 3. 完整解压

右键 ZIP，选择“全部解压”，或者使用 PowerShell：

```powershell
Expand-Archive .\SSH-Proxy-Bridge-v0.1.0-win-x64.zip -DestinationPath .\SSH-Proxy-Bridge-v0.1.0
```

不要在压缩包预览窗口中直接运行 EXE，也不要只复制 `SshProxyBridge.exe`。EXE、`ssh-proxy-bridge.ps1`、`USER_GUIDE.md` 和其他随包文件需要保留在同一目录。

发行包已经包含 .NET 运行时，目标电脑不需要另外安装 .NET。开始前仍需准备：

- Windows 10/11 x64 和 Windows OpenSSH Client。
- VS Code、Remote - SSH 扩展，以及需要在服务器使用的远程扩展，例如 Codex。
- 已启动的 Windows HTTP、HTTPS 或 mixed 代理，并知道它监听的地址和端口，例如 `127.0.0.1:7897`。
- Linux 服务器的 SSH 地址、端口、用户名和密码；服务器需要允许公钥认证和 TCP 端口转发。

#### 4. 第一次启动

双击解压目录中的：

```text
SshProxyBridge.exe
```

当前版本没有商业代码签名证书，Windows SmartScreen 可能显示“未知发布者”。只有在确认下载地址来自本项目 Releases 页面、并且 SHA-256 校验通过后，才点击“更多信息”→“仍要运行”。

应用打开后，先查看左下角“本机代理”状态：

- 绿色：配置的代理端口正在监听，可以继续连接。
- 灰色：先启动代理软件，或在服务器配置中核对代理地址和端口。

#### 5. 添加服务器

点击右上角“添加服务器”，按照三步向导填写：

1. **SSH 服务器**：填写便于识别的服务器名称、SSH 地址、端口、Linux 用户和服务器密码。密码只用于验证身份和安装公钥；选择“保存密码”时，密码写入当前 Windows 用户的 Credential Manager，不会写入普通配置文件。
2. **Windows 本机代理**：地址通常填写 `127.0.0.1`，端口填写代理软件的实际监听端口；不确定代理协议时选择“自动检测”。
3. **项目与安全选项**：远程目录填写 Linux 绝对路径，例如 `/workspace/project`；普通用户建议保留“一键模式”和默认服务器代理端口。

首次连接时，应用会显示服务器 SSH 主机密钥类型和 SHA-256 指纹。请通过云服务商控制台或服务器管理员提供的信息核对指纹；无法确认时不要继续。

密码和主机指纹验证成功后，服务器状态会变为“密码已验证”。选择该服务器并点击“完成 SSH 初始化”，应用会：

1. 为这个服务器 Profile 创建独立的 ED25519 Key。
2. 把公钥加入服务器的 `~/.ssh/authorized_keys`。
3. 验证无需服务器密码的 Key-only 登录。
4. 检查并选择可用的服务器代理端口。
5. 生成应用专属的 `known_hosts`、SSH Config 和运行配置。

全部成功后，服务器状态会显示“可以连接”。重复初始化不会重复写入相同公钥。

#### 6. 连接并打开远程项目

1. 确认 Windows 代理软件正在运行。
2. 在 SSH Proxy Bridge 中选择目标服务器。
3. 确认左下角本机代理圆点为绿色。
4. 点击“连接并打开 VS Code”。

应用会建立或复用 SSH 反向隧道，从服务器验证代理是否可用，更新受管的远程代理环境，然后让 VS Code Remote-SSH 打开配置的远程目录。VS Code 打开后，SSH Proxy Bridge 应显示“已连接”；重复点击不会叠加新的受管隧道进程。

以后日常使用通常只需要：启动代理软件 → 打开 SSH Proxy Bridge → 选择服务器 → 点击“连接并打开 VS Code”。遇到问题时，先点击“运行诊断”，再参阅[完整用户手册](USER_GUIDE.md)中的“常见问题”。

### 从源码运行

需要 .NET 8 SDK。在项目根目录执行：

```powershell
.\start-ssh-proxy-bridge-gui.cmd
```

也可以直接构建：

```powershell
dotnet build .\app\SshProxyBridge.App\SshProxyBridge.App.csproj --configuration Release
```

## 生成 Windows 便携版

```powershell
.\publish-portable.ps1 -Version 0.1.0
```

输出位于 `release/`，包含自包含 EXE、运行引擎、用户手册、许可证、SHA256 校验文件和 ZIP 包。

## 数据与向后兼容

新版本数据默认位于：

```text
%LOCALAPPDATA%\SshProxyBridge\
```

首次启动会把 `%LOCALAPPDATA%\CodexRemoteBridge` 中尚未存在于新目录的稳定文件复制到新目录：

- 不删除或覆盖旧目录。
- 不复制可能仍由进程写入的日志文件。
- 保留旧 Windows Credential，首次读取时再复制到新凭据前缀。
- 继续识别旧 SSH Key 与 SSH Config 托管标记。
- 迁移过程不会停止或重启已运行的 SSH 隧道。

旧的脚本和 CMD 文件名保留为兼容入口；新代码应使用 `ssh-proxy-bridge.ps1` 和 `*-ssh-proxy-bridge*.cmd`。

## 安全边界

- Profile、运行 JSON、日志和命令行中不保存服务器密码。
- 保存的密码只写入当前 Windows 用户的 Credential Manager。
- 首次连接需要人工确认服务器主机指纹。
- 后续 OpenSSH 连接启用严格主机密钥检查。
- 公钥安装只追加缺失的精确公钥，不覆盖 `authorized_keys`。
- 一键模式私钥不设置口令，依赖 Windows 账户和 NTFS ACL；共享 Windows 账户不适合使用该模式。

不要提交 `config.local.json`、`.state/`、日志、真实服务器地址、用户名、私钥或密码。发现安全问题请参阅 [SECURITY.md](SECURITY.md)。

## 文档

- [用户手册](USER_GUIDE.md)
- [开发说明](app/README.md)
- [产品需求文档](PRODUCT_REQUIREMENTS.md)
- [贡献指南](CONTRIBUTING.md)
- [第三方组件](THIRD_PARTY_NOTICES.md)

## English summary

SSH Proxy Bridge is a Windows desktop application that exposes a local proxy to remote Linux development servers through a managed SSH reverse tunnel, then opens a configured folder with VS Code Remote-SSH. It is designed for remote extensions and CLI tools that honor HTTP/HTTPS proxy settings, including Codex.

## License

[MIT](LICENSE) © 2026 SSH Proxy Bridge contributors.
