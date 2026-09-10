using System.Text.RegularExpressions;

namespace DanmuApi.Core;

public enum CoreRepositorySource
{
    Official,
    Custom,
}

public sealed partial record GithubRepositoryReference(
    string Owner,
    string Repository,
    string? Branch,
    CoreRepositorySource Source)
{
    public const string OfficialOwner = "huangxd-";
    public const string OfficialRepository = "danmu_api";

    public string FullName => $"{Owner}/{Repository}";
    public bool HasExplicitBranch => !string.IsNullOrWhiteSpace(Branch);

    public static GithubRepositoryReference Official(string? branch = null) =>
        new(OfficialOwner, OfficialRepository, NormalizeOptionalBranch(branch), CoreRepositorySource.Official);

    public static GithubRepositoryReference Parse(string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        var trimmed = input.Trim();
        string path;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !uri.IsDefaultPort ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new FormatException("自定义核心地址必须是 https://github.com 上的仓库或分支地址");
            }

            path = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
        }
        else
        {
            if (trimmed.Contains("://", StringComparison.Ordinal) || trimmed.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException("自定义核心地址格式无效");
            }

            path = trimmed.Trim('/');
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is not 2 && (parts.Length < 4 || !string.Equals(parts[2], "tree", StringComparison.OrdinalIgnoreCase)))
        {
            throw new FormatException("GitHub 仓库地址必须是 owner/repo 或 https://github.com/owner/repo/tree/branch");
        }

        var owner = ValidateRepositoryPart(parts[0], "owner");
        var repository = ValidateRepositoryPart(parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? parts[1][..^4]
            : parts[1], "repo");
        var branch = parts.Length >= 4
            ? ValidateBranch(string.Join('/', parts.Skip(3)))
            : null;
        var source = string.Equals(owner, OfficialOwner, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(repository, OfficialRepository, StringComparison.OrdinalIgnoreCase)
            ? CoreRepositorySource.Official
            : CoreRepositorySource.Custom;
        return new GithubRepositoryReference(owner, repository, branch, source);
    }

    public GithubRepositoryReference WithBranch(string branch) => this with { Branch = ValidateBranch(branch) };

    public static string ValidateBranch(string branch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        var value = branch.Trim();
        if (value.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
        {
            value = value["refs/heads/".Length..];
        }

        value = value.Trim('/');
        if (value.Length is 0 or > 255 ||
            value.Contains("..", StringComparison.Ordinal) ||
            value.Contains("@{", StringComparison.Ordinal) ||
            value.Contains("//", StringComparison.Ordinal) ||
            value.EndsWith(".", StringComparison.Ordinal) ||
            value.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ||
            value.Any(character => char.IsControl(character) || character is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\'))
        {
            throw new FormatException("GitHub 分支名无效");
        }

        return value;
    }

    private static string? NormalizeOptionalBranch(string? branch) =>
        string.IsNullOrWhiteSpace(branch) ? null : ValidateBranch(branch);

    private static string ValidateRepositoryPart(string value, string label)
    {
        if (!RepositoryPartPattern().IsMatch(value))
        {
            throw new FormatException($"GitHub 仓库 {label} 无效");
        }

        return value;
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPartPattern();
}
