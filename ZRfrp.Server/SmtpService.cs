using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Encodings.Web;

namespace ZRfrp.Server;

public sealed class SmtpService
{
    private readonly StateStore _store;

    public SmtpService(StateStore store) => _store = store;

    public async Task SendVerificationCodeAsync(string email, string recipientName)
    {
        var settings = _store.State.Smtp;
        EnsureConfigured(settings);
        var normalizedEmail = NormalizeEmail(email);
        var now = DateTimeOffset.UtcNow;
        var existing = _store.State.EmailVerificationChallenges.FirstOrDefault(item =>
            item.Email.Equals(normalizedEmail, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && existing.LastSentAt > now.AddMinutes(-1))
        {
            throw new InvalidOperationException("验证码发送过于频繁，请一分钟后重试。");
        }

        var code = System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var challenge = existing ?? new EmailVerificationChallenge { Email = normalizedEmail };
        challenge.CodeHash = Security.HashToken(code);
        var verificationMinutes = Math.Clamp(settings.VerificationMinutes, 1, 120);
        challenge.ExpiresAt = now.AddMinutes(verificationMinutes);
        challenge.LastSentAt = now;
        challenge.FailedAttempts = 0;
        if (existing is null) _store.State.EmailVerificationChallenges.Add(challenge);
        await _store.SaveAsync();

        try
        {
            await SendAsync(normalizedEmail,
                Render(settings.SubjectTemplate, normalizedEmail, recipientName, code, verificationMinutes),
                Render(settings.HtmlTemplate, normalizedEmail, recipientName, code, verificationMinutes));
        }
        catch
        {
            _store.State.EmailVerificationChallenges.Remove(challenge);
            await _store.SaveAsync();
            throw;
        }
    }

    public async Task SendTestAsync(string recipientEmail) =>
        await SendAsync(NormalizeEmail(recipientEmail), "ZRfrp SMTP 测试邮件",
            "<h2>ZRfrp SMTP 配置正常</h2><p>如果您收到此邮件，说明邮件服务器配置可用。</p>");

    public bool VerifyCode(string email, string code)
    {
        var normalizedEmail = NormalizeEmail(email);
        var challenge = _store.State.EmailVerificationChallenges.FirstOrDefault(item =>
            item.Email.Equals(normalizedEmail, StringComparison.OrdinalIgnoreCase));
        if (challenge is null || challenge.ExpiresAt <= DateTimeOffset.UtcNow || challenge.FailedAttempts >= 5)
            return false;
        if (!Security.VerifyToken(code.Trim(), challenge.CodeHash))
        {
            challenge.FailedAttempts++;
            return false;
        }
        _store.State.EmailVerificationChallenges.Remove(challenge);
        return true;
    }

    public static string NormalizeEmail(string email)
    {
        try { return new MailAddress(email.Trim()).Address.ToLowerInvariant(); }
        catch { throw new InvalidOperationException("邮箱地址格式无效。"); }
    }

    private async Task SendAsync(string recipient, string subject, string html)
    {
        var settings = _store.State.Smtp;
        EnsureConfigured(settings);
        using var message = new MailMessage
        {
            From = new MailAddress(settings.FromEmail, settings.FromName),
            Subject = subject,
            SubjectEncoding = Encoding.UTF8,
            Body = html,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = true
        };
        message.To.Add(recipient);
        using var client = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = settings.EnableSsl,
            Credentials = string.IsNullOrWhiteSpace(settings.Username)
                ? CredentialCache.DefaultNetworkCredentials
                : new NetworkCredential(settings.Username, settings.Password),
            Timeout = 15000
        };
        await client.SendMailAsync(message);
    }

    private static void EnsureConfigured(SmtpSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Host) || settings.Port is < 1 or > 65535
            || string.IsNullOrWhiteSpace(settings.FromEmail))
            throw new InvalidOperationException("SMTP 配置不完整。");
    }

    private static string Render(
        string template, string email, string recipientName, string code, int verificationMinutes) => template
        .Replace("{{site_name}}", "ZRfrp", StringComparison.Ordinal)
        .Replace("{{recipient_email}}", HtmlEncoder.Default.Encode(email), StringComparison.Ordinal)
        .Replace("{{recipient_name}}", HtmlEncoder.Default.Encode(
            string.IsNullOrWhiteSpace(recipientName) ? email : recipientName.Trim()), StringComparison.Ordinal)
        .Replace("{{user_name}}", HtmlEncoder.Default.Encode(
            string.IsNullOrWhiteSpace(recipientName) ? email : recipientName.Trim()), StringComparison.Ordinal)
        .Replace("{{code}}", HtmlEncoder.Default.Encode(code), StringComparison.Ordinal)
        .Replace("{{verification_code}}", HtmlEncoder.Default.Encode(code), StringComparison.Ordinal)
        .Replace("{{expires_minutes}}", verificationMinutes.ToString(), StringComparison.Ordinal)
        .Replace("{{expires_in_minutes}}", verificationMinutes.ToString(), StringComparison.Ordinal);
}
