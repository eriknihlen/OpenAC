using System.Security.Cryptography;
using System.Text;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal static class MossTankProfileRecovery
{
    internal static string Preserve(
        IPluginHost host,
        string family,
        string key,
        string? content,
        Exception error)
    {
        string summary = $"{family} profile '{key}' could not be loaded: "
            + error.Message;
        if (!host.Storage.IsAvailable || string.IsNullOrEmpty(content))
            return summary;

        try
        {
            byte[] identity = SHA256.HashData(
                Encoding.UTF8.GetBytes(key + "\n" + content));
            string recoveryKey = $"recovery/{family.ToLowerInvariant()}/"
                + $"{Convert.ToHexString(identity)[..16]}.txt";
            string payload = $"Original key: {key}\n"
                + $"Load error: {error.Message}\n\n"
                + content;
            host.Storage.WriteText(recoveryKey, payload);
            return summary + $" Raw data was preserved as {recoveryKey}.";
        }
        catch (Exception backupError)
        {
            host.Log.Warn(
                $"MossTank could not preserve corrupt {family} profile: "
                + backupError.Message);
            return summary;
        }
    }
}
