# ZRfrp

ZRfrp 是一套面向 frp 的可视化管理工具，包含 Windows Desktop 客户端和 Linux Server 控制平面。Desktop 管理本机 `frpc.exe`，Server 提供 Web 面板、frps 配置、连接与流量统计、端口自动分配和服务端限速策略。

它面向已经拥有 frps 服务端的用户，目标是把终端里的 frpc 配置、启动、停止、日志查看和常用隧道管理变成更顺手的桌面体验。

## 功能特性

- 多节点管理：保存多个 frp 服务端节点配置。
- 隧道管理：创建、编辑、启用、停用、删除隧道。
- 隧道类型：支持 `tcp`、`udp`、`http`、`https`。
- 配置生成：自动生成标准 `frpc.toml`。
- 启动校验：启动前调用 `frpc verify -c` 校验配置。
- 连接控制：启动、停止并监控 `frpc.exe`。
- 运行日志：彩色日志、错误高亮、成功地址提示。
- 地址复制：隧道启动成功后，点击日志中的连接地址即可复制。
- 节点测速：启动时自动测速，支持单节点重测和一键测速全部节点。
- 托盘常驻：关闭窗口后可进入后台，托盘菜单支持通道开关和彻底退出。
- 软件设置：自动检测、选择或下载安装 `frpc.exe`。
- 网络代理：支持系统代理、不使用代理、手动代理，仅影响软件自身下载/访问发布信息。
- 暗色界面：无边框窗口、浮动设置/编辑窗口、自定义托盘菜单。
- 服务端托管：自动检测节点并分配、锁定 TCP/UDP 远程端口。
- 带宽策略：为隧道设置由 frps 强制执行的带宽上限。

## Linux 服务端

```bash
curl -fsSL https://raw.githubusercontent.com/3317603015whw-art/ZRfrp/main/ZRfrp.Server/deploy/install.sh | sudo bash
```

支持 Linux x64 与 arm64。安装后会输出面板密码、客户端 API Key 和 frp Token。详细说明见 [ZRfrp.Server/README.md](ZRfrp.Server/README.md)。

## 运行环境

- Windows 10/11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- 可用的 `frpc.exe`
- 已部署好的 frps 服务端

## 快速开始

1. 下载发布包并解压。
2. 启动 `ZRfrp.exe`。
3. 在左下角打开设置。
4. 检测、选择或自动下载安装 `frpc.exe`。
5. 新增或编辑节点，填写服务端地址、端口、Token。
6. 新建隧道并启动连接。

配置会保存在当前 Windows 用户目录：

```text
%AppData%\ZRfrp
```

旧版 `%AppData%\FrpDesktop` 配置会在首次运行时自动迁移。

生成给 frpc 使用的配置文件位于：

```text
%AppData%\ZRfrp\generated
```

## 从源码构建

克隆仓库后执行：

```powershell
dotnet build FrpDesktop\FrpDesktop.csproj -c Release
```

生成 Windows x64 发布包：

```powershell
dotnet publish FrpDesktop\FrpDesktop.csproj -c Release -r win-x64 --self-contained false
```

发布输出目录：

```text
FrpDesktop\bin\Release\net8.0-windows\win-x64\publish
```

## 项目结构

```text
FrpDesktop/
  Assets/                    图标资源
  App.xaml                   全局样式
  MainWindow.xaml            主界面
  MainWindow.xaml.cs         主界面逻辑
  TrayMenuWindow.xaml        自定义托盘菜单
  FrpProcessRunner.cs        frpc 进程控制
  FrpConfigSerializer.cs     frpc.toml 读写
  FrpEnvironmentService.cs   frpc 检测和下载安装
  ZRfrpControlClient.cs      服务端端口分配协议
  NetworkLatencyTester.cs    节点 TCP 测速
  Models.cs                  数据模型
ZRfrp.Server/
  Program.cs                 Web API 与 frps 插件入口
  AllocationService.cs       端口租约、节点检测与限速策略
  FrpsManager.cs             frps 状态、配置校验与服务控制
  wwwroot/                   Web 管理面板
  deploy/                    Linux 安装与 systemd 配置
```

## 说明

ZRfrp 不是 frp 官方项目。frp 项目请参考 [fatedier/frp](https://github.com/fatedier/frp)。

## 许可证

本项目基于 MIT License 开源。
