using System.Globalization;
using System.Text;

namespace DanmuApi.Core.Frp;

/// <summary>
/// 生成 frpc.toml / frps.toml。frp 自 v0.52.0 起只认 TOML/YAML/JSON，INI 已被移除，
/// 所以这里固定输出 TOML 语法。
///
/// 写文件的人只有本应用（用户在界面上改），因此生成结果必须是<b>整份替换</b>而不是增量合并：
/// 增量合并会让"删掉一个域名"这类操作永远不生效，用户看到的是配置改不掉。
/// </summary>
public static class FrpConfigWriter
{
    public const string ClientFileName = "frpc.toml";
    public const string ServerFileName = "frps.toml";

    private const string Header =
        "# 由「弹幕 API Windows 桌面端」生成，界面每次保存都会整份重写本文件。\n" +
        "# 手工修改会在下次保存/启动时被覆盖；需要自定义字段请用界面里的高级项。\n";

    public static string WriteClient(FrpClientSettings client, string token, string adminUser, string adminPassword)
    {
        ArgumentNullException.ThrowIfNull(client);
        var builder = new StringBuilder(Header);
        Line(builder, "serverAddr", Quote(client.ServerAddress));
        Line(builder, "serverPort", Number(client.ServerPort));
        // 服务商面板的账号标识：写出去之后 frpc 登记的代理名是 {user}.{name}，少了它服务器会拒绝登记。
        if (client.User.Length > 0)
        {
            Line(builder, "user", Quote(client.User));
        }
        // 默认值 true 会让 frpc 在服务器暂时不可达时直接退出（进程消失、界面只剩"启动失败"）。
        // 设为 false 后 frpc 自己重试，界面据管理接口把"正在连接服务器"如实显示出来，
        // 连接成功与否仍然以代理 running 为准，不做任何乐观判定。
        Line(builder, "loginFailExit", "false");
        builder.AppendLine();
        AppendAuth(builder, token);
        AppendLogging(builder);
        Line(builder, "transport.tls.enable", Bool(client.TransportTls));
        builder.AppendLine();
        AppendAdminServer(builder, client.AdminPort, adminUser, adminPassword);
        builder.AppendLine();
        builder.AppendLine("[[proxies]]");
        Line(builder, "name", Quote(client.ProxyName));
        Line(builder, "type", Quote(client.ProxyKind.ToFrpText()));
        Line(builder, "localIP", Quote(client.LocalAddress));
        Line(builder, "localPort", Number(client.LocalPort));
        if (client.ProxyKind.RequiresRemotePort())
        {
            Line(builder, "remotePort", Number(client.RemotePort));
        }
        else
        {
            AppendStringArray(builder, "customDomains", client.CustomDomains);
        }

        // These are BaseProxyConfig options in frp, shared by TCP, HTTP and HTTPS.
        Line(builder, "transport.useEncryption", Bool(client.UseEncryption));
        Line(builder, "transport.useCompression", Bool(client.UseCompression));
        return builder.ToString();
    }

    public static string WriteServer(FrpServerSettings server, string token, string adminUser, string adminPassword)
    {
        ArgumentNullException.ThrowIfNull(server);
        var builder = new StringBuilder(Header);
        Line(builder, "bindPort", Number(server.BindPort));
        builder.AppendLine();
        AppendAuth(builder, token);
        AppendLogging(builder);
        if (server.VhostHttpPort != 0)
        {
            Line(builder, "vhostHTTPPort", Number(server.VhostHttpPort));
        }

        if (server.SubdomainHost.Length > 0)
        {
            Line(builder, "subdomainHost", Quote(server.SubdomainHost));
        }

        builder.AppendLine();
        AppendAdminServer(builder, server.AdminPort, adminUser, adminPassword);
        return builder.ToString();
    }

    private static void AppendAuth(StringBuilder builder, string token)
    {
        if (token.Length == 0)
        {
            return;
        }

        Line(builder, "auth.method", Quote("token"));
        Line(builder, "auth.token", Quote(token));
        builder.AppendLine();
    }

    private static void AppendLogging(StringBuilder builder)
    {
        // 日志默认带 ANSI 颜色转义（实测 0.71.0 即使重定向到文件也带），
        // 会把捕获到的日志尾部变成一堆 [1;34m。关掉颜色后日志文件可以直接给人看。
        Line(builder, "log.disablePrintColor", "true");
    }

    private static void AppendAdminServer(StringBuilder builder, int port, string user, string password)
    {
        Line(builder, "webServer.addr", Quote("127.0.0.1"));
        Line(builder, "webServer.port", Number(port));
        Line(builder, "webServer.user", Quote(user));
        Line(builder, "webServer.password", Quote(password));
    }

    private static void AppendStringArray(StringBuilder builder, string key, IReadOnlyList<string> values)
    {
        var items = string.Join(", ", values.Select(Quote));
        builder.Append(key).Append(" = [").Append(items).Append(']').Append('\n');
    }

    private static void Line(StringBuilder builder, string key, string value) =>
        builder.Append(key).Append(" = ").Append(value).Append('\n');

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "true" : "false";

    /// <summary>TOML 基本字符串转义：反斜杠、引号与所有控制字符必须转义，其余按 UTF-8 原样保留。</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        builder.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
