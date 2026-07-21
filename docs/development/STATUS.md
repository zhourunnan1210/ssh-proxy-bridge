# SSH Proxy Bridge 开发状态

> 更新日期：2026-07-21

> 公开项目名已切换为 SSH Proxy Bridge。旧数据目录、凭据前缀、Key 标记和脚本入口通过非破坏性兼容层继续支持；迁移不会停止既有隧道。

## 当前里程碑：新 Profile 可从录入推进到 Ready

- WPF 单主窗口、服务器选择器、三步添加服务器向导。
- 添加、编辑、删除、密码输入、SSH 主机指纹确认和通用通知均已改为主窗口内嵌遮罩面板；完成或取消后原位返回，不创建辅助窗口。
- 内嵌面板打开时对主页面应用高斯模糊并禁用背景交互；添加服务器向导改为高度自适应布局，固定保留底部操作栏，仅滚动中间表单，避免小窗口或高 DPI 缩放时底部被截断。
- 多 Profile 数据模型、字段校验、版本化 JSON 和原子写入。
- SSH.NET 2025.1.0 密码认证与 SHA256 主机指纹确认、锁定。
- Windows Credential Manager 的保存、读取和删除封装。
- 每服务器专用 ED25519 Key 的幂等生成和 NTFS ACL 收紧。
- 固定主机指纹的公钥幂等安装与 Key-only 登录验证。
- 自动识别仅公布 `password` 认证的 SSH 网关，并切换到受控 AskPass 密码模式；支持用户名中包含 `@` 的云平台 SSH 账号。
- AskPass 运行时只携带 Windows Credential Manager 的凭据目标，密码不会进入配置、命令行、环境变量、临时文件或日志。
- 应用专属 `known_hosts`、无密码运行配置和 SSH Config 托管块。
- `PasswordVerified -> Ready` 的界面初始化流程。
- Ready Profile 可复用现有 PowerShell 引擎建立隧道并打开指定 VS Code 远程目录。
- 新 Profile 使用独立状态目录，旧 `config.local.json` 继续使用原 `.state`。
- 初始化时通过固定主机指纹的 Key-only 或密码网关 SSH 会话检测远端端口，并自动选择、持久化可用端口。
- Profile 编辑界面支持名称、本机代理和默认远程目录等非 SSH 身份设置。
- Profile 删除界面支持安全停止自身隧道、删除凭据、删除托管 SSH Config、本地运行目录及可选专用 Key。
- 删除专用 Key 前验证安全目录、文件名和 Profile 公钥标记；归属不明确时拒绝删除。
- GUI 字体统一为 Microsoft YaHei UI，并保留 Microsoft YaHei 回退。
- 服务器选择时异步探测本机代理 TCP 端口，并使用容忍任意空白的解析器识别 `Proxy:  running`，防止启动状态刷新把绿色圆点覆盖为灰色。
- 诊断入口移动到当前服务器操作行。
- GUI 使用说明改为主窗口内嵌 FlowDocument 用户手册，不展示开发 PRD、不调用外部程序、不创建阅读弹窗。
- GUI 启动脚本改为启动前增量构建，并阻止重复启动旧版进程。
- VS Code 启动时将标准输出和错误重定向到 Profile 状态目录，避免电脑重启后的首个 VS Code 进程继承 GUI 输出管道；GUI 侧另设三秒输出收尾上限，后台命令结束后不会无限停留在“处理中”。
- 一次性 SSH 检查显式启用 `ClearAllForwardings=yes`，不继承用户 SSH Config 中无关的端口转发；原生 SSH 子进程设置 20–35 秒硬超时，GUI 工作流另设 45 秒至 3 分钟的整体兜底上限。
- 已提供 Windows x64 自包含单文件发布配置和 `scripts/build/publish-portable.ps1`：复制运行脚本/手册、排除个人 `config.local.json` 与 PDB、生成 EXE/ZIP SHA256，并在发布目录及 ZIP 独立解压目录执行无界面包自检。

## 安全与兼容性结论

- 密码不进入 Profile JSON、运行时 JSON、日志、命令行或临时文件。
- 凭据仅在密码认证成功后按用户选择保存；初始化时也只有完整成功后才更新保存状态。
- `known_hosts` 使用 SSH.NET 观察到并经用户确认的原始主机公钥，后续 OpenSSH 调用启用严格校验。
- 公钥安装不会覆盖现有 `authorized_keys`。
- 新 Profile 初始化和状态文件不会停止或接管当前旧连接。
- PowerShell 的 `setup`/`start` 会先安装 SSH 别名，再按 Profile 的认证模式进行 Key-only 或受控密码登录检查。

## 已验证

```text
Release build:     0 warnings, 0 errors
Core tests:        18/18 passed
PowerShell parser: passed
Real SSH status:   passed for key and password-gateway profiles; password-gateway remote proxy returned HTTP 204
Embedded UI:       passed (all surfaces, 63 guide blocks, proxy status, AskPass mode, bounded process-output drain)
Portable package:  passed (self-contained win-x64, clean ZIP extraction and package self-check)
```

测试覆盖：

- 有效和无效 Profile 校验。
- Profile JSON 往返与无密码检查。
- Profile 作用域的 Credential Target。
- SSH SHA256 指纹规范化。
- 含双空格/制表符的代理运行状态识别，以及 `not ready` 防误判。
- 远端端口候选顺序、去重与返回标记解析。
- 一键 ED25519 Key 生成的幂等性。
- `runtime.json` 与 `known_hosts` 生成及无密码检查。
- Profile 本地清理不会删除无关 SSH Config 内容。
- 无法证明归属的私钥会被保留。
- SSH Config 托管标记不完整时拒绝改写原文件。
- 随机虚拟 Windows Credential 的写入、读取和最终清理。
- 密码网关认证能力识别、运行时字段和 AskPass 自检。

## 下一阶段建议

1. 增加服务器端公钥/代理块的显式安全卸载，并输出远程失败时的手工清理命令。
2. 增加密码替换、单独删除凭据和 SSH 身份变更/主机指纹重新确认流程。
3. 在无活动隧道时为每次连接增加端口复检与安全重选。
4. 增加诊断结果结构化展示、取消操作和更明确的错误恢复指引。
5. 设计带口令 Key 的受控 AskPass/ssh-agent 流程。
6. 补充应用图标、代码签名与自动升级策略。

当前开发和验证没有执行旧 Profile 的 `stop`、`setup`、`bootstrap-key` 或 `uninstall`。
