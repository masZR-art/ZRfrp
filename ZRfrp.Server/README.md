# ZRfrp Server

ZRfrp Server 是 Linux 端多用户控制平面，包含管理员/客户双视图、账号与流量额度、服务节点集群、frps 一键安装、模块化配置、连接统计、端口分配和服务端限速。

## 一键安装

```bash
curl -fsSL https://raw.githubusercontent.com/masZR-art/ZRfrp/main/ZRfrp.Server/deploy/install.sh | sudo bash
```

安装器支持 `x86_64` 和 `arm64`，会安装自包含的 ZRfrp Server、frps 0.69.1、两个 systemd 服务，并生成独立的管理员密码、客户端 API Key 和 frp Token；不需要预先安装 .NET Runtime。

建议仅通过 HTTPS 反向代理公开 `7600` 面板端口。frps Dashboard 默认只监听 `127.0.0.1:7500`。

支付宝支付的密钥、HTTPS 回调、沙箱和上线步骤见 [支付宝接入指南](ALIPAY.md)。

## 更新

```bash
curl -fsSL https://raw.githubusercontent.com/masZR-art/ZRfrp/main/ZRfrp.Server/deploy/install.sh | sudo bash
```

已有 `/etc/zrfrp/frps.toml` 和生产配置不会被覆盖。更新完成后 systemd 会继续管理服务。

## Desktop 托管节点

1. 在 Desktop 的节点设置中打开“ZRfrp Server 托管”。
2. 填写面板 URL 和安装时输出的客户端 API Key。
3. 新建 TCP/UDP 隧道并填写可选带宽，例如 `2MB`。
4. 保存时服务端检查节点和端口，自动分配远程端口。分配后该端口在 Desktop 中锁定。

关键策略在 frps 的 `NewProxy` 插件阶段再次执行，不能通过手工修改客户端 TOML 绕过。

## 账号体系

- 管理员账号进入主控面板，管理客户、额度、节点和 frps 配置。
- 客户账号进入用量面板，可查看已用、总额和剩余流量。
- 登录页支持客户自助注册；管理员可在“客户账号”中关闭注册并设置新账号的默认流量额度。
- Desktop 使用客户账号登录，换取短期访问会话和 frp Token。
- frps 插件在登录和创建隧道时校验账号及额度，额度用尽后拒绝新连接。

## 主控与子节点

管理员也可以直接在主控面板的“服务节点”中点击“添加节点”，填写节点名称、公网地址和可被新节点访问的主控面板地址。面板会登记待接入节点并生成专属部署命令；将该命令粘贴到新服务器执行后，节点会自动注册上线。

主控首次安装时会输出 `节点 Peer Key`。子节点使用同一密钥注册：

```bash
curl -fsSL https://raw.githubusercontent.com/masZR-art/ZRfrp/main/ZRfrp.Server/deploy/install.sh |
  sudo env ZRFRP_MODE=node ZRFRP_MASTER_URL=https://master.example.com \
  ZRFRP_MASTER_KEY=主控PeerKey ZRFRP_PEER_KEY=主控PeerKey bash
```

子节点每 15 秒向主控发送状态心跳。管理员可在节点页面查看在线情况并远程重启 frps。
