# SSH Proxy Bridge 开发说明

这是一个面向 Windows + VS Code Remote-SSH 场景的 WPF 原型。它把本机代理通过 SSH 反向隧道提供给服务器，并直接在 VS Code 中打开指定的远程目录。

## 启动

日常只双击工具根目录中的：

```text
SshProxyBridge.exe
```

首次拉取源码、根目录尚无 EXE，或代码修改后需要重建时，运行：

```text
start-ssh-proxy-bridge-gui.cmd
```

启动脚本会生成最新的 Windows x64 自包含单文件程序，把它同步为根目录唯一日常入口，再打开应用；检测到同一入口已运行时不会重复启动。`app/**/bin`、`app/**/obj` 和 `release` 都不是日常入口。

## 新服务器的完整流程

1. 点击“添加服务器”，填写 SSH 地址、端口、用户名、服务器密码、本机代理端口和远程项目目录。
2. 应用显示 SSH SHA256 主机指纹。应先核对指纹，再选择信任。
3. 密码认证成功后，Profile 进入“密码已验证”状态。若选择保存密码，密码只写入 Windows Credential Manager。
4. 选中该 Profile，点击“完成 SSH 初始化”。应用会探测认证方式：普通服务器生成独立 ED25519 Key 并验证 Key-only 登录；只提供 `password` 的云平台网关切换到受控 AskPass 模式。
5. 初始化成功后，点击“连接并打开 VS Code”。应用会建立反向隧道、验证服务器代理、写入受管的远程 Shell 代理块，并打开配置的远程目录。

初始化时应用会先检查用户填写的服务器代理端口。若该端口已被旧隧道或其他程序占用，会从该 Profile 的配置范围内选择下一个可绑定端口，并把结果保存到 Profile 和运行配置。

## 编辑与删除

- “编辑”可修改服务器名称、本机代理、代理程序路径和默认远程目录，不会改变 SSH 地址、账号、主机指纹、专用 Key 或当前隧道。
- 代理设置修改后，应先停止该 Profile 的连接，再重新连接使新设置生效。
- “删除”只对应用创建的 Profile 开放；旧 `config.local.json` 对应的“当前连接”不能从这里删除。
- Ready Profile 删除前必须先安全停止自己的受管隧道。
- 删除时可以选择清理 Windows 凭据、SSH Config 托管块及专用 Key；运行配置和状态目录始终清理。
- 专用 Key 默认保留。只有私钥位于允许管理的 `.ssh` 目录、文件名符合规则且配套公钥包含当前 Profile ID 时，应用才允许删除它。
- 当前版本默认保留服务器 `authorized_keys` 和远程 Shell 代理块，不会在删除本地 Profile 时远程改写服务器文件。

## 界面行为

- GUI 统一使用“微软雅黑 / Microsoft YaHei UI”。
- GUI 采用单主窗口结构。添加、编辑、删除服务器、密码输入、SSH 主机指纹确认以及成功/错误提示均以内嵌遮罩面板呈现；面板打开时高斯模糊并禁用背景，完成或取消后原位恢复，不创建额外窗口。添加向导高度自适应，只滚动中间表单，底部按钮保持可见。
- 选择服务器时会立即对其本机代理地址做短时 TCP 检测：端口正在监听时左下角圆点显示绿色，否则显示灰色。
- “运行诊断”位于当前服务器的操作按钮行，与“刷新状态”“停止连接”处于同一层级。
- 左侧“使用说明”直接把主窗口右侧切换为面向用户的内嵌操作手册，支持标题、列表、代码块、引用和表格；不会展示开发 PRD，不会启动外部软件，也不会弹出新窗口。点击“服务器”可原位切回服务器页面。
- 启动 VS Code 时会把其输出与 GUI 后台管道分离；电脑重启后首次打开 VS Code，也会在启动请求完成后正常从“处理中”切换为“已连接”。
- 状态检查和远程设置使用隔离的一次性 SSH 会话，不继承用户 SSH Config 中已有的端口转发，并设置 20–35 秒子进程硬超时；GUI 工作流也有整体等待上限，不会永久停在“处理中”。

已有的 `config.local.json` 会继续显示为“当前连接”，不会自动迁移或覆盖。应用启动、添加 Profile 和初始化其他 Profile 都不会主动停止这个旧连接；只有在选中它后明确点击“停止连接”才会停止它。

## 已实现的安全边界

- Profile 和运行时 JSON 不包含密码。
- 保存密码使用 Windows Credential Manager，而不是注册表、普通文件、命令行或日志。
- 密码网关由受控 AskPass Helper 按需读取 Profile 凭据，运行配置只包含凭据引用。
- 首次连接要求人工确认主机指纹；后续密码、公钥安装和 Key-only 验证均固定到该 SHA256 指纹。
- 每个新 Profile 使用应用专属 `known_hosts`，并启用 `StrictHostKeyChecking yes`。
- 公钥安装只追加缺失的精确行，不删除或覆盖服务器已有的 `authorized_keys`。
- 一键模式的私钥不设置口令，但通过 NTFS ACL 限制为当前 Windows 用户可访问。
- 新 Profile 的 PID 和隧道日志位于各自运行目录，不与旧连接共用 `.state`。
- 停止隧道前同时核对 PID、`ssh.exe`、SSH 别名和远端代理端口。
- 远端端口探测优先使用 Python socket bind，其次使用 `ss` 或 Bash TCP 探测；真正建隧道时仍由 `ExitOnForwardFailure=yes` 做最终竞争校验。

应用数据位置：

```text
%LOCALAPPDATA%\SshProxyBridge\profiles.json
%LOCALAPPDATA%\SshProxyBridge\profiles\<profile-id>\runtime.json
%LOCALAPPDATA%\SshProxyBridge\profiles\<profile-id>\known_hosts
%LOCALAPPDATA%\SshProxyBridge\profiles\<profile-id>\state\
```

专用 Key 默认位于：

```text
%USERPROFILE%\.ssh\id_ed25519_<profile-alias>
```

## 当前限制

- 自动初始化支持“一键无口令 Key”和密码网关；带口令私钥仍需要后续接入 ssh-agent 交互。
- 编辑暂不支持修改 SSH 地址、用户或重新确认主机指纹；需要更换 SSH 身份时应新建 Profile。
- 删除暂不自动清理服务器端 `authorized_keys` 和远程 Shell 代理块。
- 远端端口自动选择目前发生在首次 SSH 初始化；隧道停止后若端口后来被其他程序占用，连接会安全失败并给出错误，尚不会在每次连接时自动改写端口。
- 当前仍复用 PowerShell 连接引擎，已提供自包含便携包，但尚未提供商业代码签名。

## 开发验证

在 `app` 目录运行：

```powershell
dotnet run --project .\SshProxyBridge.Core.Tests\SshProxyBridge.Core.Tests.csproj --configuration Release
dotnet build .\SshProxyBridge.App\SshProxyBridge.App.csproj --configuration Release
```

当前基线：Core 测试 `18/18` 通过；界面冒烟测试已验证添加、编辑、删除、密码、通知和使用说明均在主窗口内呈现，并验证代理状态、原位导航与 AskPass 凭据读取；Release 构建 `0` 错误。迁移测试验证旧数据只复制不删除，凭据测试只创建随机虚拟凭据，并在 `finally` 中立即删除。

## 生成 Windows 便携版

在工具根目录运行：

```powershell
.\scripts\build\publish-portable.ps1
```

脚本从项目文件自动读取版本号，生成 Windows x64 自包含单文件主程序、必需脚本、用户手册、EXE/ZIP SHA256 和可直接分发的 ZIP，并把同一 EXE 同步到仓库根目录。它会清理本机 `release` 中的历史版本，只保留当前版本。目标电脑不需要安装 .NET。开发电脑的 `config.local.json` 不会进入发布包；发布脚本会用 `--verify-package` 无界面模式验证最终 EXE 和资源完整性。
