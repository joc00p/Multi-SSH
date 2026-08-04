using System.IO;
using MultiSSH.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MultiSSH.Services;

/// <summary>
/// Builds an SSH.NET <see cref="ConnectionInfo"/> from a <see cref="SessionConfig"/>.
/// Shared by the SSH shell and the SFTP/SCP file-transfer backends so they all
/// authenticate identically (password / public key / keyboard-interactive).
/// </summary>
public static class RemoteAuth
{
    public static ConnectionInfo BuildConnectionInfo(SessionConfig cfg)
    {
        var methods = new List<AuthenticationMethod>();

        switch (cfg.Auth)
        {
            case AuthMethod.PublicKey:
                if (string.IsNullOrWhiteSpace(cfg.PrivateKeyPath) || !File.Exists(cfg.PrivateKeyPath))
                    throw new FileNotFoundException("Private key file not found", cfg.PrivateKeyPath);
                PrivateKeyFile keyFile;
                try
                {
                    keyFile = string.IsNullOrEmpty(cfg.KeyPassphrase)
                        ? new PrivateKeyFile(cfg.PrivateKeyPath)
                        : new PrivateKeyFile(cfg.PrivateKeyPath, cfg.KeyPassphrase);
                }
                catch (Exception ex) when (IsPassphraseProblem(ex))
                {
                    throw new KeyPassphraseRequiredException(
                        string.IsNullOrEmpty(cfg.KeyPassphrase)
                            ? "The private key is encrypted and needs a passphrase."
                            : "Incorrect passphrase for the private key.", ex);
                }
                methods.Add(new PrivateKeyAuthenticationMethod(cfg.Username, keyFile));
                break;

            case AuthMethod.KeyboardInteractive:
            {
                var ki = new KeyboardInteractiveAuthenticationMethod(cfg.Username);
                ki.AuthenticationPrompt += (_, e) =>
                {
                    foreach (var prompt in e.Prompts) prompt.Response = cfg.Password ?? "";
                };
                methods.Add(ki);
                break;
            }

            case AuthMethod.Agent:
                methods.Add(new PasswordAuthenticationMethod(cfg.Username, cfg.Password ?? ""));
                break;

            default: // Password
                methods.Add(new PasswordAuthenticationMethod(cfg.Username, cfg.Password ?? ""));
                var kip = new KeyboardInteractiveAuthenticationMethod(cfg.Username);
                kip.AuthenticationPrompt += (_, e) =>
                {
                    foreach (var prompt in e.Prompts) prompt.Response = cfg.Password ?? "";
                };
                methods.Add(kip);
                break;
        }

        return new ConnectionInfo(cfg.Host, cfg.Port, cfg.Username, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(cfg.ConnectTimeoutSeconds),
        };
    }

    private static bool IsPassphraseProblem(Exception ex)
    {
        if (ex is SshPassPhraseNullOrEmptyException) return true;
        if (ex is System.Security.Cryptography.CryptographicException) return true;
        var m = ex.Message?.ToLowerInvariant() ?? "";
        return m.Contains("passphrase") || m.Contains("pass phrase")
            || m.Contains("invalid pad") || m.Contains("decrypt")
            || m.Contains("bad data");
    }
}
