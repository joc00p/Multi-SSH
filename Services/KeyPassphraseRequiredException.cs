namespace MultiSSH.Services;

/// <summary>
/// Thrown by <see cref="SshConnection"/> when a private key could not be loaded
/// because its passphrase is missing or incorrect. The UI catches this to prompt
/// the user for the passphrase and retry.
/// </summary>
public class KeyPassphraseRequiredException : Exception
{
    public KeyPassphraseRequiredException(string message, Exception? inner = null)
        : base(message, inner) { }
}
