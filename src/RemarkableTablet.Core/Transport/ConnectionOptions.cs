namespace RemarkableTablet.Core.Transport;

public sealed class ConnectionOptions
{
    public string Host { get; init; } = "10.11.99.1";
    public int Port { get; init; } = 22;
    public string Username { get; init; } = "root";

    // Exactly one of Password or PrivateKeyPath should be set.
    public string? Password { get; init; }
    public string? PrivateKeyPath { get; init; }

    public static ConnectionOptions WithPassword(string password, string host = "10.11.99.1")
    {
        return new ConnectionOptions { Host = host, Password = password };
    }

    public static ConnectionOptions WithKey(string keyPath, string host = "10.11.99.1")
    {
        return new ConnectionOptions { Host = host, PrivateKeyPath = keyPath };
    }
}