# Contributing

感谢参与 SSH Proxy Bridge。

## 开发环境

- Windows 10/11
- .NET 8 SDK
- Windows OpenSSH Client
- VS Code（进行真实 Remote-SSH 验证时需要）

## 提交流程

1. 从新分支开始开发，保持改动聚焦。
2. 不要提交真实服务器资料、密码、私钥、`config.local.json`、`.state/` 或发布产物。
3. 为行为变化增加或更新测试。
4. 在 `tools/codex-remote-proxy` 目录运行：

```powershell
dotnet run --project .\app\SshProxyBridge.Core.Tests\SshProxyBridge.Core.Tests.csproj --configuration Release
dotnet build .\app\SshProxyBridge.App\SshProxyBridge.App.csproj --configuration Release
```

5. 涉及 PowerShell 引擎时，至少执行解析检查；只有在你自己的测试服务器上进行真实 SSH 测试。
6. Pull Request 中说明用户影响、安全影响、验证方式和向后兼容性。

## 设计约束

- 不在普通文件、日志或命令行中传递服务器密码。
- 不覆盖服务器现有 `authorized_keys` 或用户 SSH Config。
- 不因应用启动、升级或数据迁移停止现有隧道。
- 删除和卸载操作必须验证资源归属与路径边界。
- GUI 操作保持在主窗口内完成。

参与本项目即表示你同意遵守 [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)。
