namespace DanmuApi.Runtime;

public static class RuntimeTokenResolver
{
    public static string Resolve(string envPath) =>
        Resolve(envPath, Environment.GetEnvironmentVariable("TOKEN"));

    public static string Resolve(string envPath, string? processToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envPath);
        if (!string.IsNullOrWhiteSpace(processToken))
        {
            return processToken.Trim();
        }

        var configuredToken = DotEnvFile.ReadValue(envPath, "TOKEN");
        return string.IsNullOrWhiteSpace(configuredToken)
            ? RuntimeDefaults.FallbackToken
            : configuredToken.Trim();
    }
}
