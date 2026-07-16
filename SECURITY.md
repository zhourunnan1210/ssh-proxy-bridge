# Security Policy

## 报告安全问题

请优先使用 GitHub 仓库的 **Security → Report a vulnerability** 私下报告漏洞，不要在公开 Issue 中提交以下内容：

- 服务器密码、私钥或完整公钥。
- 真实服务器地址、用户名和可访问端口。
- Windows Credential、配置文件或未经处理的日志。
- 可直接复现未授权访问的完整利用代码。

报告应包含受影响版本、影响范围、最小复现步骤和建议缓解措施。维护者确认问题并准备修复前，请避免公开披露。

## 安全模型

SSH Proxy Bridge 假设 Windows 用户账户、本机代理软件和目标 SSH 服务器均由用户信任。应用负责限制自身写入范围、固定 SSH 主机指纹、保护受管私钥，并把远端代理入口绑定到 loopback。

以下情况不在安全保证范围内：

- Windows 账户或服务器 root 权限已经失陷。
- 用户忽略主机指纹不匹配并信任错误服务器。
- 本地代理软件本身恶意或对局域网公开监听。
- 用户手工修改受管配置、PID 或 SSH Key 后继续强制运行。

## 敏感文件

提交代码前请确认这些内容未被纳入版本控制：

```text
config.local.json
.state/
*.log
*.key
id_ed25519*
release/
```
