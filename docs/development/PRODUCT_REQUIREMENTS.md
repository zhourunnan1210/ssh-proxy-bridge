# SSH Proxy Bridge for Windows 产品需求文档

> 文档状态：Draft v1.0（开发文档）
> 日期：2026-07-12
> 目标平台：Windows 10/11 + VS Code Remote-SSH + Linux SSH Server

## 1. 产品摘要

SSH Proxy Bridge 是一个 Windows 桌面应用，用于把 Windows 本机代理通过 SSH 反向隧道安全地提供给远程 Linux 服务器，使运行在 VS Code Remote-SSH 环境中的 Codex IDE 扩展能够访问所需网络。

用户首次添加服务器时，应用通过向导收集代理和 SSH 信息，验证服务器身份，选择性地将服务器密码保存到 Windows 凭据管理器，并自动选择专用 SSH 公钥或密码网关模式，再配置远程代理环境。完成首次配置后，用户只需点击“连接并打开 VS Code”，应用即可启动代理、建立隧道、验证网络并打开指定的远程目录。

最终交付物必须是有图形界面的 Windows 应用。所谓“核心”或“引擎”仅指界面背后的连接逻辑，不代表产品采用无界面形式。MVP 从第一个可运行版本开始就提供简单、完整、可日常使用的 GUI。

产品的现实承诺是：

> 首次向导式配置，之后一键连接、自动维护、直接打开远程项目。

## 2. 背景与问题

VS Code Remote-SSH 会在远程主机上运行 VS Code Server，并把大多数工作区扩展安装和运行在远程主机上。因此，即使 Windows 本机可以通过 Clash Verge、v2rayN、Hiddify 等软件访问网络，远程 Codex 扩展也不会自动继承 Windows 的代理。

当前已验证的技术链路是：

```text
Remote Codex / Remote Extension Host
  -> server 127.0.0.1:<remoteProxyPort>
  -> encrypted SSH reverse tunnel
  -> Windows 127.0.0.1:<localProxyPort>
  -> local proxy application
  -> Internet
```

现有 PowerShell 原型已验证以下能力：代理探测、SSH Key 初始化、SSH Config 托管、反向隧道、远程环境变量配置、代理连通性测试、VS Code Remote-SSH 启动和指定目录打开。Windows 应用是在该原型之上做工程化和产品化，而不是重新验证基本原理。

## 3. 产品目标

### 3.1 核心目标

1. 支持一个 Windows 用户管理多台 SSH 服务器。
2. 支持保存服务器密码，但禁止把密码写入 JSON、日志、命令行或 SSH Config。
3. 首次连接自动完成服务器身份确认、SSH 公钥安装和远程代理配置。
4. 日常使用能够一键建立隧道并打开指定 VS Code 远程目录。
5. 隧道断开后自动检测和重连，并向用户展示真实状态。
6. 所有远程和本地配置均可识别、幂等、回滚和卸载。
7. 提供足够清晰的一键诊断，使普通用户能够定位代理、SSH、隧道或 VS Code 层面的故障。

### 3.2 成功指标

- 在已经完成首次配置的机器上，从点击“连接”到发起 VS Code 打开的中位时间不超过 10 秒，不计 VS Code Server 自身升级时间。
- 正常网络环境下，连续启动 20 次不产生重复 SSH Config 或重复远程环境配置。
- 网络短暂中断后能够在可配置时间内恢复隧道，默认 30 秒内开始重连。
- 日志和导出的诊断包中不出现服务器密码、私钥口令或完整凭据内容。
- 删除服务器配置时，用户可以同时删除对应 Windows 凭据和托管配置。

## 4. 非目标

第一版不负责：

- 绕过服务器管理员禁止的 `AllowTcpForwarding` 或其他安全策略。
- 自动完成 ChatGPT/Codex 网页登录、验证码或双因素认证。
- 把服务器密码作为命令行参数传给 `ssh.exe`、`plink.exe` 或其他进程。
- 在纯密码认证模式下完全无提示地驱动 VS Code Remote-SSH。
- 修改系统级 `/etc/environment`、全局 `sshd_config` 或服务器其他用户的配置。
- 为不可信的多人共享服务器提供强隔离代理服务。
- 第一版支持 macOS、Linux 桌面端、移动端或集中式企业管理后台。

## 5. 目标用户与典型场景

### 5.1 目标用户

- Windows 上使用 VS Code Remote-SSH 的个人开发者。
- 本机已有可用代理，但远程 Linux 服务器无法直接访问 Codex 服务的用户。
- 同时管理多台 GPU、云服务器或实验室服务器的科研和工程用户。

### 5.2 典型场景

#### 场景 A：首次添加服务器

用户输入服务器名称、IP、端口、用户名和密码，选择本机代理端口。应用测试密码登录，展示 SSH 主机指纹，用户确认后选择“保存密码”。应用生成专用密钥、安装公钥、建立隧道、写入托管代理配置，最后打开指定目录。

#### 场景 B：日常开发

用户打开托盘菜单，点击某台服务器的“连接并打开 VS Code”。应用检查代理、SSH Key、远程端口和隧道，验证成功后打开默认项目目录。

#### 场景 C：临时打开其他目录

用户在服务器卡片中点击“打开其他目录”，输入或从远程目录历史中选择绝对路径。本次启动使用该路径，但不强制修改默认目录。

#### 场景 D：密码变化或公钥失效

应用发现 Key 登录失败，提示用户使用已保存密码重新安装公钥。若已保存密码也失效，则弹出密码输入框；只有成功登录后才允许覆盖旧凭据。

#### 场景 E：网络切换或系统休眠

应用检测隧道进程退出或远程探针失败，进入“正在重连”状态。恢复本地代理和网络后自动重建隧道，不重复修改配置。

## 6. 关键产品决策

### 6.1 独立 Windows 应用，而不是 VS Code/Codex 插件

桥接必须在远程 Codex 可用之前建立，并且需要管理 Windows 代理进程、OpenSSH、`ssh-agent`、凭据管理器和 VS Code 启动。独立应用更适合作为基础设施层，VS Code 仅作为最终启动目标。

### 6.2 普通服务器优先使用 SSH Key，密码网关受控回退

服务器密码的用途：

- 首次测试 SSH 登录。
- 安装或修复 `authorized_keys`。
- 密钥失效时进行恢复。
- 执行需要密码认证的诊断操作。

普通服务器的日常一键连接使用专用 SSH Key。对于 SSHPiper 等只公布 `password`、不允许终端用户安装可用公钥的云平台网关，应用切换到密码网关模式：OpenSSH 通过受控 AskPass Helper 从 Windows Credential Manager 即时读取该 Profile 的凭据。VS Code Remote-SSH 会继承启动时的 AskPass 环境；若复用已运行的 VS Code 进程而无法继承，仍允许显示其标准密码输入框。

专用 Key 提供两种明确模式：

- “一键模式”：生成无口令的每服务器专用 Key，以严格的当前用户 NTFS ACL 保护；适合个人受控电脑，Windows 重启后无需再次输入私钥口令。
- “增强保护模式”：Key 带口令，由 `ssh-agent` 缓存；Windows 重启或 Agent 清空后可能需要用户再次输入口令。

应用必须在向导中解释两种模式的取舍，不能把“无口令 Key”包装成与加密 Key 等价的安全等级。无论哪种模式，都不得复用用户已有的通用身份密钥作为默认值。

### 6.3 密码只进入 Windows 凭据管理器

第一版使用 Win32 Credential Manager：

- 类型：`CRED_TYPE_GENERIC`。
- 持久性：`CRED_PERSIST_LOCAL_MACHINE`，仅在当前 Windows 用户的同一台电脑后续登录会话中可见，不采用跨设备漫游。
- Target Name：`SshProxyBridge:ssh-password:<profileId>`。
- JSON 中只保存 `credentialRef` 和 `hasStoredCredential`，绝不保存密码本身。

必须向用户说明：凭据管理器可以防止配置文件和普通日志泄露，但不能防御已经获得当前 Windows 用户权限的恶意程序。

### 6.4 默认不使用 root

应用允许用户配置 root，但显示风险提示。默认推荐普通用户，所有远程改动限定在该用户的 Home 目录。除非用户明确发起管理员修复流程，否则应用不得修改 `sshd_config` 或调用 `sudo`。

### 6.5 低成本的原生 Windows 界面

MVP 使用单窗口 WPF，不使用 Electron、WebView 前端或复杂动画框架。通过原生控件、统一 ResourceDictionary、系统字体和少量 Fluent 风格图标实现简洁外观。设计优先级依次是：连接状态清楚、操作路径短、错误可理解、视觉一致；不为“看起来高级”引入仪表盘图表、装饰性动画或大量第三方 UI 依赖。

## 7. 功能需求

优先级定义：P0 为 MVP 必须，P1 为正式版重要，P2 为后续增强。

### 7.1 首次运行与本机检查

| ID | 优先级 | 需求 |
|---|---:|---|
| FR-001 | P0 | 检测 Windows 版本、CPU 架构、OpenSSH Client、VS Code 和 Remote-SSH 扩展。 |
| FR-002 | P0 | 缺少 OpenSSH 或 Remote-SSH 时给出可执行修复建议；安装系统组件前必须请求确认。 |
| FR-003 | P0 | 检测 `ssh-agent` 服务状态；启用或修改启动类型需要 UAC，且必须解释原因。 |
| FR-004 | P0 | 支持便携模式和安装模式；用户配置默认存入 `%LOCALAPPDATA%\SshProxyBridge`。 |
| FR-005 | P1 | 支持开机启动和仅最小化到托盘，默认关闭，由用户选择开启。 |

### 7.2 代理检测与管理

| ID | 优先级 | 需求 |
|---|---:|---|
| FR-010 | P0 | 用户可手动填写本机代理 Host、Port 和协议。Host 默认 `127.0.0.1`。 |
| FR-011 | P0 | 协议支持 `Auto`、`HTTP`、`SOCKS5` 和 `Mixed`；Auto 必须通过实际探针判断，而不是只看进程名。 |
| FR-012 | P0 | 使用 TCP 探针和 HTTP 204 探针分别判断端口监听与实际代理可用性。 |
| FR-013 | P0 | 支持配置代理程序路径，并在端口未监听时尝试启动。 |
| FR-014 | P1 | 提供 Clash Verge、v2rayN、Hiddify 的进程和常见安装路径适配器。 |
| FR-015 | P1 | 发现代理监听 `0.0.0.0` 或 `[::]` 时提示“允许局域网”风险，但不擅自修改代理软件配置。 |
| FR-016 | P2 | 支持用户自定义代理探针 URL 和超时。 |

### 7.3 服务器配置与凭据

| ID | 优先级 | 需求 |
|---|---:|---|
| FR-020 | P0 | 服务器配置包含名称、Host、Port、User、认证方式、默认远程目录和备注。 |
| FR-021 | P0 | 密码框支持“仅本次使用”和“保存到 Windows 凭据管理器”。默认不勾选保存。 |
| FR-022 | P0 | 只有一次 SSH 认证成功后才能保存或更新密码。 |
| FR-023 | P0 | 支持查看“已保存/未保存”状态、替换密码和删除密码，但界面永不回显完整密码。 |
| FR-024 | P0 | 服务器配置导出时只包含凭据引用状态，不导出任何密码或私钥。 |
| FR-025 | P2 | 研究通过受控 AskPass Helper 保存和使用私钥口令；在完成安全审计前不得自动把口令传给 `ssh-add`。 |
| FR-026 | P1 | 支持复制配置为新服务器，并自动生成新的 Profile ID、SSH Alias 和凭据引用。 |

### 7.4 SSH 主机身份验证

| ID | 优先级 | 需求 |
|---|---:|---|
| FR-030 | P0 | 首次连接显示主机密钥类型与 SHA256 指纹，必须由用户确认后才能继续。 |
| FR-031 | P0 | 用户确认后保存指纹到 Profile，并把一致的主机密钥写入本机 `known_hosts`。 |
| FR-032 | P0 | 后续指纹发生变化时必须中止连接，禁止自动接受或使用 `StrictHostKeyChecking=no`。 |
| FR-033 | P0 | “替换主机指纹”必须是显式高风险操作，并展示旧、新指纹。 |
| FR-034 | P1 | 支持 ED25519、ECDSA 和 RSA 主机密钥，优先显示服务器协商使用的密钥。 |

### 7.5 SSH Key 初始化

| ID | 优先级 | 需求 |
|---|---:|---|
| FR-040 | P0 | 默认为每个服务器 Profile 创建独立 ED25519 私钥；向导必须让用户选择“一键无口令 Key”或“带口令 Key”。文件名不含密码或敏感信息。 |
| FR-041 | P0 | 使用 Windows `ssh-keygen.exe` 生成密钥，私钥目录和文件 ACL 必须通过检查。 |
| FR-042 | P0 | 使用密码认证把公钥幂等地加入远程 `~/.ssh/authorized_keys`。 |
| FR-043 | P0 | 普通服务器安装后必须通过 `BatchMode=yes` 验证 Key-only 登录；服务器仅公布 `password` 时必须切换到密码网关模式并验证受控 AskPass 登录，二者均失败则首次配置不算完成。 |
| FR-044 | P0 | 托管 SSH Config 使用唯一标记块；重复执行不得产生重复条目。 |
| FR-045 | P1 | 支持选择现有私钥，不强制为每台服务器生成新密钥。 |
| FR-046 | P1 | 支持把加密私钥添加到 Windows `ssh-agent`；第一版由系统交互提示接收私钥口令，不自动注入。 |

### 7.6 SSH 反向隧道

| ID | 优先级 | 需求 |
|---|---:|---|
| FR-050 | P0 | 建立 `remote 127.0.0.1:remotePort -> local 127.0.0.1:proxyPort` 的反向转发。 |
| FR-051 | P0 | 远程监听地址必须默认为 `127.0.0.1`，禁止默认绑定 `0.0.0.0`。 |
| FR-052 | P0 | 启动时使用等价于 `ExitOnForwardFailure=yes`、`ServerAliveInterval` 和 `ServerAliveCountMax` 的配置。 |
| FR-053 | P0 | 检测远程端口冲突，并从配置范围内自动选择可用端口；选定结果持久化到 Profile。 |
| FR-054 | P0 | 每个 Profile 同一时间最多存在一个受管隧道。重复点击连接必须复用或健康检查，不得叠加进程。 |
| FR-055 | P0 | 监听隧道进程退出、SSH keepalive 失败和远程代理探针失败。 |
| FR-056 | P1 | 使用指数退避自动重连，默认 2、5、10、30 秒，之后每 60 秒；用户可停止。 |
| FR-057 | P1 | Windows 休眠恢复和网络地址变化后主动重新验证全部活动 Profile。 |
| FR-058 | P1 | 支持同时维护多台服务器，但每台服务器状态、日志和取消令牌相互隔离。 |

### 7.7 远程环境配置

| ID | 优先级 | 需求 |
|---|---:|---|
| FR-060 | P0 | 检测远程操作系统、Home、默认 Shell、`curl` 和端口转发能力。 |
| FR-061 | P0 | 第一版支持 Linux + Bash；不满足条件时停止并给出明确原因。 |
| FR-062 | P0 | 代理变量包含 `HTTP_PROXY`、`HTTPS_PROXY`、`http_proxy`、`https_proxy` 和合理的 `NO_PROXY/no_proxy`。 |
| FR-063 | P0 | 远程 Shell 配置使用带版本和 Profile ID 的托管块，修改前保存备份。 |
| FR-064 | P0 | 新 Shell 只有在远程代理端口可连接时才导出代理变量，避免隧道关闭后永久破坏普通网络命令。 |
| FR-065 | P0 | 配置完成后从服务器执行代理探针；TCP 可达但外网失败时显示为“隧道已建立，代理不可用”。 |
| FR-066 | P1 | 支持 Zsh，并为 Bash、Zsh 分别选择正确的启动文件。 |
| FR-067 | P1 | 支持只为 VS Code/Codex 配置代理和“为所有远程 Shell 配置代理”两种模式。后者必须解释影响。 |
| FR-068 | P1 | 支持远程配置升级和版本迁移；未知或被用户修改的托管块不得静默覆盖。 |

### 7.8 VS Code 集成

| ID | 优先级 | 需求 |
|---|---:|---|
| FR-070 | P0 | 使用本地 `code` 命令打开 `vscode-remote://ssh-remote+<alias><absolutePath>` 对应的远程目录。 |
| FR-071 | P0 | 每个 Profile 保存一个默认远程目录，并允许本次启动临时覆盖。 |
| FR-072 | P0 | 打开前验证远程目录存在；不存在时允许用户创建或重新选择。 |
| FR-073 | P0 | 检测本地 Remote-SSH 和 Codex 扩展；远程扩展安装状态由 VS Code 连接后继续检查。 |
| FR-074 | P1 | 支持配置 `remote.SSH.defaultExtensions`，但修改 VS Code 用户设置前显示变更内容并获得确认。 |
| FR-075 | P1 | 检测扩展磁盘版本变化导致的 Reload Window 提示，并在状态页解释这是扩展更新，不是代理故障。 |
| FR-076 | P2 | 支持 VS Code Stable、Insiders 和用户指定的兼容发行版。 |

### 7.9 界面与交互设计

MVP 只需要一个主窗口、一个添加/编辑服务器向导和一个系统托盘入口。

主窗口建议布局：

```text
┌──────────────────────────────────────────────────────────┐
│ SSH Proxy Bridge                         ─  □  ×      │
├──────────────┬───────────────────────────────────────────┤
│ 服务器       │ 我的服务器                     ＋ 添加     │
│ 诊断         │                                           │
│ 设置         │ ● GPU Server                              │
│              │   developer@example.com:22                │
│              │   /workspace/project                      │
│              │   代理正常 · 隧道已连接                   │
│              │                     [打开 VS Code] [···]  │
│              │                                           │
│              │ ○ Test Server                             │
│              │   尚未连接                    [连接] [···]│
├──────────────┴───────────────────────────────────────────┤
│ 状态：1 台已连接                         查看运行日志     │
└──────────────────────────────────────────────────────────┘
```

添加服务器向导控制在三个步骤：

1. 服务器：名称、Host、Port、User、Password、“保存密码”复选框。
2. 本机代理：自动检测结果，或手动填写 Host、Port、协议和程序路径。
3. 项目与确认：默认远程目录、Key 模式、安全摘要和“测试并保存”。

交互要求：

| ID | 优先级 | 需求 |
|---|---:|---|
| FR-077 | P0 | 主界面采用服务器卡片；每张卡片只保留一个主按钮：“连接”或“打开 VS Code”。 |
| FR-078 | P0 | 连接时在卡片内展示当前步骤，例如“正在检查代理”“正在建立隧道”，不弹出持续刷新的控制台窗口。 |
| FR-079 | P0 | 高级 SSH、端口范围和超时配置默认折叠，普通用户无需理解即可完成向导。 |
| FR-079A | P0 | 表单错误显示在对应输入项下方，不只用通用弹窗提示“操作失败”。 |
| FR-079B | P1 | 支持浅色、深色和跟随系统；默认跟随系统。 |
| FR-079C | P1 | 使用 Segoe UI Variable 和 Segoe Fluent Icons；统一 8px 圆角和 8px 间距网格。 |
| FR-079D | P1 | 支持 125%/150%/200% DPI、键盘操作和高对比度，不使用颜色作为唯一状态提示。 |

为降低开发成本，第一版不做：服务器拓扑图、流量曲线、皮肤市场、复杂拖拽排序、玻璃特效或超过 200ms 的装饰动画。

### 7.10 状态、托盘与通知

连接状态机：

```text
Disconnected
  -> CheckingLocalProxy
  -> Authenticating
  -> CheckingRemote
  -> EstablishingTunnel
  -> ValidatingRemoteProxy
  -> LaunchingVSCode
  -> Connected
  -> Degraded / Reconnecting / Error
```

| ID | 优先级 | 需求 |
|---|---:|---|
| FR-080 | P0 | 主窗口显示服务器卡片、代理状态、SSH 状态、隧道状态和最后一次验证时间。 |
| FR-081 | P0 | 每个长操作都可取消；UI 不得因 SSH 或网络操作阻塞。 |
| FR-082 | P0 | 托盘菜单支持连接、打开默认目录、停止、状态和退出。 |
| FR-083 | P1 | 仅在需要用户行动、连接恢复或最终失败时发送 Windows 通知，避免重复刷屏。 |
| FR-084 | P1 | 应用退出时询问“停止隧道”或“保持后台运行”；不得留下无法管理的孤儿进程。 |

### 7.11 诊断、日志与恢复

| ID | 优先级 | 需求 |
|---|---:|---|
| FR-090 | P0 | 一键诊断按本机代理、OpenSSH、认证、转发权限、远程端口、远程代理、VS Code 七层输出结果。 |
| FR-091 | P0 | 日志结构化记录时间、Profile ID、步骤、耗时、错误码和安全脱敏后的命令。 |
| FR-092 | P0 | 日志禁止记录密码、私钥口令、私钥内容、Credential Blob 和含认证信息的代理 URL。 |
| FR-093 | P0 | 导出诊断包前二次执行脱敏，并允许用户预览文件列表。 |
| FR-094 | P0 | 提供“修复公钥”“重建 SSH Config”“重建远程环境”“重启隧道”等分层修复动作。 |
| FR-095 | P1 | 提供对照式错误说明，包括问题层级、证据、可自动修复项和需管理员处理项。 |
| FR-096 | P0 | 成功连接后启动 Profile 独立的后台隧道监控；SSH 进程退出或远端代理探针失败时，在本机代理与 SSH 可用后按退避策略安全重建。 |
| FR-097 | P0 | 提供不打开 VS Code 的“修复隧道”入口；健康隧道不得重建，“停止连接”必须同时停止监控和受管隧道。 |
| FR-098 | P0 | 自动修复必须使用 Profile 级互斥锁和进程归属校验，不得停止其他 SSH、VS Code 或用户 PowerShell 进程。 |

### 7.12 删除与卸载

| ID | 优先级 | 需求 |
|---|---:|---|
| FR-100 | P0 | 删除 Profile 时分别询问：停止隧道、删除 Windows 凭据、删除 SSH Config 块、删除远程代理块、删除专用私钥。 |
| FR-101 | P0 | 默认保留私钥和服务器 `authorized_keys` 项，除非用户明确选择删除。 |
| FR-102 | P0 | 远程清理失败不得阻止本地配置删除，但必须输出可复制的手动清理命令。 |
| FR-103 | P1 | 卸载应用时能够保留或删除全部用户数据，选择结果必须明确。 |

## 8. 安全与隐私需求

### 8.1 凭据处理

- 服务器密码仅通过密码框输入，禁止复制到普通文本控件。
- 凭据仅在执行 SSH 认证前即时读取，使用后尽快释放引用。
- 不通过命令行参数、环境变量、临时文件或异常消息传递密码。密码网关模式仅允许 AskPass Helper 按 OpenSSH 协议把密码写入该 SSH 子进程的专属匿名管道；不得把该输出写入日志或普通终端。
- 保存、替换和删除凭据都需要明确的用户操作。
- 诊断模式不得为了“方便排查”关闭脱敏。
- 任何遥测默认关闭；第一版建议完全不上传日志或配置。

### 8.2 SSH 安全

- 禁止自动信任首次主机密钥。
- 禁止为了连接成功而设置 `StrictHostKeyChecking=no`。
- 私钥只写入当前用户 `.ssh` 目录，并检查 ACL。
- 反向端口仅绑定服务器 loopback。
- 服务器密码认证成功后优先切换到公钥认证；服务器明确只公布 `password` 时使用受控密码网关模式。
- 不在服务器保存 Windows 代理软件凭据。

### 8.3 共享服务器限制

绑定到服务器 `127.0.0.1` 可以阻止外部网络直接访问，但通常不能阻止同一服务器上的其他本地用户连接该 TCP 端口。因此：

- 首次配置必须询问服务器是否为不可信的多人共享环境。
- 对共享服务器显示持续安全提示。
- MVP 不宣称提供用户级网络隔离。
- 后续版本可研究带认证的本地中继或 Unix socket 网关，但不能用随机端口冒充安全隔离。

## 9. 数据模型

### 9.1 Profile JSON 示例

```json
{
  "schemaVersion": 1,
  "id": "8a62c3c4-59c9-45a8-88c5-f9dc4bb15ad8",
  "name": "GPU Server",
  "proxy": {
    "adapter": "generic",
    "host": "127.0.0.1",
    "port": 7897,
    "protocol": "auto",
    "executablePath": "D:\\Apps\\Proxy\\proxy.exe",
    "autoStart": true
  },
  "ssh": {
    "alias": "codex-gpu-server",
    "host": "203.0.113.10",
    "port": 22,
    "user": "developer",
    "authentication": "managed-key",
    "identityFile": "%USERPROFILE%\\.ssh\\id_ed25519_codex_gpu_server",
    "hostKeySha256": "SHA256:example",
    "credentialRef": "SshProxyBridge:ssh-password:8a62c3c4-59c9-45a8-88c5-f9dc4bb15ad8",
    "hasStoredCredential": true
  },
  "remote": {
    "proxyHost": "127.0.0.1",
    "proxyPort": 17897,
    "proxyPortRangeStart": 17897,
    "proxyPortRangeEnd": 17997,
    "shell": "auto",
    "environmentScope": "vscode-and-shell",
    "managedConfigVersion": 1
  },
  "vscode": {
    "channel": "stable",
    "launch": true,
    "defaultWorkspace": "/workspace/project",
    "extensionId": "openai.chatgpt"
  },
  "behavior": {
    "autoReconnect": true,
    "connectOnAppStart": false,
    "openVsCodeAfterConnect": true
  }
}
```

### 9.2 运行状态

运行状态不混入 Profile 配置，单独存储：

```json
{
  "profileId": "...",
  "state": "Connected",
  "tunnelPid": 12345,
  "startedAt": "2026-07-12T12:00:00Z",
  "lastHealthyAt": "2026-07-12T12:03:00Z",
  "failureCount": 0
}
```

应用启动时不能盲信 PID，必须同时校验进程路径、启动参数摘要和隧道健康状态，防止误操作复用后的 PID。

## 10. 技术架构建议

### 10.1 技术栈

- 运行时：.NET 10 LTS，目标 `net10.0-windows`。
- UI：WPF，采用 MVVM；使用原生 ResourceDictionary 建立轻量设计系统，第一版不引入 WebView、Electron 或大型 UI 套件。
- 宿主：`Microsoft.Extensions.Hosting`，统一依赖注入、配置、日志和后台服务生命周期。
- SSH 密码认证和远程命令：SSH.NET。
- VS Code 日常连接和最终兼容链路：Windows OpenSSH。
- 凭据：Win32 Credential Manager 的 `CredWriteW`、`CredReadW`、`CredDeleteW`。
- 日志：`Microsoft.Extensions.Logging` + 本地滚动 JSON/Text Provider；实现统一脱敏器。
- 安装：优先 MSIX；若 OpenSSH/UAC 集成受限，再选择 WiX Toolset 制作 MSI。

选择 SSH.NET 的目的不是取代 VS Code 的 OpenSSH，而是安全完成密码认证、公钥安装、认证能力探测和初始化远程命令，避免把密码放进子进程参数。日常 VS Code 仍通过标准 SSH Config 连接；普通服务器使用 Key，密码网关由 OpenSSH AskPass 回调读取 Windows 凭据。

### 10.2 解决方案结构

```text
src/
  SshProxyBridge.App/                 WPF、ViewModel、托盘和通知
  SshProxyBridge.Domain/              Profile、状态、错误和领域规则
  SshProxyBridge.Application/         用例编排和接口
  SshProxyBridge.Infrastructure.Win/  凭据、注册表、进程、服务、UAC
  SshProxyBridge.Infrastructure.Ssh/  SSH.NET、OpenSSH、known_hosts、隧道
  SshProxyBridge.Infrastructure.Proxy/代理探测和客户端适配器
  SshProxyBridge.Infrastructure.Code/ VS Code 检测与启动
  SshProxyBridge.Cli/                 无界面诊断和自动化入口
tests/
  SshProxyBridge.UnitTests/
  SshProxyBridge.IntegrationTests/
  SshProxyBridge.E2E.Tests/
```

UI 不得直接调用 PowerShell、SSH.NET 或 `Process.Start`。所有操作通过 Application 层接口执行，以便 CLI、单元测试和 GUI 共享同一套逻辑。

### 10.3 核心接口建议

```csharp
public interface ICredentialStore
{
    Task SaveAsync(CredentialReference reference, string userName, string secret,
        CancellationToken cancellationToken);
    Task<StoredCredential?> ReadAsync(CredentialReference reference,
        CancellationToken cancellationToken);
    Task DeleteAsync(CredentialReference reference,
        CancellationToken cancellationToken);
}

public interface IProxyService
{
    Task<ProxyProbeResult> ProbeAsync(ProxyProfile profile,
        CancellationToken cancellationToken);
    Task<ProxyStartResult> EnsureStartedAsync(ProxyProfile profile,
        CancellationToken cancellationToken);
}

public interface ISshBootstrapper
{
    Task<HostKeyObservation> ObserveHostKeyAsync(SshProfile profile,
        CancellationToken cancellationToken);
    Task<SshBootstrapResult> InstallPublicKeyAsync(
        SshProfile profile,
        StoredCredential password,
        CancellationToken cancellationToken);
}

public interface ITunnelManager
{
    Task<TunnelHandle> StartAsync(ConnectionProfile profile,
        CancellationToken cancellationToken);
    Task<TunnelHealth> CheckAsync(TunnelHandle handle,
        CancellationToken cancellationToken);
    Task StopAsync(Guid profileId, CancellationToken cancellationToken);
}

public interface IRemoteConfigurator
{
    Task<RemoteInspection> InspectAsync(ConnectionProfile profile,
        CancellationToken cancellationToken);
    Task<RemoteChangeSet> PlanAsync(ConnectionProfile profile,
        CancellationToken cancellationToken);
    Task ApplyAsync(RemoteChangeSet changeSet, CancellationToken cancellationToken);
    Task RollbackAsync(Guid profileId, CancellationToken cancellationToken);
}

public interface IVsCodeLauncher
{
    Task<VsCodeInspection> InspectAsync(CancellationToken cancellationToken);
    Task LaunchRemoteAsync(string sshAlias, string remotePath,
        CancellationToken cancellationToken);
}
```

### 10.4 连接编排建议

```csharp
public async Task<ConnectResult> ConnectAsync(
    Guid profileId,
    ConnectOptions options,
    IProgress<ConnectionProgress> progress,
    CancellationToken cancellationToken)
{
    var profile = await profiles.GetRequiredAsync(profileId, cancellationToken);
    await proxy.EnsureStartedAsync(profile.Proxy, cancellationToken);
    await sshIdentity.VerifyPinnedHostKeyAsync(profile.Ssh, cancellationToken);
    await sshKeys.EnsureKeyLoginAsync(profile, cancellationToken);
    await remote.EnsureConfiguredAsync(profile, cancellationToken);
    var tunnel = await tunnels.EnsureStartedAsync(profile, cancellationToken);
    await health.VerifyRemoteProxyAsync(profile, tunnel, cancellationToken);

    if (options.OpenVsCode)
        await vscode.LaunchRemoteAsync(profile.Ssh.Alias, options.RemotePath, cancellationToken);

    return ConnectResult.Success(tunnel);
}
```

该方法必须幂等。每一步返回结构化结果，而不是依赖解析面向用户的控制台字符串。

### 10.5 Windows 凭据实现建议

```csharp
internal static class CredentialTarget
{
    public static string SshPassword(Guid profileId) =>
        $"SshProxyBridge:ssh-password:{profileId:D}";
}
```

实现时直接 P/Invoke `CredWriteW`、`CredReadW`、`CredDeleteW`，或者使用经过安全审计且仍在维护的轻量封装。无论采用哪种方式，都必须为读取结果调用 `CredFree`，并覆盖以下测试：Unicode 密码、空密码拒绝、替换、删除、凭据不存在、非当前用户不可读取和日志脱敏。

### 10.6 SSH 实现建议

SSH 分成两个适配器：

1. `SshNetBootstrapClient`
   - 使用内存中的密码完成首次认证。
   - 订阅主机密钥事件并执行指纹校验。
   - 创建 `~/.ssh`、修正权限、幂等追加公钥。
   - 执行远程检查和配置命令。

2. `OpenSshTunnelProcess`
   - 使用 Windows `ssh.exe` 和专用 Key 维持普通服务器隧道；密码网关则由受控 AskPass Helper 提供即时认证。
   - 参数使用 `-N`、`-T`、`-R` 和显式 keepalive 配置。
   - 捕获标准错误但必须脱敏。
   - 通过 Job Object 或受管进程表确保退出时可回收。

不要实现以下方案：

- `ssh.exe user@host password`：OpenSSH 不支持，且会泄露。
- `plink -pw password`：密码会暴露在命令行或进程检查中。
- 自动发送键盘输入到密码窗口：脆弱且可能输错目标窗口。
- 临时生成包含密码的 `.bat`、`.ps1` 或环境变量。

### 10.7 远程脚本建议

远程配置不要继续把大段 Bash 拼接到 C# 字符串。应把版本化脚本作为嵌入资源：

```text
Resources/Remote/
  inspect-linux.sh
  install-env-bash.sh
  uninstall-env-bash.sh
  probe-proxy.sh
  install-authorized-key.sh
```

执行前把非秘密参数作为严格校验后的参数传入，或上传到用户目录的临时文件并校验 SHA256；禁止把密码放入脚本。脚本使用 `set -eu`，输出稳定的 JSON 或 `KEY=VALUE` 协议，并给每个错误分配代码。

### 10.8 从现有 PowerShell 原型迁移

现有 `ssh-proxy-bridge.ps1` 可作为行为基线：

- 保留其 doctor、setup、start、status、stop、uninstall 语义。
- 首先抽取配置校验、代理探针、托管块、SSH 参数和状态机测试用例。
- 早期 GUI 可以调用 PowerShell 兼容层完成非秘密操作，但密码流程不得由 PowerShell 参数传递。
- 迁移完成前保留 CLI 作为故障回退；CLI 和 GUI 共用同一 Application 层。
- 产品配置使用新的 Schema；为现有 `config.local.json` 提供一次性导入器，导入时不假设其中存在密码。

## 11. 错误模型

错误必须包含稳定代码、层级、简短说明和建议动作，例如：

| 错误码 | 含义 | 建议动作 |
|---|---|---|
| `PROXY_PORT_CLOSED` | 本机代理端口未监听 | 启动代理或修改端口 |
| `PROXY_PROBE_FAILED` | 端口存在但代理不能访问测试地址 | 检查节点和代理模式 |
| `SSH_HOST_KEY_NEW` | 首次发现主机密钥 | 显示指纹并等待确认 |
| `SSH_HOST_KEY_CHANGED` | 主机指纹变化 | 中止并联系管理员核实 |
| `SSH_PASSWORD_REJECTED` | 密码认证失败 | 重新输入；成功后再更新保存项 |
| `SSH_KEY_REJECTED` | 公钥认证失败 | 使用密码执行“修复公钥” |
| `SSH_FORWARDING_DENIED` | 服务器禁止 TCP 转发 | 联系服务器管理员 |
| `REMOTE_PORT_BUSY` | 远程端口被占用 | 自动选择其他端口 |
| `REMOTE_PROXY_UNREACHABLE` | 隧道存在但服务器代理探针失败 | 检查隧道和本地代理协议 |
| `VSCODE_NOT_FOUND` | 找不到 VS Code CLI | 选择安装位置或安装 VS Code |
| `REMOTE_PATH_NOT_FOUND` | 默认远程目录不存在 | 创建或选择其他目录 |

## 12. 非功能需求

### 12.1 兼容性

MVP 测试范围：

- Windows 10 22H2、Windows 11 当前受支持版本。
- VS Code Stable 当前版与前一个主要月度版本。
- Windows 内置 OpenSSH Client。
- Ubuntu 22.04/24.04、Debian 12，x86_64。
- Bash，普通用户与 root 两类账户。
- HTTP/Mixed 本地代理；SOCKS5 作为兼容测试项。

### 12.2 性能与资源

- 空闲后台内存目标低于 150 MB。
- 健康检查默认 30 秒一次，不持续高频访问外部网络。
- 所有网络操作必须有超时和取消令牌。
- Profile 数量达到 50 时主界面仍应流畅；默认不同时自动连接全部服务器。

### 12.3 可维护性

- Domain 和 Application 层单元测试覆盖率目标不低于 80%。
- 所有远程配置和本地配置格式具有 `schemaVersion`。
- 远程脚本必须有 ShellCheck 检查和容器化集成测试。
- 第三方依赖使用锁定文件、依赖更新机器人和许可证清单。

## 13. 测试计划

### 13.1 单元测试

- Profile 校验和迁移。
- SSH Alias 生成与冲突处理。
- Credential Target 生成。
- 日志脱敏，包括密码恰好出现在异常文本中的情况。
- 托管配置块的安装、升级、重复执行和删除。
- 状态机、取消和重连退避。

### 13.2 集成测试

- 使用临时 Linux 容器和独立 sshd 测试密码认证、公钥安装与反向转发。
- 测试 `AllowTcpForwarding no`、错误密码、端口冲突和主机密钥变化。
- 使用本地假 HTTP/SOCKS 代理测试协议探测。
- 使用临时 Windows 测试账号验证 Credential Manager 的用户边界。

### 13.3 端到端测试

- 全新 Windows 用户首次安装到打开远程目录。
- Windows 重启后恢复。
- 代理未启动、代理端口变化、网络断开和恢复。
- 多服务器并发连接。
- VS Code/Codex 扩展更新后重新加载窗口。
- 删除 Profile 和完整卸载。

## 14. MVP 验收标准

MVP 只有满足以下条件才可发布：

1. 能在干净的 Windows 10/11 用户环境安装和启动。
2. 能添加 Ubuntu/Debian 服务器并验证主机指纹。
3. 能使用密码首次认证，并按用户选择保存到 Windows Credential Manager。
4. 能为普通服务器生成并安装专用 SSH Key、验证 Key-only 登录，并能自动识别和连接只允许密码认证的 SSH 网关。
5. 能自动启动或验证本机代理并建立反向隧道。
6. 能从服务器验证代理连通性。
7. 能直接打开用户指定的 VS Code 远程绝对路径。
8. 能停止、恢复和重连隧道。
9. 能删除保存的密码和所有托管配置。
10. 自动测试和人工审计均确认日志、配置和诊断包不包含秘密。

## 15. 开发里程碑

### M0：冻结原型行为

- 为现有 PowerShell 脚本补充行为测试清单。
- 固定 Profile Schema、错误码和远程托管块格式。
- 记录当前成功环境作为回归基线。

### M1：可操作的 GUI 技术切片

- 建立 .NET 解决方案分层和 WPF 单窗口外壳。
- 完成服务器列表、三步添加向导、连接状态和“打开 VS Code”主按钮。
- 实现 Profile Store、Credential Store、Proxy Probe、SSH.NET Bootstrap 和 OpenSSH Tunnel 的纵向最小链路。
- 同时提供 `doctor/connect/status/disconnect/remove` CLI 作为开发测试和诊断入口，但普通用户不需要使用 CLI。
- 界面达到可日常使用的整洁程度，复杂设置和次要视觉效果留到后续。

### M2：完整 GUI 与异常流程

- 完善保存、替换、删除密码和主机指纹变化界面。
- 加入托盘、Windows 通知、远程目录历史和完整诊断页。
- 补齐取消、失败重试、修复公钥、删除服务器和回滚流程。

### M3：可靠性与发布

- 加入自动重连、休眠恢复、多 Profile 并发和诊断包。
- 完成签名、安装包、升级和卸载。
- 完成 Windows/Linux/VS Code 测试矩阵。

### M4：适配扩展

- Clash Verge、v2rayN、Hiddify 适配器。
- Zsh、更多 Linux 发行版和 SOCKS-only 中继。
- 配置导入导出与团队模板，但继续排除密码。

## 16. 建议的首批开发任务

采用“可见界面骨架 + 高风险核心纵向切片”的顺序：

1. 建立 `SshProxyBridge.Domain`、Profile Schema 和 WPF 主窗口骨架。
2. 用模拟数据完成服务器卡片和三步添加向导，先确认最短操作路径。
3. 实现 Win32 Credential Manager 封装及安全测试，并接入向导的“保存密码”。
4. 实现 SSH.NET 主机指纹验证、密码登录和公钥安装。
5. 实现 OpenSSH 隧道生命周期和健康检查，并实时绑定到服务器卡片状态。
6. 把现有远程 Bash 逻辑拆成版本化资源脚本。
7. 打通“添加服务器 → 测试 → 保存 → 连接 → 打开 VS Code”的 GUI 端到端链路。
8. 再补充 CLI 诊断、托盘和异常恢复流程。

首个技术验收切片应当是：

> 在全新测试账号中保存密码，使用该密码安装公钥，清除内存引用，通过 Key-only 建立反向隧道，从服务器完成代理探针，然后删除 Windows 凭据；整个过程中日志零秘密。

## 17. 官方依据与技术参考

- [VS Code Remote Development using SSH](https://code.visualstudio.com/docs/remote/ssh)：远程文件夹、远程扩展、SSH Config 和端口转发行为。
- [VS Code Remote Development Tips and Tricks](https://code.visualstudio.com/docs/remote/troubleshooting)：`AllowTcpForwarding`、远程 `HTTP_PROXY/HTTPS_PROXY` 和 Windows `ssh-agent` 建议。
- [OpenAI Codex IDE extension](https://learn.chatgpt.com/docs/codex/ide)：Codex IDE 扩展的使用边界。
- [Microsoft Credential Locker guidance](https://learn.microsoft.com/en-us/windows/apps/develop/security/credential-locker)：只有用户选择保存且成功登录后才保存密码，不得使用明文应用配置。
- [Win32 CREDENTIAL structure](https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentialw)：Generic Credential 和本机持久性语义。
- [CredWriteW](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credwritew)、[CredReadW](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credreadw)、[CredDeleteW](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-creddeletew)：Windows 凭据读写接口。
- [.NET Generic Host in WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/app-development/how-to-use-host-builder)：WPF 中统一依赖注入、配置、日志和后台生命周期。
- [.NET Support Policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)：.NET 10 是支持至 2028-11-14 的 LTS 版本。
- [SSH.NET package](https://www.nuget.org/packages/SSH.NET/) 与 [ForwardedPortRemote API](https://sshnet.github.io/SSH.NET/api/Renci.SshNet.ForwardedPortRemote.html)：.NET 密码认证、远程命令和反向端口转发能力。
