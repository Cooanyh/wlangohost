using System.Text;

namespace PadP2P.App.Services;

internal static class NetworkProfileValidator
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool TryValidate(string ssid, string passphrase, out string errorResourceKey)
    {
        if (string.IsNullOrWhiteSpace(ssid))
        {
            errorResourceKey = "NetworkProfile.ErrorSsidRequired";
            return false;
        }

        if (ContainsControlCharacters(ssid))
        {
            errorResourceKey = "NetworkProfile.ErrorSsidControlCharacters";
            return false;
        }

        try
        {
            if (StrictUtf8.GetByteCount(ssid) > 32)
            {
                errorResourceKey = "NetworkProfile.ErrorSsidLength";
                return false;
            }
        }
        catch (EncoderFallbackException)
        {
            errorResourceKey = "NetworkProfile.ErrorSsidEncoding";
            return false;
        }

        if (passphrase.Length is < 8 or > 63)
        {
            errorResourceKey = "NetworkProfile.ErrorPasswordLength";
            return false;
        }

        if (passphrase.Any(character => character is < ' ' or > '~'))
        {
            errorResourceKey = "NetworkProfile.ErrorPasswordCharacters";
            return false;
        }

        errorResourceKey = string.Empty;
        return true;
    }

    private static bool ContainsControlCharacters(string value) => value.Any(char.IsControl);
}
