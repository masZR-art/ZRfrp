# ZRfrp

<p align="center">
  <img src="FrpDesktop/Assets/AppIcon256.png" width="144" height="144" alt="ZRfrp Logo">
</p>

<p align="center">
  面向 frp 的可视化多用户、多节点控制平台
</p>

<p align="center">
  <a href="https://github.com/masZR-art/ZRfrp/releases/latest">下载最新版</a>
  ·
  <a href="ZRfrp.Server/README.md">服务端文档</a>
  ·
  <a href="ZRfrp.Server/ALIPAY.md">支付宝接入</a>
  ·
  <a href="https://github.com/masZR-art/ZRfrp/tree/personal-manager">个人管理器</a>
</p>

ZRfrp 将 Windows 上的 frpc 管理与 Linux 上的 frps 控制平面整合为一套完整平台。它不仅提供节点、隧道、日志和托盘控制，还包含账号认证、流量额度、多服务节点调度、端口分配、服务端策略校验和可视化运维能力。

当前主版本面向需要统一管理用户和多个 frps 节点的场景。早期无需账号体系的个人使用版保留在 [`personal-manager`](https://github.com/masZR-art/ZRfrp/tree/personal-manager) 分支。

> ZRfrp 是独立的第三方项目，并非 frp 官方组件。底层穿透能力由 [fatedier/frp](https://github.com/fatedier/frp) 提供。

## 项目组成

| 组件 | 运行平台 | 用途 |
| --- | --- | --- |
| **ZRfrp Desktop** | Windows 10/11 | 登录平台、同步节点、创建隧道、控制 frpc、查看日志与连接状态 |
| **ZRfrp Server** | Linux x64 / arm64 | 提供 Web 面板、账号与额度管理、多节点控制、端口分配及 frps 运维 |
| **frpc / frps** | Windows / Linux | 实际执行反向代理连接与流量转发 |

## 核心能力

### Windows Desktop

- **节点管理**：管理本地节点，也可登录控制平台自动同步全部可用节点。
- **节点配置导入**：导入 ZRfrp 节点配置文件，自动跳过已经存在的相同节点。
- **隧道管理**：创建、编辑、启停和删除 `tcp`、`udp`、`http`、`https` 隧道。
- **服务端分配**：托管 TCP/UDP 隧道由目标服务节点分配端口，避免多用户端口冲突。
- **配置生成与校验**：生成标准 `frpc.toml`，启动前自动执行 `frpc verify -c`。
- **进程控制**：启动、停止和监控 frpc；应用仅允许一个主进程实例运行。
- **实时日志**：区分普通、成功、警告和错误信息；隧道地址可点击复制。
- **节点测速**：启动时自动测试延迟，支持单节点重测和全部节点一键测速。
- **后台托盘**：主窗口关闭后可继续运行；自定义托盘菜单可控制不同节点的通道或彻底退出。
- **FRP 环境管理**：自动识别、选择或下载安装兼容的 `frpc.exe`，并应用到全部节点。
- **网络代理**：支持跟随系统代理、不使用代理和手动 HTTP/SOCKS 代理，用于版本检查与资源下载。
- **自动更新**：从 GitHub Releases 检查、下载并安装新版本。
- **完整桌面体验**：暗色无边框界面、自定义弹窗、节点国旗标识及多尺寸应用图标。

Desktop 首次启动时会显示平台登录窗口。登录后会获取短期授权并同步节点；也可以跳过登录，先以本地节点管理方式使用。退出账号后，先前由该账号授权的托管节点将失效。

### Linux Server

- **双角色 Web 面板**：管理员进入主控面板，普通客户进入个人仪表盘。
- **账号系统**：支持客户注册、登录、启停、额度调整、流量重置和账号删除。
- **流量管理**：统计账号流量，展示已用、总额和剩余额度；超额后阻止新连接。
- **客户端状态**：从 frps Dashboard 获取在线客户端、活动隧道和流量统计。
- **服务节点集群**：统一登记、命名、监控、重启和删除主节点与子节点。
- **节点自动接入**：主控面板生成专属安装命令和离线部署包，子节点安装后自动注册并上报心跳。
- **节点配置导出**：客户可复制或下载当前账号可用的节点配置，供 Desktop 一键导入。
- **端口与节点分配**：按目标节点创建远程端口租约，防止冲突并确保隧道落到正确的 frps。
- **服务端策略校验**：通过 frps HTTP 插件验证账号、额度、端口租约和隧道归属。
- **带宽限制**：由服务端策略下发并强制执行隧道带宽上限。
- **frps 运维**：在面板内检测、安装、修复、启动、停止和重启 frps。
- **模块化配置**：可视化编辑常用 frps 参数；高级模式保留完整 TOML 编辑能力。
- **版本维护**：检测 GitHub Release，并通过 systemd 安全更新服务端程序。
- **审计与活动记录**：记录账号、节点、配置、分配和服务操作，固定区域滚动展示。

## 快速开始

### 1. 部署主控服务端

在一台 Linux x64 或 arm64 服务器上运行：

```bash
curl -fsSL https://raw.githubusercontent.com/masZR-art/ZRfrp/main/ZRfrp.Server/deploy/install.sh | sudo bash
```

安装器会部署自包含的 ZRfrp Server、frps 和对应的 systemd 服务，不要求服务器预装 .NET。安装完成后请立即保存终端输出的：

- 面板地址与初始管理员密码
- 客户端 API Key
- frp Token
- 节点 Peer Key

默认端口：

| 端口 | 用途 |
| --- | --- |
| `7600` | ZRfrp Web 面板与 API |
| `7000` | frps 客户端连接端口 |
| `7500` | frps Dashboard，默认仅监听 `127.0.0.1` |

生产环境建议通过 HTTPS 反向代理公开面板，并限制管理端口的访问来源。

### 2. 初始化平台

1. 打开安装器输出的面板地址。
2. 使用 `admin` 和安装器输出的初始密码登录。
3. 在“系统维护”中确认 frps 状态正常。
4. 在“模块化配置”中检查绑定端口、认证和 Dashboard 设置。
5. 在“客户账号”中配置注册策略、默认额度或直接创建客户账号。

### 3. 安装 Desktop

1. 从 [GitHub Releases](https://github.com/masZR-art/ZRfrp/releases/latest) 下载 Windows x64 发布包。
2. 解压后运行 `ZRfrp.exe`。
3. 输入主控面板地址和客户账号，登录后自动同步可用节点。
4. 在设置中检测或安装 `frpc.exe`。
5. 选择节点并创建隧道，然后启动连接。

平台地址应填写 ZRfrp Server 面板的根地址，例如：

```text
https://frp.example.com
```

或未配置 HTTPS 时：

```text
http://203.0.113.10:7600
```

### 4. 添加服务节点

推荐在主控面板的“服务节点”页面完成：

1. 点击“添加节点”。
2. 选择国家/地区标识并填写节点名称。
3. 填写新节点真实公网地址，以及新服务器可以访问的主控面板地址。
4. 创建后复制面板生成的专属安装命令，在新 Linux 服务器上以 root 权限执行。
5. 等待节点完成安装并上报心跳，面板状态将变为在线。

当新服务器访问 GitHub 缓慢或失败时，可使用面板生成的离线部署方式：先从主控下载包含 ZRfrp Server 与 frps 的文件，再通过 SCP 传至新服务器并执行生成的离线安装脚本。该方式不要求新节点直接访问 GitHub。

## 配置与数据目录

### Desktop

```text
%AppData%\ZRfrp
```

生成给 frpc 使用的配置位于：

```text
%AppData%\ZRfrp\generated
```

旧版 `%AppData%\FrpDesktop` 数据会在首次运行时自动迁移。

### Server

| 路径 | 内容 |
| --- | --- |
| `/opt/zrfrp/server` | ZRfrp Server 程序 |
| `/opt/zrfrp/frps` | frps 可执行文件 |
| `/etc/zrfrp` | ZRfrp 与 frps 配置及状态数据 |
| `/etc/systemd/system/zrfrp-server.service` | Web 控制平面服务 |
| `/etc/systemd/system/zrfrp-frps.service` | frps 服务 |

## 安全说明

- 不要公开提交管理员密码、客户端 API Key、frp Token、Peer Key 或导出的节点授权文件。
- 面板建议放在 HTTPS 反向代理之后，避免凭据通过明文 HTTP 传输。
- frps Dashboard 默认只应在本机监听，不建议直接暴露到公网。
- 客户端授权具有有效期；退出登录后应停止使用该账号同步的节点。
- Token 与 Peer Key 用途不同：frp Token 用于 frpc/frps 认证，Peer Key 用于主控与子节点通信。

## 从源码构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

构建 Windows Desktop：

```powershell
dotnet restore FrpDesktop\FrpDesktop.csproj
dotnet build FrpDesktop\FrpDesktop.csproj -c Release --no-restore
dotnet publish FrpDesktop\FrpDesktop.csproj -c Release -r win-x64 --self-contained false
```

构建 Linux Server：

```powershell
dotnet publish ZRfrp.Server\ZRfrp.Server.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
dotnet publish ZRfrp.Server\ZRfrp.Server.csproj -c Release -r linux-arm64 --self-contained true -p:PublishSingleFile=true
```

每次推送或提交 Pull Request 时，[GitHub Actions](.github/workflows/build.yml) 会自动验证 Desktop，并构建 Linux x64 与 arm64 服务端。

## 项目结构

```text
FrpDesktop/
  Assets/                    应用图标与国家/地区标识
  MainWindow.xaml            Desktop 主界面
  FrpProcessRunner.cs        frpc 生命周期与日志处理
  FrpConfigSerializer.cs     frpc.toml 生成与读取
  FrpEnvironmentService.cs   frpc 检测、下载与安装
  ZRfrpControlClient.cs      平台登录、节点同步与端口分配
  DesktopUpdateService.cs    Desktop 版本更新
  TrayMenuWindow.xaml        自定义托盘控制菜单

ZRfrp.Server/
  Program.cs                 Web API、认证与 frps 插件入口
  AccountService.cs          账号、会话和流量额度
  AllocationService.cs       节点选择与端口租约
  FrpsManager.cs             frps 安装、状态与服务控制
  NodeHeartbeatService.cs    子节点心跳与主控通信
  TrafficCollector.cs        Dashboard 流量采集
  BootstrapPackageService.cs 节点在线/离线部署包
  wwwroot/                   Web 管理与客户面板
  deploy/                    Linux 安装、修复、更新及 systemd 配置
```

## 参与开发

欢迎通过 [Issues](https://github.com/masZR-art/ZRfrp/issues) 报告问题或提出建议。提交问题时建议附上 ZRfrp 版本、frp 版本、操作系统、相关日志以及可复现步骤，并在分享前移除所有凭据。

## 许可证

本项目基于 [MIT License](LICENSE) 开源。
