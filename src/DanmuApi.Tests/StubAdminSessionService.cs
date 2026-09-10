using DanmuApi.App.Services;

namespace DanmuApi.Tests;

/// <summary>共享测试替身：可控的管理员会话状态与调用记录。</summary>
public sealed class StubAdminSessionService : IAdminSessionService
{
    public StubAdminSessionService(bool adminMode = false, bool configured = false, string? sessionToken = null)
    {
        State = new AdminSessionState(adminMode, configured, adminMode || configured ? "ad***in" : "未配置");
        Token = sessionToken;
    }

    public string? Token { get; set; }
    public AdminSessionState State { get; set; }
    public int RefreshCalls { get; private set; }
    public int LogoutCalls { get; private set; }
    public List<string> LoginInputs { get; } = [];
    public List<string> SetTokenInputs { get; } = [];
    public AdminSessionOperationResult LoginResult { get; set; } = AdminSessionOperationResult.Failure("not used");
    public AdminSessionOperationResult SetResult { get; set; } = AdminSessionOperationResult.Failure("not used");

    public void Refresh() => RefreshCalls++;
    public string? CurrentAdminTokenOrNull() => State.IsAdminMode ? Token : null;

    public AdminSessionOperationResult Login(string inputToken)
    {
        LoginInputs.Add(inputToken);
        if (LoginResult.Succeeded)
        {
            State = new AdminSessionState(true, true, State.TokenHint);
        }

        return LoginResult;
    }

    public AdminSessionOperationResult SetAdminTokenAndLogin(string token)
    {
        SetTokenInputs.Add(token);
        if (SetResult.Succeeded)
        {
            State = new AdminSessionState(true, true, "se***ss");
        }

        return SetResult;
    }

    public AdminSessionOperationResult Logout()
    {
        LogoutCalls++;
        State = new AdminSessionState(false, State.HasAdminTokenConfigured, State.TokenHint);
        return AdminSessionOperationResult.Success("已退出管理员模式");
    }
}
