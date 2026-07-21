# 脚本目录

## `build/`

维护和发布使用。`publish-portable.ps1` 会从项目文件读取版本号，生成当前 Windows 便携包，并同步根目录唯一 GUI：`SshProxyBridge.exe`。

## `legacy/`

仅为旧 `config.local.json` 连接保留的兼容工具：公钥引导、诊断和带口令私钥解锁。新建服务器请直接使用 GUI，不需要运行这些脚本。

根目录的 `ssh-proxy-bridge.ps1` 是 GUI 的运行时引擎，必须与根目录 EXE 保持同级，因此没有移动到这里。
