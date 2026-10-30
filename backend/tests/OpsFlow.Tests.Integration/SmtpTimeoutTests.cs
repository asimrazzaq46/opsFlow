using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Abstractions;
using OpsFlow.Infrastructure.Email;

namespace OpsFlow.Tests.Integration;

public class SmtpTimeoutTests
{
    private static IEmailService Create(int port, int timeoutSeconds)
    {
        var type = typeof(SmtpOptions).Assembly.GetType("OpsFlow.Infrastructure.Email.SmtpEmailService")!; // internal by design
        return (IEmailService)Activator.CreateInstance(type, Options.Create(new SmtpOptions { Host = "127.0.0.1", Port = port, TimeoutSeconds = timeoutSeconds }))!;
    }

    [Fact]
    public async Task A_smtp_server_that_never_answers_is_cut_off_after_the_configured_timeout()
    {
        using var silent = new TcpListener(IPAddress.Loopback, 0); // accepts the connection, never speaks SMTP
        silent.Start();
        var service = Create(((IPEndPoint)silent.LocalEndpoint).Port, 1);

        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<Exception>(() => service.SendAsync("a@example.com", "s", "b", CancellationToken.None));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "The send was not bounded by Smtp:TimeoutSeconds.");
    }

    [Fact]
    public async Task Host_shutdown_is_still_reported_as_cancellation_not_as_a_timeout()
    {
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var service = Create(((IPEndPoint)silent.LocalEndpoint).Port, 30);
        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => service.SendAsync("a@example.com", "s", "b", shutdown.Token));

        Assert.IsNotType<TimeoutException>(ex);
    }
}
