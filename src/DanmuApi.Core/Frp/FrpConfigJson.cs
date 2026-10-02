using System.Text.Json;
using System.Text.Json.Nodes;

namespace DanmuApi.Core.Frp;

/// <summary>Problems reject the document; Unsupported and AppliedDefaults must be shown even for a successful import.</summary>
public sealed record FrpConfigJsonImport(
    FrpSettings? Settings,
    string? Token,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Unsupported,
    IReadOnlyList<string> AppliedDefaults)
{
    public bool Succeeded => Settings is not null && Problems.Count == 0;
    public static FrpConfigJsonImport Failure(params string[] problems) => new(null, null, problems, [], []);
    public static FrpConfigJsonImport Failure(IReadOnlyList<string> problems, IReadOnlyList<string> unsupported) =>
        new(null, null, problems, unsupported, []);
}

/// <summary>
/// Native frp JSON, excluding this machine's management credentials. Import replaces the described role,
/// preserves the opposite role and host settings, and never writes storage. A successful null Token means clear auth.
/// </summary>
public static class FrpConfigJson
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Export(FrpSettings settings, string token)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var root = new JsonObject();
        if (settings.Role == FrpRole.Client)
        {
            var client = settings.Client;
            root["serverAddr"] = client.ServerAddress;
            root["serverPort"] = client.ServerPort;
            root["loginFailExit"] = false;
            root["transport"] = new JsonObject { ["tls"] = new JsonObject { ["enable"] = client.TransportTls } };
            var proxy = new JsonObject
            {
                ["name"] = client.ProxyName,
                ["type"] = client.ProxyKind.ToFrpText(),
                ["localIP"] = client.LocalAddress,
                ["localPort"] = client.LocalPort,
                ["transport"] = new JsonObject
                {
                    ["useEncryption"] = client.UseEncryption,
                    ["useCompression"] = client.UseCompression,
                },
            };
            if (client.ProxyKind.RequiresRemotePort())
                proxy["remotePort"] = client.RemotePort;
            else
                proxy["customDomains"] = new JsonArray(client.CustomDomains.Select(domain => (JsonNode)domain!).ToArray());
            root["proxies"] = new JsonArray(proxy);
        }
        else
        {
            var server = settings.Server;
            root["bindPort"] = server.BindPort;
            if (server.VhostHttpPort != 0) root["vhostHTTPPort"] = server.VhostHttpPort;
            if (server.SubdomainHost.Length > 0) root["subdomainHost"] = server.SubdomainHost;
        }

        if (token.Length > 0) root["auth"] = new JsonObject { ["method"] = "token", ["token"] = token };
        root["log"] = new JsonObject { ["disablePrintColor"] = true };
        root["webServer"] = new JsonObject
        {
            ["addr"] = "127.0.0.1",
            ["port"] = settings.Role == FrpRole.Client ? settings.Client.AdminPort : settings.Server.AdminPort,
        };
        return root.ToJsonString(WriteOptions) + Environment.NewLine;
    }

    public static FrpConfigJsonImport Import(string json, FrpSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (string.IsNullOrWhiteSpace(json)) return FrpConfigJsonImport.Failure("粘贴内容为空");
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException error)
        {
            // JsonException contains position information, not pasted secret values.
            return FrpConfigJsonImport.Failure($"不是合法的 JSON：{error.Message}");
        }

        using (parsed)
        {
            var document = parsed.RootElement;
            if (document.ValueKind != JsonValueKind.Object)
                return FrpConfigJsonImport.Failure("JSON 根节点必须是一个对象");
            var problems = new List<string>();
            var unsupported = new List<string>();
            var defaults = new List<string>();
            CheckDuplicateKeys(document, string.Empty, problems);
            var hasServer = document.TryGetProperty("bindPort", out _);
            var hasClient = document.TryGetProperty("serverAddr", out _) || document.TryGetProperty("proxies", out _);
            if (hasServer == hasClient)
            {
                problems.Add(hasServer
                    ? "这份 JSON 同时含 frps 的 bindPort 与 frpc 的 serverAddr/proxies，无法判断是客户端还是服务端配置"
                    : "这份 JSON 既没有 bindPort（服务端）也没有 serverAddr/proxies（客户端）");
                return new(null, null, problems, unsupported, defaults);
            }

            var token = ImportAuth(document, problems, unsupported, defaults);
            ImportLog(document, problems, unsupported, defaults);
            var settings = hasClient
                ? current with { Role = FrpRole.Client, Client = ImportClient(document, current.Client.ProxyKind, problems, unsupported, defaults) }
                : current with { Role = FrpRole.Server, Server = ImportServer(document, problems, unsupported, defaults) };
            problems.AddRange(settings.Validate());
            return new(problems.Count == 0 ? settings : null, token.Length == 0 ? null : token, problems, unsupported, defaults);
        }
    }

    private static FrpClientSettings ImportClient(JsonElement document, FrpProxyKind currentKind,
        List<string> problems, List<string> unsupported, List<string> defaults)
    {
        var baseline = FrpSettings.Default(9321).Client;
        var address = ReadString(document, "serverAddr", "", problems, defaults, required: true);
        var port = ReadPort(document, "serverPort", FrpClientSettings.DefaultServerPort, problems, defaults);
        var tls = baseline.TransportTls;
        if (ReadObject(document, "transport", problems, out var transport))
        {
            if (ReadObject(transport, "tls", problems, out var tlsObject, "transport"))
            {
                tls = ReadBool(tlsObject, "enable", baseline.TransportTls, problems, defaults, "transport.tls");
                ReportUnknown(tlsObject, ["enable"], "transport.tls", unsupported);
            }
            else if (!transport.TryGetProperty("tls", out _)) Default(defaults, "transport.tls.enable", "true");
            ReportUnknown(transport, ["tls"], "transport", unsupported);
        }
        else if (!document.TryGetProperty("transport", out _)) Default(defaults, "transport.tls.enable", "true");

        if (document.TryGetProperty("loginFailExit", out _))
        {
            if (ReadBool(document, "loginFailExit", false, problems, defaults))
                unsupported.Add("loginFailExit=true：宿主固定使用 false，由 frp 重连");
        }
        else Default(defaults, "loginFailExit", "false（宿主重连策略）");

        var adminPort = ImportWebServer(document, baseline.AdminPort, problems, unsupported, defaults);
        var client = baseline with { ServerAddress = address, ServerPort = port, TransportTls = tls, AdminPort = adminPort };
        if (!document.TryGetProperty("proxies", out var proxies) || proxies.ValueKind != JsonValueKind.Array || proxies.GetArrayLength() == 0)
        {
            problems.Add("客户端配置必须包含至少一个 proxies 条目（对象数组）");
        }
        else
        {
            if (proxies.GetArrayLength() > 1)
                unsupported.Add($"proxies：本应用只管理一条代理，已导入第一条（共 {proxies.GetArrayLength()} 条）");
            var index = 0;
            foreach (var proxy in proxies.EnumerateArray())
            {
                var prefix = $"proxies[{index}]";
                if (proxy.ValueKind != JsonValueKind.Object) problems.Add($"{prefix} 必须是对象");
                else
                {
                    // Extra proxies are unsupported, but malformed managed fields are still rejected.
                    var imported = ImportProxy(proxy, client, currentKind, prefix, problems, unsupported, index == 0 ? defaults : []);
                    if (index == 0) client = imported;
                    else problems.AddRange(FrpSettingsValidation.ValidateClient(imported).Select(problem => $"{prefix}：{problem}"));
                }
                index++;
            }
        }
        ReportUnknown(document, ["serverAddr", "serverPort", "loginFailExit", "auth", "log", "transport", "webServer", "proxies"], "", unsupported);
        return client;
    }

    private static FrpClientSettings ImportProxy(JsonElement proxy, FrpClientSettings client, FrpProxyKind currentKind,
        string prefix, List<string> problems, List<string> unsupported, List<string> defaults)
    {
        var name = ReadString(proxy, "name", FrpSettings.DefaultProxyName, problems, defaults, prefix);
        var localIp = ReadString(proxy, "localIP", FrpClientSettings.DefaultLocalAddress, problems, defaults, prefix);
        var localPort = ReadPort(proxy, "localPort", 0, problems, defaults, prefix, required: true);
        var kind = currentKind;
        if (proxy.TryGetProperty("type", out _))
        {
            var text = ReadString(proxy, "type", "", problems, defaults, prefix);
            switch (text)
            {
                case "tcp": kind = FrpProxyKind.Tcp; break;
                case "http": kind = FrpProxyKind.Http; break;
                case "https": kind = FrpProxyKind.Https; break;
                default: problems.Add($"{prefix}.type 取值无效（只能是 tcp、http 或 https）"); break;
            }
        }
        else Default(defaults, prefix + ".type", $"当前的「{currentKind.ToLabel()}」");

        var remotePort = ReadPort(proxy, "remotePort", localPort, problems, defaults, prefix, required: kind.RequiresRemotePort());
        var domains = new List<string>();
        if (proxy.TryGetProperty("customDomains", out var domainArray))
        {
            if (domainArray.ValueKind != JsonValueKind.Array) problems.Add($"{prefix}.customDomains 必须是字符串数组");
            else
            {
                var index = 0;
                foreach (var domain in domainArray.EnumerateArray())
                {
                    if (domain.ValueKind != JsonValueKind.String) problems.Add($"{prefix}.customDomains[{index}] 必须是字符串");
                    else
                    {
                        var text = domain.GetString()!.Trim();
                        if (text.Length == 0 || text.Any(char.IsWhiteSpace) || text.Contains('/') || text.Contains(':'))
                            problems.Add($"{prefix}.customDomains[{index}] 域名格式无效（不能是空串、协议、端口或路径）");
                        domains.Add(text);
                    }
                    index++;
                }
            }
        }
        else Default(defaults, prefix + ".customDomains", "空列表（清除原域名）");

        var encryption = false;
        var compression = false;
        if (ReadObject(proxy, "transport", problems, out var transport, prefix))
        {
            encryption = ReadBool(transport, "useEncryption", false, problems, defaults, prefix + ".transport");
            compression = ReadBool(transport, "useCompression", false, problems, defaults, prefix + ".transport");
            ReportUnknown(transport, ["useEncryption", "useCompression"], prefix + ".transport", unsupported);
        }
        else if (!proxy.TryGetProperty("transport", out _))
        {
            Default(defaults, prefix + ".transport.useEncryption", "false");
            Default(defaults, prefix + ".transport.useCompression", "false");
        }
        ReportUnknown(proxy, ["name", "type", "localIP", "localPort", "remotePort", "customDomains", "transport"], prefix, unsupported);
        return client with
        {
            ProxyName = name, ProxyKind = kind, LocalAddress = localIp, LocalPort = localPort,
            RemotePort = remotePort, CustomDomains = domains, UseEncryption = encryption, UseCompression = compression,
        };
    }

    private static FrpServerSettings ImportServer(JsonElement document,
        List<string> problems, List<string> unsupported, List<string> defaults)
    {
        var bind = ReadPort(document, "bindPort", 0, problems, defaults, required: true);
        var vhost = ReadPort(document, "vhostHTTPPort", 0, problems, defaults, allowZero: true);
        var subdomain = ReadString(document, "subdomainHost", "", problems, defaults);
        var admin = ImportWebServer(document, FrpServerSettings.DefaultAdminPort, problems, unsupported, defaults);
        ReportUnknown(document, ["bindPort", "auth", "log", "vhostHTTPPort", "subdomainHost", "webServer"], "", unsupported);
        return new(bind, vhost, subdomain, admin);
    }

    private static int ImportWebServer(JsonElement document, int defaultPort,
        List<string> problems, List<string> unsupported, List<string> defaults)
    {
        if (!ReadObject(document, "webServer", problems, out var server))
        {
            if (!document.TryGetProperty("webServer", out _))
            {
                Default(defaults, "webServer.addr", "127.0.0.1");
                Default(defaults, "webServer.port", defaultPort.ToString());
            }
            return defaultPort;
        }
        var addr = ReadString(server, "addr", "127.0.0.1", problems, defaults, "webServer");
        if (!string.Equals(addr, "127.0.0.1", StringComparison.Ordinal))
            problems.Add("webServer.addr 只能是 127.0.0.1：本地状态接口必须监听回环");
        var port = ReadPort(server, "port", defaultPort, problems, defaults, "webServer");
        ReportUnknown(server, ["addr", "port"], "webServer", unsupported);
        return port;
    }

    private static string ImportAuth(JsonElement document,
        List<string> problems, List<string> unsupported, List<string> defaults)
    {
        if (!ReadObject(document, "auth", problems, out var auth))
        {
            if (!document.TryGetProperty("auth", out _)) Default(defaults, "auth.token", "空（清除原 Token）");
            return "";
        }
        var method = ReadString(auth, "method", "token", problems, defaults, "auth");
        if (method != "token") problems.Add("auth.method 只支持 token");
        var token = ReadString(auth, "token", "", problems, defaults, "auth");
        ReportUnknown(auth, ["method", "token"], "auth", unsupported);
        return token;
    }

    private static void ImportLog(JsonElement document,
        List<string> problems, List<string> unsupported, List<string> defaults)
    {
        if (ReadObject(document, "log", problems, out var log))
        {
            if (!ReadBool(log, "disablePrintColor", true, problems, defaults, "log"))
                unsupported.Add("log.disablePrintColor=false：宿主固定关闭日志颜色");
            ReportUnknown(log, ["disablePrintColor"], "log", unsupported);
        }
        else if (!document.TryGetProperty("log", out _)) Default(defaults, "log.disablePrintColor", "true（宿主日志策略）");
    }

    private static bool ReadObject(JsonElement document, string name, List<string> problems, out JsonElement value, string prefix = "")
    {
        if (!document.TryGetProperty(name, out value)) return false;
        if (value.ValueKind == JsonValueKind.Object) return true;
        problems.Add($"{Field(prefix, name)} 必须是对象");
        return false;
    }

    private static string ReadString(JsonElement document, string name, string defaultValue,
        List<string> problems, List<string> defaults, string prefix = "", bool required = false)
    {
        if (!document.TryGetProperty(name, out var value))
        {
            if (required) problems.Add($"缺少字段 {Field(prefix, name)}");
            else Default(defaults, Field(prefix, name), defaultValue.Length == 0 ? "空（清除原值）" : defaultValue);
            return defaultValue;
        }
        if (value.ValueKind == JsonValueKind.String) return value.GetString()!.Trim();
        problems.Add($"{Field(prefix, name)} 必须是字符串");
        return "";
    }

    private static int ReadPort(JsonElement document, string name, int defaultValue,
        List<string> problems, List<string> defaults, string prefix = "", bool required = false, bool allowZero = false)
    {
        var field = Field(prefix, name);
        if (!document.TryGetProperty(name, out var value))
        {
            if (required) problems.Add($"缺少字段 {field}");
            else Default(defaults, field, defaultValue.ToString());
            return defaultValue;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            problems.Add($"{field} 必须是整数");
            return 0;
        }
        if (!(allowZero && number == 0) && (number is < FrpSettingsValidation.MinPort or > FrpSettingsValidation.MaxPort))
            problems.Add($"{field} 必须在 {FrpSettingsValidation.MinPort} 到 {FrpSettingsValidation.MaxPort} 之间" + (allowZero ? "，或为 0（不启用）" : ""));
        return number;
    }

    private static bool ReadBool(JsonElement document, string name, bool defaultValue,
        List<string> problems, List<string> defaults, string prefix = "")
    {
        if (!document.TryGetProperty(name, out var value))
        {
            Default(defaults, Field(prefix, name), defaultValue ? "true" : "false");
            return defaultValue;
        }
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        problems.Add($"{Field(prefix, name)} 必须是布尔值（true/false）");
        return false;
    }

    private static void CheckDuplicateKeys(JsonElement value, string prefix, List<string> problems)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                var field = Field(prefix, property.Name);
                if (!names.Add(property.Name)) problems.Add($"重复字段 {field}");
                CheckDuplicateKeys(property.Value, field, problems);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray()) CheckDuplicateKeys(item, $"{prefix}[{index++}]", problems);
        }
    }

    private static void ReportUnknown(JsonElement document, string[] known, string prefix, List<string> unsupported)
    {
        foreach (var property in document.EnumerateObject())
            if (!known.Contains(property.Name, StringComparer.Ordinal)) unsupported.Add(Field(prefix, property.Name));
    }

    private static void Default(List<string> defaults, string field, string value) => defaults.Add($"{field} 未给出，填入 {value}");
    private static string Field(string prefix, string name) => prefix.Length == 0 ? name : prefix + "." + name;
}
