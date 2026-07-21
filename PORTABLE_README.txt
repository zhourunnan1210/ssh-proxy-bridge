SSH Proxy Bridge 便携版
========================

启动方式
--------
双击 SshProxyBridge.exe。

这是 Windows x64 自包含版本，目标电脑不需要另外安装 .NET。请保留 EXE、
ssh-proxy-bridge.ps1 和 USER_GUIDE.md 在同一个文件夹中，不要只复制 EXE。

目标电脑仍需具备
--------------
1. Windows 10/11 x64 与系统 PowerShell 5.1。
2. Windows OpenSSH Client（ssh.exe、ssh-keygen.exe）。
3. VS Code、Remote - SSH 扩展，以及需要联网的远程扩展（例如 Codex）。
4. 可用的 Windows 代理软件。

首次使用
--------
1. 启动 Windows 代理软件。
2. 双击 SshProxyBridge.exe。
3. 点击“添加服务器”，在 GUI 中完成服务器录入和 SSH 初始化。
4. 点击“连接并打开 VS Code”。

自动修复
--------
连接成功后，程序会启动独立的后台监控。GUI 关闭后它仍会检查受管 SSH 隧道，
并在网络、本机代理和 SSH 恢复可用后自动重建。点击“停止连接”会同时停止监控；
需要立即检查时可点击 GUI 中的“修复隧道”，该操作不会重复打开 VS Code。

配置与密码
----------
应用创建的 Profile 保存在：
%LOCALAPPDATA%\SshProxyBridge

选择保存的服务器密码只进入当前 Windows 用户的 Credential Manager，不会写入发布目录。
卸载便携版时，删除程序文件夹不会自动删除上述用户数据或 Windows 凭据。

旧版 config.local.json
----------------------
通用发布包不会包含开发电脑上的 config.local.json，以免泄露服务器信息。
如需在同一台电脑继续显示旧的“当前连接”，可自行把原 config.local.json 复制到本目录。
新电脑建议直接使用 GUI 的“添加服务器”。

Windows 安全提示
----------------
当前版本尚未使用商业代码签名证书。Windows SmartScreen 可能显示“未知发布者”。
请只运行由可信渠道获得且 SHA256 与 SHA256SUMS.txt 一致的发布包。
