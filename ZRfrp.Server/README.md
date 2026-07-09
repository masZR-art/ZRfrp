# ZRfrp Server

ZRfrp Server 是 Linux 端控制平面，包含 Web 管理面板、frps 配置管理、连接与流量概览、端口自动分配、服务端策略插件和隧道限速。

## 一键安装

```bash
curl -fsSL https://raw.githubusercontent.com/3317603015whw-art/ZRfrp/main/ZRfrp.Server/deploy/install.sh | sudo bash
```

安装器支持 `x86_64` 和 `arm64`，会安装 ZRfrp Server、frps 0.69.1、两个 systemd 服务，并生成独立的管理员密码、客户端 API Key 和 frp Token。

建议仅通过 HTTPS 反向代理公开 `7600` 面板端口。frps Dashboard 默认只监听 `127.0.0.1:7500`。

## 更新

```bash
curl -fsSL https://raw.githubusercontent.com/3317603015whw-art/ZRfrp/main/ZRfrp.Server/deploy/install.sh | sudo bash
```

已有 `/etc/zrfrp/frps.toml` 和生产配置不会被覆盖。更新完成后 systemd 会继续管理服务。

## Desktop 托管节点

1. 在 Desktop 的节点设置中打开“ZRfrp Server 托管”。
2. 填写面板 URL 和安装时输出的客户端 API Key。
3. 新建 TCP/UDP 隧道并填写可选带宽，例如 `2MB`。
4. 保存时服务端检查节点和端口，自动分配远程端口。分配后该端口在 Desktop 中锁定。

关键策略在 frps 的 `NewProxy` 插件阶段再次执行，不能通过手工修改客户端 TOML 绕过。
