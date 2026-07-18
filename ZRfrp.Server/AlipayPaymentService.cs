using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZRfrp.Server;

public sealed class AlipayPaymentService
{
    public const string ProductionGateway = "https://openapi.alipay.com/gateway.do";
    public const string SandboxGateway = "https://openapi-sandbox.dl.alipaydev.com/gateway.do";

    private readonly StateStore _store;
    private readonly SubscriptionService _subscriptions;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AlipayPaymentService(StateStore store, SubscriptionService subscriptions)
    {
        _store = store;
        _subscriptions = subscriptions;
    }

    public static bool IsConfigured(AlipaySettings? settings) =>
        settings is not null
        && settings.Enabled
        && !string.IsNullOrWhiteSpace(settings.AppId)
        && !string.IsNullOrWhiteSpace(settings.MerchantPrivateKey)
        && !string.IsNullOrWhiteSpace(settings.AlipayPublicKey)
        && !string.IsNullOrWhiteSpace(settings.PublicBaseUrl)
        && IsSupportedGateway(settings.Gateway);

    public async Task<string?> SaveSettingsAsync(AlipaySettingsRequest request)
    {
        var current = _store.State.Alipay ??= new();
        var privateKey = string.IsNullOrWhiteSpace(request.MerchantPrivateKey)
            ? current.MerchantPrivateKey
            : NormalizeKey(request.MerchantPrivateKey);
        var publicKey = NormalizeKey(request.AlipayPublicKey);
        var gateway = string.IsNullOrWhiteSpace(request.Gateway)
            ? ProductionGateway
            : request.Gateway.Trim();
        var publicBaseUrl = (request.PublicBaseUrl ?? "").Trim().TrimEnd('/');

        if (!IsSupportedGateway(gateway))
            return "支付宝网关只能选择正式环境或沙箱环境。";
        if (!string.IsNullOrWhiteSpace(publicBaseUrl)
            && !IsValidPublicBaseUrl(publicBaseUrl))
            return "公网回调地址必须是 HTTPS；本机预览仅允许 localhost 或 127.0.0.1 使用 HTTP。";
        if (!string.IsNullOrWhiteSpace(privateKey))
        {
            try { using var rsa = LoadPrivateKey(privateKey); }
            catch (Exception) { return "应用私钥无法解析，请粘贴 PKCS#1 或 PKCS#8 RSA 私钥。"; }
        }
        if (!string.IsNullOrWhiteSpace(publicKey))
        {
            try { using var rsa = LoadPublicKey(publicKey); }
            catch (Exception) { return "支付宝公钥无法解析，请确认粘贴的是支付宝公钥而非应用公钥。"; }
        }
        if (request.Enabled && (string.IsNullOrWhiteSpace(request.AppId)
                                || string.IsNullOrWhiteSpace(privateKey)
                                || string.IsNullOrWhiteSpace(publicKey)
                                || string.IsNullOrWhiteSpace(publicBaseUrl)))
            return "启用支付宝前必须填写 AppID、应用私钥、支付宝公钥和公网回调地址。";

        await _gate.WaitAsync();
        try
        {
            current.Enabled = request.Enabled;
            current.AppId = (request.AppId ?? "").Trim();
            current.SellerId = (request.SellerId ?? "").Trim();
            current.MerchantPrivateKey = privateKey;
            current.AlipayPublicKey = publicKey;
            current.Gateway = gateway;
            current.PublicBaseUrl = publicBaseUrl;
            await _store.AuditAsync("payment", $"支付宝支付配置已{(current.Enabled ? "启用" : "停用")}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public string BuildPaymentHtml(SubscriptionOrder order, bool mobile)
    {
        var settings = _store.State.Alipay ?? new();
        if (!IsConfigured(settings))
            throw new InvalidOperationException("支付宝支付尚未配置完成。");
        if (order.Status != SubscriptionService.PendingPayment
            || order.PaymentStatus == "paid"
            || string.IsNullOrWhiteSpace(order.OutTradeNo))
            throw new InvalidOperationException("该订单当前不能发起支付。");

        var method = mobile ? "alipay.trade.wap.pay" : "alipay.trade.page.pay";
        var productCode = mobile ? "QUICK_WAP_WAY" : "FAST_INSTANT_TRADE_PAY";
        var baseUrl = settings.PublicBaseUrl.TrimEnd('/');
        var bizContent = JsonSerializer.Serialize(new
        {
            out_trade_no = order.OutTradeNo,
            total_amount = (order.PriceCents / 100m).ToString("F2", CultureInfo.InvariantCulture),
            subject = $"ZRfrp - {order.PlanName}",
            product_code = productCode,
            timeout_express = "15m"
        });
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["app_id"] = settings.AppId,
            ["method"] = method,
            ["format"] = "JSON",
            ["charset"] = "utf-8",
            ["sign_type"] = "RSA2",
            ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["version"] = "1.0",
            ["notify_url"] = baseUrl + "/api/payments/alipay/notify",
            ["return_url"] = baseUrl + "/api/payments/alipay/return",
            ["biz_content"] = bizContent
        };
        parameters["sign"] = Sign(parameters, settings.MerchantPrivateKey);

        var fields = string.Join("", parameters.Select(item =>
            $"<input type=\"hidden\" name=\"{WebUtility.HtmlEncode(item.Key)}\" value=\"{WebUtility.HtmlEncode(item.Value)}\">"));
        var gateway = WebUtility.HtmlEncode(settings.Gateway);
        return $$$"""
<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>正在前往支付宝</title>
<style>html,body{height:100%;margin:0;background:#071521;color:#f5f8fb;font-family:Arial,'Microsoft YaHei',sans-serif}body{display:grid;place-items:center}main{text-align:center}i{display:block;width:34px;height:34px;margin:0 auto 18px;border:3px solid #17334a;border-top-color:#1fba91;border-radius:50%;animation:r .8s linear infinite}p{color:#8fb0ca}@keyframes r{to{transform:rotate(360deg)}}</style></head><body><main><i></i><strong>正在安全跳转到支付宝</strong><p>请勿关闭当前页面</p></main>
<form id="alipay-submit" action="{{{gateway}}}" method="post">{{{fields}}}</form><script>document.getElementById('alipay-submit').submit();</script></body></html>
""";
    }

    public async Task<(bool Success, string Message)> HandleNotificationAsync(
        IReadOnlyDictionary<string, string> values)
    {
        var settings = _store.State.Alipay ?? new();
        if (!IsConfigured(settings)) return (false, "支付宝未启用。");
        if (!VerifySignature(values, settings.AlipayPublicKey)) return (false, "支付宝通知验签失败。");
        if (!MatchesMerchant(values, settings)) return (false, "支付宝通知商户信息不匹配。");
        if (!values.TryGetValue("trade_status", out var tradeStatus)
            || tradeStatus is not ("TRADE_SUCCESS" or "TRADE_FINISHED"))
            return (false, "交易尚未成功。");
        if (!values.TryGetValue("out_trade_no", out var outTradeNo)
            || !values.TryGetValue("total_amount", out var amountText)
            || !decimal.TryParse(amountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            return (false, "支付宝通知缺少订单号或金额。");

        var paidCents = decimal.ToInt64(decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));
        var transactionId = values.TryGetValue("trade_no", out var tradeNo) ? tradeNo : "";
        var result = await _subscriptions.MarkPaidAsync(outTradeNo, transactionId, paidCents);
        return result.Order is not null ? (true, "success") : (false, result.Error ?? "支付入账失败。");
    }

    public bool VerifyReturn(IReadOnlyDictionary<string, string> values)
    {
        var settings = _store.State.Alipay ?? new();
        return IsConfigured(settings)
            && VerifySignature(values, settings.AlipayPublicKey)
            && MatchesMerchant(values, settings);
    }

    private static bool MatchesMerchant(
        IReadOnlyDictionary<string, string> values, AlipaySettings settings)
    {
        if (!values.TryGetValue("app_id", out var appId)
            || !appId.Equals(settings.AppId, StringComparison.Ordinal)) return false;
        if (string.IsNullOrWhiteSpace(settings.SellerId)) return true;
        return values.TryGetValue("seller_id", out var sellerId)
               && sellerId.Equals(settings.SellerId, StringComparison.Ordinal);
    }

    private static string Sign(
        IReadOnlyDictionary<string, string> parameters, string privateKey)
    {
        var content = Canonicalize(parameters);
        using var rsa = LoadPrivateKey(privateKey);
        return Convert.ToBase64String(rsa.SignData(
            Encoding.UTF8.GetBytes(content), HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));
    }

    private static bool VerifySignature(
        IReadOnlyDictionary<string, string> parameters, string publicKey)
    {
        if (!parameters.TryGetValue("sign", out var signature)
            || string.IsNullOrWhiteSpace(signature)) return false;
        try
        {
            var content = Canonicalize(parameters.Where(item =>
                    item.Key is not ("sign" or "sign_type"))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
            using var rsa = LoadPublicKey(publicKey);
            return rsa.VerifyData(
                Encoding.UTF8.GetBytes(content),
                Convert.FromBase64String(signature.Replace(' ', '+')),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        catch
        {
            return false;
        }
    }

    private static string Canonicalize(IEnumerable<KeyValuePair<string, string>> parameters) =>
        string.Join("&", parameters
            .Where(item => !string.IsNullOrEmpty(item.Value) && item.Key != "sign")
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => $"{item.Key}={item.Value}"));

    private static RSA LoadPrivateKey(string value)
    {
        var rsa = RSA.Create();
        var normalized = NormalizeKey(value);
        if (normalized.Contains("BEGIN", StringComparison.Ordinal))
        {
            rsa.ImportFromPem(normalized);
            return rsa;
        }
        var bytes = Convert.FromBase64String(RemoveWhitespace(normalized));
        try { rsa.ImportPkcs8PrivateKey(bytes, out _); }
        catch (CryptographicException) { rsa.ImportRSAPrivateKey(bytes, out _); }
        return rsa;
    }

    private static RSA LoadPublicKey(string value)
    {
        var rsa = RSA.Create();
        var normalized = NormalizeKey(value);
        if (normalized.Contains("BEGIN", StringComparison.Ordinal))
        {
            rsa.ImportFromPem(normalized);
            return rsa;
        }
        var bytes = Convert.FromBase64String(RemoveWhitespace(normalized));
        try { rsa.ImportSubjectPublicKeyInfo(bytes, out _); }
        catch (CryptographicException) { rsa.ImportRSAPublicKey(bytes, out _); }
        return rsa;
    }

    private static bool IsSupportedGateway(string? value) =>
        value is ProductionGateway or SandboxGateway;

    private static bool IsValidPublicBaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return true;
        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeKey(string? value) =>
        (value ?? "").Trim().Replace("\\n", "\n", StringComparison.Ordinal);

    private static string RemoveWhitespace(string value) =>
        new(value.Where(character => !char.IsWhiteSpace(character)).ToArray());
}
