using System.Security.Cryptography;
using System.Text;

namespace WhatsAppTasks.Core;

public static class SettingsProtection
{
    public static string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser));
    }

    public static string Unprotect(string protectedValue) => string.IsNullOrEmpty(protectedValue)
        ? string.Empty
        : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser));
}
