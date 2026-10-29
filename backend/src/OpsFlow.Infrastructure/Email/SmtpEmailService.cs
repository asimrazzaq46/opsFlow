using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Abstractions;

namespace OpsFlow.Infrastructure.Email;

public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    /// <summary>Defaults match Mailpit (SMTP on 1025, web UI on 8025).</summary>
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1025;
    public bool EnableSsl { get; set; }
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = "noreply@opsflow.local";
    public string FromName { get; set; } = "OpsFlow";
}

internal sealed class SmtpEmailService(IOptions<SmtpOptions> options) : IEmailService
{
    public async Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken)
    {
        var o = options.Value;
        using var client = new SmtpClient(o.Host, o.Port) { EnableSsl = o.EnableSsl };
        if (!string.IsNullOrEmpty(o.UserName))
        {
            client.Credentials = new NetworkCredential(o.UserName, o.Password);
        }

        using var message = new MailMessage(new MailAddress(o.FromAddress, o.FromName), new MailAddress(recipient))
        {
            Subject = subject,
            Body = body,
        };
        await client.SendMailAsync(message, cancellationToken);
    }
}
