using System;
using System.Linq;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace MinioCommon
{
    /// <summary>
    /// Sends email alerts via SMTP when MinIO operations fail.
    /// Uses MailKit (the recommended SMTP library for .NET 8+).
    /// Requires root-level Email settings in config.json.
    /// </summary>
    public static class EmailNotifier
    {
        /// <summary>
        /// Sends a failure alert to the specified recipients.
        /// Returns true if the email was sent successfully, false on any failure (logged).
        /// </summary>
        public static async Task<bool> SendAlertAsync(
            EmailSettings settings,
            string[] recipients,
            string subject,
            string body)
        {
            if (settings == null)
                return false;

            if (recipients == null || recipients.Length == 0)
                return false;

            try
            {
                var message = new MimeMessage();
                message.From.Add(new MailboxAddress(
                    settings.SenderName ?? "MinioSync",
                    settings.SenderAddress));
                foreach (var addr in recipients.Where(r => !string.IsNullOrWhiteSpace(r)))
                {
                    message.To.Add(new MailboxAddress(addr.Trim(), addr.Trim()));
                }

                if (message.To.Count == 0)
                    return false;

                message.Subject = subject;
                message.Body = new TextPart("plain") { Text = body };

                using (var client = new SmtpClient())
                {
                    // Accept any server certificate (intranet/MinIO scenario)
                    client.ServerCertificateValidationCallback = (s, c, h, e) => true;

                    await client.ConnectAsync(
                        settings.SmtpServer,
                        settings.SmtpPort,
                        settings.UseSsl
                            ? SecureSocketOptions.SslOnConnect
                            : SecureSocketOptions.StartTlsWhenAvailable);

                    if (!string.IsNullOrEmpty(settings.SenderPassword))
                    {
                        await client.AuthenticateAsync(
                            settings.SenderAddress,
                            settings.SenderPassword);
                    }

                    await client.SendAsync(message);
                    await client.DisconnectAsync(true);
                }

                Logger.Info($"邮件通知已发送: {subject} -> {string.Join(", ", recipients)}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"发送邮件失败: {subject}", ex);
                return false;
            }
        }

        /// <summary>
        /// Builds a formatted failure notification body with standard fields.
        /// </summary>
        public static string BuildFailureBody(string configId, string action, string filePath, string errorDetail)
        {
            return $"MinioSync 同步失败通知\n" +
                   $"========================\n" +
                   $"时间:     {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                   $"配置 ID:  {configId ?? "(未知)"}\n" +
                   $"操作:     {action ?? "(未知)"}\n" +
                   $"文件:     {filePath ?? "(未知)"}\n" +
                   $"错误:     {errorDetail ?? "(无详情)"}\n" +
                   $"========================\n" +
                   $"此邮件由 MinioSync 自动发送。";
        }
    }
}
