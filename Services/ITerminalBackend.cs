namespace MultiSSH.Services;

/// <summary>
/// Whatever is on the other end of a terminal: an SSH channel
/// (<see cref="SshConnection"/>) or a local console
/// (<see cref="LocalShellConnection"/>). Both raise bytes as they arrive and
/// take keystrokes back.
/// </summary>
public interface ITerminalBackend : IDisposable
{
    event Action<byte[]>? DataReceived;
    event Action<string>? StatusChanged;
    event Action<string>? Closed;

    /// <summary>Raised once when the shell on the far side ends.</summary>
    event Action? ShellExited;

    bool IsConnected { get; }

    Task ConnectAsync(int cols, int rows);

    void Send(byte[] data);
    void Send(string text);

    void Resize(int cols, int rows);
}
