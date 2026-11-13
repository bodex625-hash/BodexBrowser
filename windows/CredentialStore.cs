using System.Security.Cryptography;
using System.Text;

namespace HBProxy.Windows;

/// <summary>
/// Stores SOCKS5 credentials encrypted with Windows DPAPI for the current user.
/// The encrypted file is not portable to another Windows account or machine.
/// </summary>
public sealed class CredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("H.B Proxy for Windows / credential store / v1");
    private readonly string _filePath;

    public CredentialStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bodex", "HBProxy");
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "socks5.credentials.dpapi");
    }

    public void Save(string? username, string? password)
    {
        if (string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password))
        {
            Delete();
            return;
        }

        if (string.IsNullOrEmpty(username) || password is null)
            throw new ArgumentException("Both username and password are required.");

        var plain = Encoding.UTF8.GetBytes(username + "\n" + password);
        try
        {
            var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_filePath, protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public (string Username, string Password)? Load()
    {
        if (!File.Exists(_filePath))
            return null;

        var protectedBytes = File.ReadAllBytes(_filePath);
        byte[]? plain = null;
        try
        {
            plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            var value = Encoding.UTF8.GetString(plain);
            var separator = value.IndexOf('\n');
            if (separator <= 0)
                throw new CryptographicException("Stored SOCKS5 credentials have an invalid format.");

            return (value[..separator], value[(separator + 1)..]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (plain is not null)
                CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Delete()
    {
        if (File.Exists(_filePath))
            File.Delete(_filePath);
    }
}
