using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    /// <summary>Upper bound of ONE send (connect + transfer). Keep it well below Outbox:LeaseSeconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}

internal sealed partial class SmtpEmailService : IEmailService
{
    private readonly IOptions<SmtpOptions> options;
    private readonly ILogger<SmtpEmailService> logger;

    public SmtpEmailService(IOptions<SmtpOptions> options)
        : this(options, NullLogger<SmtpEmailService>.Instance) { }

    public SmtpEmailService(IOptions<SmtpOptions> options, ILogger<SmtpEmailService> logger)
    {
        this.options = options;
        this.logger = logger;
    }

    // The send keeps its own deadline, so a stuck server cannot hold a thread-pool worker for longer than TimeoutSeconds.
    public void SendDetached(string recipient, string subject, string body) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await SendAsync(recipient, subject, body, CancellationToken.None);
            }
            catch (Exception ex)
            {
                LogDetachedFailed(logger, ex);
            }
        });

    [LoggerMessage(Level = LogLevel.Error, Message = "Detached email delivery failed")]
    private static partial void LogDetachedFailed(ILogger logger, Exception exception);

    public async Task SendAsync(
        string recipient,
        string subject,
        string body,
        CancellationToken cancellationToken
    )
    {
        var o = options.Value;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 1, 300));
        using var client = new SmtpClient(o.Host, o.Port)
        {
            EnableSsl = o.EnableSsl,
            Timeout = (int)timeout.TotalMilliseconds,
        };
        if (!string.IsNullOrEmpty(o.UserName))
        {
            client.Credentials = new NetworkCredential(o.UserName, o.Password);
        }

        using var message = new MailMessage(
            new MailAddress(o.FromAddress, o.FromName),
            new MailAddress(recipient)
        )
        {
            Subject = subject,
            Body = body,
        };
        // SmtpClient.Timeout is unreliable for async sends, so use our own deadline token (host shutdown still surfaces as OperationCanceled).
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await client.SendMailAsync(message, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"SMTP send exceeded {timeout.TotalSeconds:0} s.");
        }
    }
}
