# ZRfrp 支付宝接入指南

ZRfrp Server 2.5.0 支持支付宝电脑网站支付与手机网站支付。支付请求使用 RSA2 签名，套餐只会在支付宝异步通知通过签名、应用、商户、订单号和金额校验后入账；浏览器同步返回不会直接发放权益。

## 1. 准备条件

- 已完成支付宝开放平台企业或个体工商户认证，并具备网站支付产品权限。
- 面板拥有外网可访问的 HTTPS 地址，例如 `https://panel.example.com`。
- 反向代理必须把 `/api/payments/alipay/notify` 原样转发到 ZRfrp Server，不能要求登录、验证码或额外鉴权。
- 服务器时钟应启用 NTP 同步。

支付宝开放平台入口及官方文档：

- [网页与移动应用](https://open.alipay.com/module/webApp)
- [电脑网站支付快速接入](https://developer.alibaba.com/docs/doc.htm?articleId=105900&docType=1&treeId=270)
- [手机网站支付快速接入](https://developer.alibaba.com/docs/doc.htm?articleId=106411&docType=1&treeId=331)
- [异步通知验签说明](https://developer.alibaba.com/docs/doc.htm?articleId=106448&docType=1&treeId=193)

## 2. 创建应用和 RSA2 密钥

1. 登录支付宝开放平台，创建网页/移动应用并获取 `AppID`。
2. 使用支付宝开放平台密钥工具生成 2048 位 RSA2 应用密钥对。
3. 将“应用公钥”配置到支付宝开放平台对应应用中。
4. 保存本地生成的“应用私钥”。该私钥填入 ZRfrp，不能上传到代码仓库或发送给他人。
5. 应用公钥配置生效后，在支付宝开放平台复制“支付宝公钥”。注意它不是应用公钥。
6. 完成电脑网站支付、手机网站支付所需的产品签约和应用上线审核。

密钥对应关系：

| ZRfrp 字段 | 应填写内容 |
| --- | --- |
| 应用私钥 | 本地生成、与开放平台应用公钥配对的 RSA 私钥 |
| 支付宝公钥 | 开放平台在应用密钥页面提供的支付宝公钥 |
| Seller ID / PID | 可选的支付宝商户 PID，用于额外校验异步通知 |

## 3. 配置 HTTPS 反向代理

以下 Nginx 示例假定 ZRfrp Server 监听本机 `7600`：

```nginx
server {
    listen 443 ssl http2;
    server_name panel.example.com;

    ssl_certificate     /etc/letsencrypt/live/panel.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/panel.example.com/privkey.pem;

    location / {
        proxy_pass http://127.0.0.1:7600;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-Proto https;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    }
}
```

配置后确认外网能访问：

```bash
curl -I https://panel.example.com/
```

## 4. 在 ZRfrp 主控面板填写

1. 使用管理员账号登录主控面板。
2. 打开“支付设置”。
3. 测试阶段选择“沙箱环境”，正式收款选择“正式环境”。
4. 填写 AppID、应用私钥、支付宝公钥和面板公网 HTTPS 地址。
5. 如已知商户 PID，可填写 Seller ID / PID；填写后异步通知必须携带相同商户号。
6. 打开“启用支付宝支付”，保存。
7. 页面状态应显示“可收款”，并生成以下地址：
   - `https://panel.example.com/api/payments/alipay/notify`
   - `https://panel.example.com/api/payments/alipay/return`

应用私钥保存后不会返回浏览器。以后修改其他字段时，私钥输入框留空即可保留原值。

## 5. 套餐与批准策略

- 价格大于 0 且支付宝配置完整：客户下单后进入“待支付”。
- 支付成功且套餐开启“自动批准”：异步通知验签通过后立即发放流量、有效期、节点和通道权益。
- 支付成功但套餐关闭“自动批准”：订单进入“已支付 · 待审核”，管理员批准后生效。
- 免费套餐开启“自动批准”：提交后立即生效。
- 免费套餐关闭“自动批准”：提交后等待管理员审核。
- 空节点范围代表全部当前及未来节点；通道上限 `0` 代表不限。

## 6. 沙箱验收流程

1. 在支付宝开放平台沙箱中取得沙箱 AppID、密钥和沙箱买家账号。
2. ZRfrp 选择“沙箱环境”并保存沙箱凭据。
3. 创建一个低价测试套餐，开启自动批准。
4. 使用客户账号提交订单并使用沙箱买家付款。
5. 返回订阅中心后，订单可能短暂显示“待支付”；支付宝异步通知到达后应变为“已生效”。
6. 在主控面板确认账户流量、有效期、节点范围和通道上限均已更新。
7. 重复发送同一通知不会重复增加流量或延长有效期。

## 7. 常见问题

- **一直待支付**：检查公网 HTTPS、反向代理、DNS、防火墙和支付宝异步通知记录。
- **验签失败**：通常是误填“应用公钥”，应填写“支付宝公钥”；也要检查正式与沙箱密钥是否混用。
- **商户信息不匹配**：检查 Seller ID / PID；不需要额外校验时可留空。
- **金额不一致**：不要在订单创建后直接修改历史订单；ZRfrp 会拒绝金额与套餐快照不同的通知。
- **支付后待审核**：这是套餐关闭“自动批准”时的预期状态，管理员可在“订阅管理”中批准。
- **回调返回 401/302**：反向代理或外层访问控制拦截了通知路径。支付宝通知接口必须可以匿名 POST，并由 RSA2 验签承担认证。

生产启用前，建议先在沙箱完成至少一次电脑端、一次手机端和一次重复通知测试。
