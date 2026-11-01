using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Email;

/// <summary>A plain-text email. No HTML: nothing in it can be rendered as markup or load remote content.</summary>
internal sealed record EmailMessage(string To, string Subject, string Body);

internal interface IEmailSender
{
    /// <exception cref="EmailDeliveryException">The email couldn't be sent.</exception>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>A failed delivery. <see cref="Permanent"/> failures (e.g. an unknown mailbox) aren't retried.</summary>
internal sealed class EmailDeliveryException(string result, bool permanent, Exception? inner = null)
    : Exception($"Sending the email failed ({result}).", inner)
{
    public string Result { get; } = result;

    public bool Permanent { get; } = permanent;
}

internal sealed class SmtpEmailSender(IOptions<SmtpOptions> smtpOptions, IOptions<BrandingOptions> branding) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var options = smtpOptions.Value;
        if (options is not { Enabled: true, Host: { } host, FromAddress: { } from })
        {
            throw new EmailDeliveryException("smtp_not_configured", permanent: true);
        }

        using var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(options.FromName ?? branding.Value.ProductName, from));
        mime.To.Add(new MailboxAddress(string.Empty, message.To));
        mime.Subject = message.Subject;
        mime.Body = new TextPart("plain") { Text = message.Body };

        using var client = new SmtpClient { Timeout = (int)options.Timeout.TotalMilliseconds };
        try
        {
            // The server certificate is validated (MailKit's default); there's no option to turn that off.
            await client.ConnectAsync(host, options.Port, options.Security switch
            {
                SmtpSecurity.Tls => SecureSocketOptions.SslOnConnect,
                SmtpSecurity.None => SecureSocketOptions.None,
                _ => SecureSocketOptions.StartTls,
            }, cancellationToken);
            if (!string.IsNullOrEmpty(options.UserName))
            {
                await client.AuthenticateAsync(options.UserName, options.Password ?? string.Empty, cancellationToken);
            }

            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);
        }
        catch (SmtpCommandException ex)
        {
            // RFC 5321 A4.2.1: 5yz replies are permanent failures; 4yz replies are transient.
            var code = (int)ex.StatusCode;
            throw new EmailDeliveryException($"smtp_{code}", permanent: code >= 500, ex);
        }
        catch (AuthenticationException ex)
        {
            throw new EmailDeliveryException("authentication_failed", permanent: false, ex);
        }
        catch (Exception ex) when (ex is SmtpProtocolException or SslHandshakeException or IOException or System.Net.Sockets.SocketException or TimeoutException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new EmailDeliveryException("network_error", permanent: false, ex);
        }
    }
}
