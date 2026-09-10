namespace DanmuApi.Runtime;

public static class RuntimeOwnership
{
    public sealed record Health(
        string? RuntimeIdentity,
        int? MainPort,
        string? EnvHome,
        string? ResolvedHome,
        string? Cwd);

    public static bool IsOwned(string expectedIdentity, int expectedPort, string expectedHome, Health health)
    {
        if (string.IsNullOrWhiteSpace(expectedIdentity) || !string.Equals(expectedIdentity, health.RuntimeIdentity, StringComparison.Ordinal))
        {
            return false;
        }

        if (health.MainPort != expectedPort)
        {
            return false;
        }

        var expected = RuntimeValidation.CanonicalPath(expectedHome);
        return new[] { health.EnvHome, health.ResolvedHome, health.Cwd }
            .All(path => path is not null && PathsEqual(path, expected));
    }

    private static bool PathsEqual(string candidate, string expected)
    {
        try
        {
            return string.Equals(RuntimeValidation.CanonicalPath(candidate), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
