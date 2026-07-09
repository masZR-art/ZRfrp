# ZRfrp v2.0.3

- 将 Web 登录 Cookie 的 Data Protection 密钥持久化到 `/var/lib/zrfrp/keys`。
- 修复服务重启、安装或更新后现有登录会话立即失效的问题。
- systemd 明确授权服务写入持久化密钥目录。

## v2.0.2

- 修复 `NoNewPrivileges=true` 阻止面板执行受限维护命令的问题。
- 安装器现在会重启现有服务，确保新的 systemd 安全策略立即生效。
- 新增独立系统维护页面，显示 frps 安装状态、版本和连接状态。
- 新增面板内 frps 检测、安装/修复、ZRfrp 版本检查和一键更新按钮。
- 将服务配置重做为网络、认证监控和日志三个可视化模块。
- 禁用 Web 静态资源缓存，避免升级后仍显示旧面板。

## v2.0.1

- 修复重复安装或升级 Linux Server 时 `PUBLIC_HOST: unbound variable` 导致脚本末尾报错。
- 安装器优先保留已有生产配置中的公网地址，也支持通过 `ZRFRP_PUBLIC_HOST` 显式指定。

## v2.0.0

控制平台主版本。原个人使用版保存在 `personal-manager` 分支。

## 新增

- 管理员与客户双角色账号体系。
- Desktop 必须登录客户账号，动态获取短期会话和 frp Token。
- 服务端插件校验账号、会话、隧道归属与流量额度。
- 管理员可创建、启停客户账号并设置流量额度。
- 客户面板展示已用、总额和剩余流量。
- 主控/子节点心跳、节点状态和远程服务控制。
- 面板一键安装部署 frps。
- `frps.toml` 模块化配置，高级模式保留完整文本编辑。
- Desktop 与 Server GitHub 版本检测和一键更新。

## v1.1.0

Linux Server 首个版本，同时更新 Windows Desktop 的服务端托管能力。

## 新增

- 深色 Web 管理面板，展示 frps 状态、客户端、隧道、流量和操作记录。
- 可视化编辑、校验和应用 `frps.toml`。
- 服务端自动分配并持久化端口租约，分配前检测节点与端口可用性。
- frps 策略插件强制远程端口和隧道带宽限制。
- Desktop 支持托管节点、客户端 API Key、带宽限制和远程端口锁定。
- Linux x64/arm64 一键安装、systemd 服务与自动构建。

## v1.0.0

首个开源版本。当前版本为 Windows 桌面端，后续可继续扩展其他平台客户端。

## 功能

- 可视化管理多个 frp 节点。
- 创建、编辑、启用、停用、删除 frp 隧道。
- 支持 `tcp`、`udp`、`http`、`https` 隧道配置。
- 自动生成 `frpc.toml`，并在启动前调用 `frpc verify` 校验配置。
- 启动、停止并监控 `frpc.exe` 进程。
- 彩色运行日志，隧道启动成功后显示可连接地址并支持点击复制。
- 节点 TCP 延迟测速，支持启动时自动测速和手动一键测速。
- 托盘常驻，支持后台运行、恢复主界面、彻底退出和通道开关。
- 暗色无边框界面，支持浮动设置/编辑窗口。
- 自动检测、选择或下载安装本机 `frpc.exe`。
- 软件联网代理设置：系统代理、不使用代理、手动代理。

## 运行环境

- Windows 10/11
- .NET 8 Desktop Runtime
- frp/frpc 0.69.x

## 发布包

Windows x64 发布包可由以下命令生成：

```powershell
dotnet publish FrpDesktop\FrpDesktop.csproj -c Release -r win-x64 --self-contained false
```
