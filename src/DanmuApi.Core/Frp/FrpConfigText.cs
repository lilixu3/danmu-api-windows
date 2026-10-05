using System.Text.Json;

namespace DanmuApi.Core.Frp;

/// <summary>
/// 粘贴导入的入口：同一段文本既可以是 frp 原生 JSON，也可以是 <c>frpc.toml</c> / <c>frps.toml</c>。
///
/// 判定规则固定且可预期：去掉 BOM 与前置空白后以 <c>{</c> 开头 → 按 JSON 解析，其余 → 按 TOML 解析。
/// 两条路最终都汇聚到 <see cref="FrpConfigJson.ImportDocument"/>，"认得出但不管理"的字段、
/// 我们补的默认值、拒绝导入的问题清单完全一致，用户换一种格式粘贴不会得到两套说法。
/// </summary>
public static class FrpConfigText
{
    public static FrpConfigJsonImport Import(string text, FrpSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (string.IsNullOrWhiteSpace(text)) return FrpConfigJsonImport.Failure("粘贴内容为空");
        var trimmed = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (trimmed.StartsWith('{'))
        {
            return FrpConfigJson.Import(trimmed, current);
        }

        if (!FrpConfigToml.TryParse(trimmed, out var document, out var problems))
        {
            return FrpConfigJsonImport.Failure(problems.Select(problem => $"不是合法的 TOML：{problem}").ToArray());
        }

        // TOML 子集已经解析成 JSON 文档模型，走与 JSON 粘贴同一条导入契约。
        using var parsed = JsonDocument.Parse(document.ToJsonString());
        return FrpConfigJson.ImportDocument(parsed.RootElement, current);
    }
}
