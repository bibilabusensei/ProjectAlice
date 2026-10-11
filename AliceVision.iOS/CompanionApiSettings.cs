using System.Security.Cryptography;
using System.Text;
using AliceVision.Core.Agent;
using Foundation;
using Security;

namespace AliceVision.iOS;

internal sealed record CompanionApiSettingsSnapshot(string ProviderId, string Endpoint, string Model,
    string ApiKey, int IntervalSeconds, string Persona)
{
    public CompanionApiConfiguration ToApiConfiguration() => new(Endpoint, Model, ApiKey);
}

internal static class CompanionApiSettings
{
    private const string Prefix = "AliceVision.AI.";
    private const string KeychainService = "com.projectalice.vision.api";

    public static CompanionApiSettingsSnapshot Current
    {
        get
        {
            var defaults = NSUserDefaults.StandardUserDefaults;
            string endpoint = defaults.StringForKey(Prefix + "Endpoint") ?? "";
            return new(defaults.StringForKey(Prefix + "Provider") ?? "custom", endpoint,
                defaults.StringForKey(Prefix + "Model") ?? "", ReadKey(endpoint),
                Math.Clamp((int)(defaults.IntForKey(Prefix + "Interval") is var n && n > 0 ? n : 15), 10, 120),
                defaults.StringForKey(Prefix + "Persona") ?? "友善的空间伙伴。在允许的区域内散步、观察环境、偶尔招手打招呼。不要每次重复相同动作。");
        }
    }

    public static bool ValidEndpoint(string text, out Uri? uri)
    {
        uri = null;
        return text.Length is > 0 and <= 2048 && !text.Any(char.IsControl) && Uri.TryCreate(text, UriKind.Absolute, out uri) &&
            uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) &&
            string.IsNullOrEmpty(uri.Fragment) && string.IsNullOrEmpty(uri.Query) && !string.IsNullOrEmpty(uri.Host);
    }

    public static void Save(CompanionApiSettingsSnapshot settings)
    {
        string? configurationError = CompanionApiClient.ValidateConfiguration(settings.ToApiConfiguration(), out _);
        if (configurationError is not null) throw new ArgumentException(configurationError);
        if (!ValidEndpoint(settings.Endpoint, out _) || string.IsNullOrWhiteSpace(settings.Model) ||
            settings.Model.Length > 160 || settings.Model.Any(char.IsControl) ||
            settings.ApiKey.Length > 4096 || settings.ApiKey.Any(char.IsControl) || settings.Persona.Length > 1000)
            throw new ArgumentException("请填写完整 HTTPS 接口、有效模型 ID；Key 不可包含换行，角色描述最多 1000 字。");

        // Save the secret first. A keychain failure must not silently downgrade to plaintext preferences.
        WriteKey(settings.Endpoint, settings.ApiKey);
        var defaults = NSUserDefaults.StandardUserDefaults;
        defaults.SetString(settings.ProviderId, Prefix + "Provider");
        defaults.SetString(settings.Endpoint, Prefix + "Endpoint");
        defaults.SetString(settings.Model, Prefix + "Model");
        defaults.SetInt(Math.Clamp(settings.IntervalSeconds, 10, 120), Prefix + "Interval");
        defaults.SetString(settings.Persona, Prefix + "Persona");
    }

    private static SecRecord Query(string endpoint) => new(SecKind.GenericPassword)
    {
        Service = KeychainService,
        Account = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)))
    };

    private static string ReadKey(string endpoint)
    {
        if (string.IsNullOrEmpty(endpoint)) return "";
        using var query = Query(endpoint);
        using var value = SecKeyChain.QueryAsRecord(query, out var status);
        if (status != SecStatusCode.Success || value?.ValueData is null) return "";
        using var data = value.ValueData;
        return Encoding.UTF8.GetString(data.ToArray());
    }

    private static void WriteKey(string endpoint, string key)
    {
        using var query = Query(endpoint);
        if (string.IsNullOrEmpty(key))
        {
            var removed = SecKeyChain.Remove(query);
            if (removed is not (SecStatusCode.Success or SecStatusCode.ItemNotFound))
                throw new InvalidOperationException("无法清除钥匙串 Key。请检查当前签名的钥匙串权限。");
            return;
        }
        using var data = NSData.FromArray(Encoding.UTF8.GetBytes(key));
        using var update = new SecRecord { ValueData = data, Accessible = SecAccessible.WhenUnlockedThisDeviceOnly };
        var status = SecKeyChain.Update(query, update);
        if (status == SecStatusCode.ItemNotFound)
        {
            query.ValueData = data;
            query.Accessible = SecAccessible.WhenUnlockedThisDeviceOnly;
            status = SecKeyChain.Add(query);
        }
        if (status != SecStatusCode.Success)
            throw new InvalidOperationException("Key 未保存：钥匙串不可用。请检查签名权限；不会将 Key 保存到普通配置文件。");
    }
}
