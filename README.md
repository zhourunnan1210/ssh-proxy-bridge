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

1. 下载 `SSH-Proxy-Bridge-v<version>-win-x64.zip` 并完整解压。
2. 双击 `SshProxyBridge.exe`。
3. 点击“添加服务器”，填写 SSH、本地代理和远程项目目录。
4. 核对服务器主机指纹并完成 SSH 初始化。
5. 点击“连接并打开 VS Code”。

发行包自带 .NET 运行时，目标电脑不需要另外安装 .NET。

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
