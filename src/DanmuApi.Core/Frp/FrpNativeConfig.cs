using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DanmuApi.Core.Frp;

/// <summary>
/// A native proxy's identities. Name is the frps registered name (including the client's user prefix).
/// AdminName is the unprefixed source name reported by frpc /api/status; monitoring must match it exactly.
/// LocalPort = 0 and an empty LocalAddress mean there is no ordinary local TCP endpoint, for example a plugin.
/// </summary>
public sealed record FrpNativeProxy(string Name, string Type, string LocalAddress, int LocalPort)
{
    public string AdminName { get; init; } = Name;

    public override string ToString() => nameof(FrpNativeProxy);
}

/// <summary>
/// An immutable native document, not an import into the single-proxy visual model. Unknown fields and all proxies
/// survive into the runtime JSON so the official frpc/frps verifier, not a lossy importer, validates their schemas.
/// This object contains secrets. Only Summary/Problems are suitable for diagnostics; Secrets is for redaction.
/// </summary>
public sealed class FrpNativeConfig
{
    public const int MaxDocumentBytes = 32 * 1024;
    public const int MaxDepth = 64;

    private static readonly Encoding DocumentEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = MaxDepth,
    };
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        MaxDepth = MaxDepth,
    };

    private readonly JsonObject _source;
    private readonly FrpClientSettings _clientMetadata;
    private readonly FrpServerSettings _serverMetadata;

    private FrpNativeConfig(JsonObject source, string format, FrpRole role, int adminPort,
        FrpClientSettings clientMetadata, FrpServerSettings serverMetadata,
        IReadOnlyList<FrpNativeProxy> proxies, IReadOnlyList<string> secrets)
    {
        _source = source;
        Format = format;
        Role = role;
        AdminPort = adminPort;
        _clientMetadata = clientMetadata;
        _serverMetadata = serverMetadata;
        ServerAddress = role == FrpRole.Client ? clientMetadata.ServerAddress : string.Empty;
        ServerPort = role == FrpRole.Client ? clientMetadata.ServerPort : 0;
        User = role == FrpRole.Client ? clientMetadata.User : string.Empty;
        BindPort = role == FrpRole.Server ? serverMetadata.BindPort : 0;
        VhostHttpPort = role == FrpRole.Server ? serverMetadata.VhostHttpPort : 0;
        Proxies = proxies;
        Secrets = secrets;
        Summary = role == FrpRole.Client
            ? $"{format} · 客户端 · {proxies.Count} 条代理 · 本地状态端口 {adminPort}"
            : $"{format} · 服务端 · 监听端口 {BindPort} · 本地状态端口 {adminPort}";
    }

    public FrpRole Role { get; }
    public string Format { get; }
    public int AdminPort { get; }
    public string ServerAddress { get; }
    public int ServerPort { get; }
    public int BindPort { get; }
    public int VhostHttpPort { get; }
    public string User { get; }
    public IReadOnlyList<FrpNativeProxy> Proxies { get; }
    public IReadOnlyList<string> Secrets { get; }
    public string Summary { get; }

    public override string ToString() => Summary;

    /// <summary>Parse without connecting to a provider. All rejection messages omit source keys/values and secrets.</summary>
    public static FrpNativeConfig Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw Failure("原生配置文本不能为空");
        if (text.Length > MaxDocumentBytes)
            throw Failure($"原生配置文本超过 {MaxDocumentBytes} 字节（UTF-8）上限");
        try
        {
            if (DocumentEncoding.GetByteCount(text) > MaxDocumentBytes)
                throw Failure($"原生配置文本超过 {MaxDocumentBytes} 字节（UTF-8）上限");
        }
        catch (EncoderFallbackException)
        {
            throw Failure("原生配置文本含无效 Unicode 字符");
        }

        var content = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        JsonObject root;
        string format;
        if (content.StartsWith('{') || content.StartsWith("//", StringComparison.Ordinal)
            || content.StartsWith("/*", StringComparison.Ordinal))
        {
            format = "JSON";
            root = ParseJson(content);
        }
        else
        {
            format = "TOML";
            CheckTomlNesting(content);
            try
            {
                if (!FrpConfigToml.TryParse(content, out root, out var errors))
                    throw new FrpConfigurationException(errors.Select(SafeTomlProblem).ToArray());
            }
            catch (ArgumentException)
            {
                // For example an out-of-range Unicode escape. Do not retain an exception containing source text.
                throw Failure("TOML 语法无效：Unicode 转义或文档结构不合法");
            }
            catch (InvalidOperationException)
            {
                throw Failure("TOML 语法无效：文档结构不合法");
            }
            CheckDepth(root);
        }
        return ReadMetadata(root, format);
    }

    /// <summary>
    /// Deep-clone the complete AST and apply only the explicit Windows-host policies. The source is never changed.
    /// webServer TLS is rejected during Parse because monitoring uses an authenticated, plaintext loopback endpoint.
    /// </summary>
    public string CreateRuntimeConfig(string adminUser, string adminPassword)
    {
        if (string.IsNullOrWhiteSpace(adminUser) || adminUser.Contains(':') || adminUser.Any(char.IsControl))
            throw Failure("宿主管理接口用户名无效");
        if (string.IsNullOrWhiteSpace(adminPassword) || adminPassword.Any(char.IsControl))
            throw Failure("宿主管理接口密码无效");

        var runtime = (JsonObject)_source.DeepClone();
        var webServer = runtime["webServer"] as JsonObject ?? new JsonObject();
        if (!runtime.ContainsKey("webServer")) runtime["webServer"] = webServer;
        webServer["addr"] = "127.0.0.1";
        webServer["port"] = AdminPort;
        webServer["user"] = adminUser;
        webServer["password"] = adminPassword;

        var log = runtime["log"] as JsonObject ?? new JsonObject();
        if (!runtime.ContainsKey("log")) runtime["log"] = log;
        log["to"] = "console";
        log["disablePrintColor"] = true;
        if (Role == FrpRole.Client) runtime["loginFailExit"] = false;
        return runtime.ToJsonString(WriteOptions) + Environment.NewLine;
    }

    /// <summary>
    /// Metadata-only monitor snapshot. ProxyName stays unprefixed, User stays separate. With no API-type proxy,
    /// the display target/name are empty and LocalPort is zero. Never use this snapshot to rebuild the document.
    /// The inactive visual configuration, mode and original raw text remain intact.
    /// </summary>
    public FrpSettings Describe(FrpSettings visual)
    {
        ArgumentNullException.ThrowIfNull(visual);
        return Role == FrpRole.Client
            ? visual with { Role = Role, Client = _clientMetadata }
            : visual with { Role = Role, Server = _serverMetadata };
    }

    private static JsonObject ParseJson(string text)
    {
        try
        {
            using var parsed = JsonDocument.Parse(text, ReadOptions);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                throw Failure("JSON 根节点必须是对象");
            CheckDuplicateProperties(parsed.RootElement);
            // Do not convert to JsonNode before checking duplicates: a conversion could overwrite/reject them late.
            return (JsonObject)JsonNode.Parse(parsed.RootElement.GetRawText(), documentOptions: ReadOptions)!;
        }
        catch (JsonException error)
        {
            // JsonException.Message/Path can include a source character/key. Only numerical positions are safe.
            throw Failure($"不是合法的 JSON：第 {error.LineNumber.GetValueOrDefault() + 1} 行，字节位置 "
                + $"{error.BytePositionInLine.GetValueOrDefault()}（语法错误或超过 {MaxDepth} 层嵌套）");
        }
        catch (InvalidOperationException)
        {
            // JsonDocument can defer decoding a malformed surrogate escape until GetString/Name is accessed.
            throw Failure("JSON 字符串或字段名称含无效 Unicode 转义");
        }
    }

    private static void CheckDuplicateProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Failure("JSON 对象含重复字段（字段名称不回显以保护凭据）");
                CheckDuplicateProperties(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in node.EnumerateArray()) CheckDuplicateProperties(value);
        }
        else if (node.ValueKind == JsonValueKind.String)
        {
            _ = node.GetString(); // Validate escaped Unicode even in unknown extension fields before conversion.
        }
    }

    private static FrpNativeConfig ReadMetadata(JsonObject root, string format)
    {
        var problems = new List<string>();
        var hasServer = HasAny(root, "bindPort", "bindAddr", "vhostHTTPPort", "vhostHTTPSPort", "subdomainHost");
        var hasClient = HasAny(root, "serverAddr", "serverPort", "proxies", "visitors");
        if (hasServer == hasClient)
            throw Failure(hasServer
                ? "原生配置同时包含客户端与服务端字段，不能混用两种角色"
                : "原生配置缺少可判定角色的 serverAddr/proxies 或 bindPort 等字段");
        var role = hasClient ? FrpRole.Client : FrpRole.Server;

        var auth = ReadObject(root, "auth", "auth", problems);
        if (auth is not null)
        {
            _ = ReadString(auth, "method", "auth.method", "token", problems, nonBlank: true);
            _ = ReadString(auth, "token", "auth.token", string.Empty, problems);
            _ = ReadObject(auth, "oidc", "auth.oidc", problems);
        }
        var log = ReadObject(root, "log", "log", problems);
        if (log is not null)
        {
            _ = ReadString(log, "to", "log.to", "console", problems);
            _ = ReadString(log, "level", "log.level", "info", problems);
            _ = ReadBool(log, "disablePrintColor", "log.disablePrintColor", false, problems);
        }
        var transport = ReadObject(root, "transport", "transport", problems);
        var tlsEnabled = true;
        if (transport is not null)
        {
            _ = ReadString(transport, "protocol", "transport.protocol", "tcp", problems, nonBlank: true);
            var tls = ReadObject(transport, "tls", "transport.tls", problems);
            if (tls is not null)
                tlsEnabled = ReadBool(tls, "enable", "transport.tls.enable", true, problems);
        }
        _ = ReadBool(root, "loginFailExit", "loginFailExit", true, problems);

        var webServer = ReadObject(root, "webServer", "webServer", problems);
        var adminPort = role == FrpRole.Client ? FrpClientSettings.DefaultAdminPort : FrpServerSettings.DefaultAdminPort;
        if (webServer is not null)
        {
            var address = ReadString(webServer, "addr", "webServer.addr", "127.0.0.1", problems, nonBlank: true);
            if (!IsLoopback(address)) problems.Add("webServer.addr 必须是回环地址：宿主管理接口不得对外监听");
            adminPort = ReadPort(webServer, "port", "webServer.port", adminPort, problems);
            _ = ReadString(webServer, "user", "webServer.user", string.Empty, problems);
            _ = ReadString(webServer, "password", "webServer.password", string.Empty, problems);
            if (webServer.ContainsKey("tls"))
                problems.Add("webServer.tls 与宿主的明文回环管理接口不兼容，请明确删除该字段后运行");
        }

        var serverAddress = role == FrpRole.Client
            ? ReadString(root, "serverAddr", "serverAddr", string.Empty, problems, required: true, nonBlank: true)
            : string.Empty;
        if (role == FrpRole.Client && (serverAddress.Any(char.IsWhiteSpace) || serverAddress.Contains('/')))
            problems.Add("serverAddr 必须是地址本身，不能包含空白、协议或路径");
        var serverPort = role == FrpRole.Client
            ? ReadPort(root, "serverPort", "serverPort", FrpClientSettings.DefaultServerPort, problems) : 0;
        var user = role == FrpRole.Client ? ReadString(root, "user", "user", string.Empty, problems) : string.Empty;
        var bindPort = role == FrpRole.Server
            ? ReadPort(root, "bindPort", "bindPort", FrpServerSettings.DefaultBindPort, problems) : 0;
        var vhostHttpPort = role == FrpRole.Server ? ReadPort(root, "vhostHTTPPort", "vhostHTTPPort", 0, problems, allowZero: true) : 0;
        var vhostHttpsPort = role == FrpRole.Server ? ReadPort(root, "vhostHTTPSPort", "vhostHTTPSPort", 0, problems, allowZero: true) : 0;
        var subdomainHost = role == FrpRole.Server ? ReadString(root, "subdomainHost", "subdomainHost", string.Empty, problems) : string.Empty;
        if (role == FrpRole.Server)
        {
            _ = ReadString(root, "bindAddr", "bindAddr", "0.0.0.0", problems, nonBlank: true);
            if (bindPort == adminPort || vhostHttpPort == adminPort || vhostHttpsPort == adminPort)
                problems.Add("服务端监听端口不能占用宿主管理接口端口");
        }

        var proxies = new List<FrpNativeProxy>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        FrpClientSettings? firstApi = null;
        if (root.TryGetPropertyValue("proxies", out var proxyNode))
        {
            if (proxyNode is not JsonArray array) problems.Add("proxies 必须是对象数组，不能为 null");
            else
            {
                for (var index = 0; index < array.Count; index++)
                {
                    var field = $"proxies[{index}]";
                    if (array[index] is not JsonObject proxy)
                    {
                        problems.Add($"{field} 必须是对象，不能为 null");
                        continue;
                    }
                    var name = ReadString(proxy, "name", field + ".name", string.Empty, problems, required: true, nonBlank: true);
                    var type = ReadString(proxy, "type", field + ".type", "tcp", problems, nonBlank: true);
                    var registeredName = user.Length == 0 ? name : user + "." + name;
                    if (!names.Add(registeredName)) problems.Add($"{field}.name 与另一条代理重复");
                    var plugin = ReadObject(proxy, "plugin", field + ".plugin", problems);
                    var isApiType = type is "tcp" or "http" or "https";
                    var localPort = ReadPort(proxy, "localPort", field + ".localPort", 0, problems,
                        allowZero: plugin is not null || !isApiType, required: plugin is null && isApiType);
                    var localAddress = ReadString(proxy, "localIP", field + ".localIP", "127.0.0.1", problems, nonBlank: true);
                    var remotePort = ReadPort(proxy, "remotePort", field + ".remotePort", 0, problems, allowZero: true);
                    var domains = ReadStrings(proxy, "customDomains", field + ".customDomains", problems);
                    var proxyTransport = ReadObject(proxy, "transport", field + ".transport", problems);
                    var encryption = proxyTransport is not null
                        && ReadBool(proxyTransport, "useEncryption", field + ".transport.useEncryption", false, problems);
                    var compression = proxyTransport is not null
                        && ReadBool(proxyTransport, "useCompression", field + ".transport.useCompression", false, problems);
                    _ = ReadObject(proxy, "healthCheck", field + ".healthCheck", problems);
                    // A plugin uses its own endpoint, even when an unused localPort also appears in the source.
                    if (plugin is not null || localPort == 0)
                    {
                        localPort = 0;
                        localAddress = string.Empty;
                    }
                    if (localPort == adminPort && IsLoopback(localAddress))
                        problems.Add($"{field}.localPort 不能占用宿主管理接口端口");
                    proxies.Add(new FrpNativeProxy(registeredName, type, localAddress, localPort) { AdminName = name });
                    if (isApiType && firstApi is null)
                        firstApi = new FrpClientSettings(serverAddress, serverPort, user, name, ToApiKind(type),
                            localAddress, localPort, remotePort, domains, encryption, compression, tlsEnabled, adminPort);
                }
            }
        }
        if (role == FrpRole.Client && proxies.Count == 0)
            problems.Add("客户端原生配置至少需要一个 proxies 条目；宿主无法监控仅 visitors 或没有代理的穿透");
        if (root.TryGetPropertyValue("visitors", out var visitors) && visitors is not JsonArray)
            problems.Add("visitors 必须是对象数组，不能为 null");
        if (problems.Count > 0) throw new FrpConfigurationException(problems.AsReadOnly());

        var clientMetadata = firstApi ?? new FrpClientSettings(serverAddress, serverPort, user, string.Empty,
            FrpProxyKind.Tcp, string.Empty, 0, 0, Array.Empty<string>(), false, false, tlsEnabled, adminPort);
        var serverMetadata = new FrpServerSettings(bindPort, vhostHttpPort, subdomainHost, adminPort);
        return new FrpNativeConfig(root, format, role, adminPort, clientMetadata, serverMetadata,
            proxies.AsReadOnly(), CollectSecrets(root));
    }

    private static FrpProxyKind ToApiKind(string type) => type switch
    {
        "tcp" => FrpProxyKind.Tcp,
        "http" => FrpProxyKind.Http,
        "https" => FrpProxyKind.Https,
        _ => throw new InvalidOperationException("非 API 代理类型不能转换为可视化类型"),
    };

    private static bool HasAny(JsonObject root, params string[] names) => names.Any(root.ContainsKey);

    private static bool IsLoopback(string address) =>
        string.Equals(address, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(address, out var ip) && IPAddress.IsLoopback(ip));

    private static JsonObject? ReadObject(JsonObject parent, string name, string field, List<string> problems)
    {
        if (!parent.TryGetPropertyValue(name, out var node)) return null;
        if (node is JsonObject value) return value;
        problems.Add($"{field} 必须是对象，不能为 null");
        return null;
    }

    // Defaults apply only to absent native fields. Invalid present values always add a problem and reject Parse.
    private static string ReadString(JsonObject parent, string name, string field, string absentValue,
        List<string> problems, bool required = false, bool nonBlank = false)
    {
        if (!parent.TryGetPropertyValue(name, out var node))
        {
            if (required) problems.Add($"缺少字段 {field}");
            return absentValue;
        }
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            problems.Add($"{field} 必须是字符串，不能为 null");
            return string.Empty;
        }
        if (nonBlank && string.IsNullOrWhiteSpace(text)) problems.Add($"{field} 不能为空");
        return text;
    }

    private static int ReadPort(JsonObject parent, string name, string field, int absentValue,
        List<string> problems, bool allowZero = false, bool required = false)
    {
        if (!parent.TryGetPropertyValue(name, out var node))
        {
            if (required) problems.Add($"缺少字段 {field}");
            return absentValue;
        }
        if (node is not JsonValue value)
        {
            problems.Add($"{field} 必须是整数，不能为 null");
            return 0;
        }
        long number;
        if (value.TryGetValue<int>(out var intValue)) number = intValue;
        else if (!value.TryGetValue<long>(out number))
        {
            problems.Add($"{field} 必须是整数，不能为 null");
            return 0;
        }
        if (number > FrpSettingsValidation.MaxPort || number < (allowZero ? 0 : FrpSettingsValidation.MinPort))
        {
            problems.Add($"{field} 必须在 {(allowZero ? 0 : FrpSettingsValidation.MinPort)} 到 {FrpSettingsValidation.MaxPort} 之间");
            return 0;
        }
        return (int)number;
    }

    private static bool ReadBool(JsonObject parent, string name, string field, bool absentValue, List<string> problems)
    {
        if (!parent.TryGetPropertyValue(name, out var node)) return absentValue;
        if (node is JsonValue value && value.TryGetValue<bool>(out var result)) return result;
        problems.Add($"{field} 必须是布尔值，不能为 null");
        return false;
    }

    private static IReadOnlyList<string> ReadStrings(JsonObject parent, string name, string field, List<string> problems)
    {
        if (!parent.TryGetPropertyValue(name, out var node)) return Array.Empty<string>();
        if (node is not JsonArray array)
        {
            problems.Add($"{field} 必须是字符串数组，不能为 null");
            return Array.Empty<string>();
        }
        var strings = new List<string>();
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonValue value || !value.TryGetValue<string>(out var text)
                || string.IsNullOrWhiteSpace(text))
                problems.Add($"{field}[{index}] 必须是非空字符串，不能为 null");
            else strings.Add(text);
        }
        return strings.AsReadOnly();
    }

    private static IReadOnlyList<string> CollectSecrets(JsonObject root)
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        Visit(root, false);
        return Array.AsReadOnly(secrets.ToArray());

        void Visit(JsonNode? node, bool sensitive)
        {
            if (node is JsonObject obj)
            {
                foreach (var pair in obj)
                    Visit(pair.Value, sensitive || IsCredentialKey(pair.Key));
            }
            else if (node is JsonArray array)
            {
                foreach (var child in array) Visit(child, sensitive);
            }
            else if (sensitive && node is JsonValue value && value.TryGetValue<string>(out var text)
                     && !string.IsNullOrWhiteSpace(text))
                secrets.Add(text);
        }
    }

    private static bool IsCredentialKey(string key) =>
        string.Equals(key, "auth", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "user", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "username", StringComparison.OrdinalIgnoreCase)
        || new[] { "token", "password", "passwd", "secret", "cookie", "key", "authorization", "credential", "header" }
            .Any(part => key.Contains(part, StringComparison.OrdinalIgnoreCase));

    private static void CheckDepth(JsonObject root)
    {
        var pending = new Stack<(JsonNode Node, int Depth)>();
        pending.Push((root, 1));
        while (pending.TryPop(out var item))
        {
            if (item.Depth > MaxDepth) throw Failure($"TOML 配置超过 {MaxDepth} 层嵌套");
            if (item.Node is JsonObject obj)
            {
                foreach (var child in obj.Select(pair => pair.Value)) Push(child, item.Depth);
            }
            else if (item.Node is JsonArray array)
            {
                foreach (var child in array) Push(child, item.Depth);
            }
        }
        void Push(JsonNode? child, int parentDepth)
        {
            if (child is JsonObject or JsonArray) pending.Push((child, parentDepth + 1));
        }
    }

    // Bound recursive TOML value parsing before calling the existing parser (a post-parse limit is too late).
    private static void CheckTomlNesting(string text)
    {
        var depth = 0;
        var line = 1;
        var quote = '\0';
        var escaped = false;
        var comment = false;
        foreach (var character in text)
        {
            if (character == '\n') { line++; comment = false; }
            if (comment) continue;
            if (quote != '\0')
            {
                if (escaped) { escaped = false; continue; }
                if (quote == '"' && character == '\\') { escaped = true; continue; }
                if (character == quote) quote = '\0';
                continue;
            }
            if (character == '#') { comment = true; continue; }
            if (character is '"' or '\'') { quote = character; continue; }
            if (character is '[' or '{')
            {
                if (++depth >= MaxDepth)
                    throw Failure($"TOML 第 {line} 行：配置超过 {MaxDepth} 层嵌套");
            }
            else if (character is ']' or '}') depth--;
        }
    }

    private static string SafeTomlProblem(string problem)
    {
        // The old TOML parser includes literal values/quoted keys in some errors. Keep its line but no raw payload.
        var colon = problem.IndexOf('：');
        var linePrefix = "TOML";
        if (colon > 0 && problem.StartsWith("第 ", StringComparison.Ordinal)
            && int.TryParse(problem.AsSpan(2, colon - 4), NumberStyles.None, CultureInfo.InvariantCulture, out var line))
            linePrefix = $"TOML 第 {line} 行";
        var reason = problem.Contains("不支持多行字符串", StringComparison.Ordinal) ? "不支持多行字符串"
            : problem.Contains("不支持小数", StringComparison.Ordinal) ? "不支持小数值"
            : problem.Contains("不支持日期/时间", StringComparison.Ordinal) ? "不支持日期/时间值"
            : problem.Contains("不支持十六进制", StringComparison.Ordinal) ? "不支持十六进制/八进制/二进制整数"
            : problem.Contains("重复的键", StringComparison.Ordinal) ? "重复的键"
            : problem.Contains("重复定义", StringComparison.Ordinal) ? "重复的表定义"
            : problem.Contains("整数字面量无效", StringComparison.Ordinal) ? "整数字面量无效"
            : problem.Contains("字符串没有闭合", StringComparison.Ordinal) ? "字符串没有闭合"
            : problem.Contains("不支持的转义", StringComparison.Ordinal) ? "不支持的字符串转义"
            : problem.Contains("Unicode 码位无效", StringComparison.Ordinal) ? "Unicode 码位无效"
            : problem.Contains("转义需要", StringComparison.Ordinal) ? "Unicode 转义格式无效"
            : "语法无效或包含不支持的写法，请检查键、表头、引号与分隔符";
        return $"{linePrefix}：{reason}";
    }

    private static FrpConfigurationException Failure(string problem) => new([problem]);
}
