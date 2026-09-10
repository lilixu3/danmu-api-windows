using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DanmuApi.Core;

public sealed class CoreEnvCatalogException : FormatException
{
    public CoreEnvCatalogException(string message, int line, int column)
        : base($"{message}（第 {line} 行，第 {column} 列）")
    {
        Line = line;
        Column = column;
    }

    public int Line { get; }

    public int Column { get; }
}

public static class CoreEnvCatalog
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex CatalogDeclaration = new(
        @"\bconst\s+envVarConfig\s*=",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex KeyPattern = new(
        @"^[A-Z][A-Z0-9_]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> SupportedFields = new(StringComparer.Ordinal)
    {
        "category",
        "type",
        "description",
        "options",
        "sources",
        "min",
        "max",
        "encrypt",
    };
    private static readonly string[] StaticArrayNames =
    [
        "ALLOWED_SOURCES",
        "ALLOWED_PLATFORMS",
        "VOD_ALLOWED_PLATFORMS",
        "MERGE_ALLOWED_SOURCES",
    ];
    private static readonly IReadOnlyList<object?> DanAnyFormats =
    [
        "artplayer.json",
        "baha.json",
        "bili.xml",
        "danuni.json",
        "danuni.binpb",
        "ddplay.json",
        "dplayer.json",
        "vod.json",
    ];

    public static IReadOnlyList<CoreEnvDefinition> ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"核心环境变量定义文件不存在：{path}", path);
        }

        return Parse(StrictUtf8.GetString(File.ReadAllBytes(path)));
    }

    public static IReadOnlyList<CoreEnvDefinition> Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var declaration = CatalogDeclaration.Match(source);
        if (!declaration.Success)
        {
            throw Failure(source, "envs.js 缺少 envVarConfig 定义", 0);
        }

        var symbols = ReadStaticSymbols(source);
        var parser = new JsValueParser(source, declaration.Index + declaration.Length, symbols);
        var parsed = parser.ParseValue();
        if (parsed is not Dictionary<string, object?> catalog || catalog.Count == 0)
        {
            throw Failure(source, "envVarConfig 必须是非空对象", declaration.Index);
        }

        var getMetadata = ReadGetMetadata(source);
        var definitions = new List<CoreEnvDefinition>(catalog.Count);
        foreach (var (key, rawDefinition) in catalog)
        {
            var keyIndex = FindCatalogKey(source, declaration.Index, key);
            if (!KeyPattern.IsMatch(key))
            {
                throw Failure(source, $"envVarConfig 包含非法变量名：{key}", keyIndex);
            }

            if (CoreEnvDefinitionRules.IsHostOwned(key))
            {
                throw Failure(source, $"envVarConfig 暴露了 Desktop 宿主变量：{key}", keyIndex);
            }

            if (rawDefinition is not Dictionary<string, object?> metadata)
            {
                throw Failure(source, $"变量 {key} 的定义不是对象", keyIndex);
            }

            var unknownField = metadata.Keys.FirstOrDefault(field => !SupportedFields.Contains(field));
            if (unknownField is not null)
            {
                throw Failure(source, $"变量 {key} 包含不支持的元数据字段：{unknownField}", keyIndex);
            }

            var category = RequiredString(source, metadata, key, "category", keyIndex);
            var type = ParseType(source, RequiredString(source, metadata, key, "type", keyIndex), key, keyIndex);
            var description = RequiredString(source, metadata, key, "description", keyIndex);
            var options = ReadStringList(source, metadata, key, "options", keyIndex);
            var sources = ReadStringList(source, metadata, key, "sources", keyIndex);
            var minimum = ReadNumber(source, metadata, key, "min", keyIndex);
            var maximum = ReadNumber(source, metadata, key, "max", keyIndex);
            if (minimum is not null && maximum is not null && minimum > maximum)
            {
                throw Failure(source, $"变量 {key} 的 min 大于 max", keyIndex);
            }

            getMetadata.TryGetValue(key, out var access);
            var encrypted = ReadBoolean(source, metadata, key, "encrypt", keyIndex) == true;
            definitions.Add(new CoreEnvDefinition(
                key,
                category,
                type,
                description,
                options,
                InferSources(key, sources, symbols),
                minimum,
                maximum,
                access?.DefaultValue,
                encrypted || access?.Encrypted == true || CoreEnvDefinitionRules.IsSensitiveName(key),
                key.Equals("ADMIN_TOKEN", StringComparison.Ordinal),
                CoreEnvApplyMode.HotReload));
        }

        return definitions;
    }

    private static Dictionary<string, object?> ReadStaticSymbols(string source)
    {
        var symbols = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["danAnyFormats"] = DanAnyFormats,
        };

        foreach (var name in StaticArrayNames)
        {
            var declaration = Regex.Match(
                source,
                $@"\bstatic\s+{Regex.Escape(name)}\s*=",
                RegexOptions.CultureInvariant);
            if (!declaration.Success)
            {
                continue;
            }

            var parser = new JsValueParser(source, declaration.Index + declaration.Length, symbols);
            var value = parser.ParseValue();
            if (value is not IReadOnlyList<object?>)
            {
                throw Failure(source, $"静态常量 {name} 必须是数组", declaration.Index);
            }

            symbols[$"this.{name}"] = value;
            symbols[name] = value;
        }

        return symbols;
    }

    private static IReadOnlyList<string> InferSources(
        string key,
        IReadOnlyList<string> declared,
        IReadOnlyDictionary<string, object?> symbols)
    {
        if (declared.Count > 0)
        {
            return declared;
        }

        var symbol = key switch
        {
            "AUTO_MATCH_MAPPING_TABLE" => "this.ALLOWED_PLATFORMS",
            _ => null,
        };
        return symbol is null || !symbols.TryGetValue(symbol, out var value)
            ? Array.Empty<string>()
            : SanitizeStrings((IEnumerable)value!);
    }

    private static Dictionary<string, GetMetadata> ReadGetMetadata(string source)
    {
        var result = new Dictionary<string, GetMetadata>(StringComparer.Ordinal);
        var cursor = 0;
        while (cursor < source.Length)
        {
            var index = source.IndexOf("this.get", cursor, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            cursor = index + "this.get".Length;
            var open = SkipTrivia(source, cursor);
            if (open >= source.Length || source[open] != '(')
            {
                continue;
            }

            var call = ParseCall(source, open);
            cursor = call.EndIndex;
            if (call.Arguments.Count < 2)
            {
                throw Failure(source, "this.get 调用缺少变量名或默认值", index);
            }

            var key = EvaluateStringLiteral(source, call.Arguments[0]);
            if (!KeyPattern.IsMatch(key))
            {
                throw Failure(source, $"this.get 使用了非法变量名：{key}", call.Arguments[0].Start);
            }

            var encrypted = call.Arguments.Count >= 4 &&
                            EvaluateBooleanLiteral(source, call.Arguments[3]);
            if (result.TryGetValue(key, out var existing))
            {
                if (encrypted && !existing.Encrypted)
                {
                    result[key] = existing with { Encrypted = true };
                }

                continue;
            }

            result[key] = new GetMetadata(
                EvaluateDefault(source, call.Arguments[1], new HashSet<string>(StringComparer.Ordinal)),
                encrypted);
        }

        return result;
    }

    private static string EvaluateDefault(string source, Segment expression, HashSet<string> resolving)
    {
        var value = expression.Text(source).Trim();
        if (value.Length == 0 || value is "null" or "undefined")
        {
            return string.Empty;
        }

        if (value[0] is '\'' or '"' or '`')
        {
            return EvaluateStringLiteral(source, expression);
        }

        if (bool.TryParse(value, out var boolean))
        {
            return boolean ? "true" : "false";
        }

        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return value;
        }

        var identifier = value.StartsWith("this.", StringComparison.Ordinal) ? value[5..] : value;
        if (!Regex.IsMatch(identifier, @"^[A-Za-z_$][A-Za-z0-9_$]*$", RegexOptions.CultureInvariant))
        {
            throw Failure(source, $"无法安全解析默认值表达式：{value}", expression.Start);
        }

        if (!resolving.Add(identifier))
        {
            throw Failure(source, $"默认值常量存在循环引用：{identifier}", expression.Start);
        }

        var declaration = Regex.Match(
            source,
            $@"\b(?:static\s+)?(?:const\s+|let\s+|var\s+)?{Regex.Escape(identifier)}\s*=",
            RegexOptions.CultureInvariant);
        if (!declaration.Success)
        {
            throw Failure(source, $"默认值引用了未知静态常量：{value}", expression.Start);
        }

        var constant = ReadExpression(source, declaration.Index + declaration.Length);
        var evaluated = EvaluateDefault(source, constant, resolving);
        resolving.Remove(identifier);
        return evaluated;
    }

    private static Segment ReadExpression(string source, int start)
    {
        var index = SkipTrivia(source, start);
        var begin = index;
        var quote = '\0';
        var escaped = false;
        while (index < source.Length)
        {
            var character = source[index];
            if (quote != '\0')
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == quote)
                {
                    quote = '\0';
                }

                index++;
                continue;
            }

            if (character is '\'' or '"' or '`')
            {
                quote = character;
                index++;
                continue;
            }

            if (character == ';' || character is '\r' or '\n')
            {
                break;
            }

            index++;
        }

        return new Segment(begin, index);
    }

    private static ParsedCall ParseCall(string source, int open)
    {
        var arguments = new List<Segment>();
        var argumentStart = open + 1;
        var index = argumentStart;
        var depth = 0;
        var quote = '\0';
        var escaped = false;
        while (index < source.Length)
        {
            var character = source[index];
            if (quote != '\0')
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == quote)
                {
                    quote = '\0';
                }

                index++;
                continue;
            }

            switch (character)
            {
                case '\'':
                case '"':
                case '`':
                    quote = character;
                    break;
                case '(':
                case '[':
                case '{':
                    depth++;
                    break;
                case ')':
                    if (depth == 0)
                    {
                        arguments.Add(new Segment(argumentStart, index));
                        return new ParsedCall(arguments, index + 1);
                    }

                    depth--;
                    break;
                case ']':
                case '}':
                    if (depth == 0)
                    {
                        throw Failure(source, "this.get 调用括号不匹配", index);
                    }

                    depth--;
                    break;
                case ',':
                    if (depth == 0)
                    {
                        arguments.Add(new Segment(argumentStart, index));
                        argumentStart = index + 1;
                    }

                    break;
            }

            index++;
        }

        throw Failure(source, "this.get 调用缺少结束括号", open);
    }

    private static string EvaluateStringLiteral(string source, Segment segment)
    {
        var start = segment.Start;
        while (start < segment.End && char.IsWhiteSpace(source[start]))
        {
            start++;
        }

        if (start >= segment.End || source[start] is not ('\'' or '"' or '`'))
        {
            throw Failure(source, "this.get 变量名必须是字符串字面量", start);
        }

        var parser = new JsValueParser(source, start, new Dictionary<string, object?>());
        if (parser.ParseValue() is not string value)
        {
            throw Failure(source, "字符串字面量解析失败", start);
        }

        var tail = parser.Position;
        while (tail < segment.End && char.IsWhiteSpace(source[tail]))
        {
            tail++;
        }

        if (tail != segment.End)
        {
            throw Failure(source, "字符串字面量后包含不支持的表达式", tail);
        }

        return value;
    }

    private static bool EvaluateBooleanLiteral(string source, Segment segment)
    {
        var value = segment.Text(source).Trim();
        return value switch
        {
            "true" => true,
            "false" => false,
            _ => throw Failure(source, $"敏感标记必须是布尔字面量：{value}", segment.Start),
        };
    }

    private static string RequiredString(
        string source,
        IReadOnlyDictionary<string, object?> metadata,
        string key,
        string field,
        int index)
    {
        if (!metadata.TryGetValue(field, out var raw) || raw is not string value || string.IsNullOrWhiteSpace(value))
        {
            throw Failure(source, $"变量 {key} 缺少非空字符串字段 {field}", index);
        }

        return value.Trim();
    }

    private static IReadOnlyList<string> ReadStringList(
        string source,
        IReadOnlyDictionary<string, object?> metadata,
        string key,
        string field,
        int index)
    {
        if (!metadata.TryGetValue(field, out var raw))
        {
            return Array.Empty<string>();
        }

        if (raw is not IEnumerable values || raw is string)
        {
            throw Failure(source, $"变量 {key} 的 {field} 必须是字符串数组", index);
        }

        try
        {
            var result = SanitizeStrings(values);
            if (result.Count == 0)
            {
                throw new FormatException("数组不能为空");
            }

            return result;
        }
        catch (FormatException error)
        {
            throw Failure(source, $"变量 {key} 的 {field} 非法：{error.Message}", index);
        }
    }

    private static IReadOnlyList<string> SanitizeStrings(IEnumerable values)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in values)
        {
            if (raw is not string text || string.IsNullOrWhiteSpace(text))
            {
                throw new FormatException("选项数组只能包含非空字符串");
            }

            var normalized = text == "*" ? "all" : text.Trim();
            if (!seen.Add(normalized))
            {
                throw new FormatException($"选项数组包含重复值：{normalized}");
            }

            result.Add(normalized);
        }

        return result;
    }

    private static decimal? ReadNumber(
        string source,
        IReadOnlyDictionary<string, object?> metadata,
        string key,
        string field,
        int index)
    {
        if (!metadata.TryGetValue(field, out var raw))
        {
            return null;
        }

        if (raw is decimal number)
        {
            return number;
        }

        throw Failure(source, $"变量 {key} 的 {field} 必须是数字", index);
    }

    private static bool? ReadBoolean(
        string source,
        IReadOnlyDictionary<string, object?> metadata,
        string key,
        string field,
        int index)
    {
        if (!metadata.TryGetValue(field, out var raw))
        {
            return null;
        }

        if (raw is bool boolean)
        {
            return boolean;
        }

        throw Failure(source, $"变量 {key} 的 {field} 必须是布尔值", index);
    }

    private static CoreEnvType ParseType(string source, string type, string key, int index) =>
        type.Trim().ToLowerInvariant() switch
        {
            "text" or "string" => CoreEnvType.Text,
            "number" or "int" or "integer" => CoreEnvType.Number,
            "boolean" or "bool" => CoreEnvType.Boolean,
            "select" => CoreEnvType.Select,
            "multi-select" or "multiselect" => CoreEnvType.MultiSelect,
            "map" => CoreEnvType.Map,
            _ => throw Failure(source, $"变量 {key} 使用了不支持的类型：{type}", index),
        };

    private static int FindCatalogKey(string source, int start, string key)
    {
        var quoted = source.IndexOf($"'{key}'", start, StringComparison.Ordinal);
        if (quoted >= 0)
        {
            return quoted;
        }

        quoted = source.IndexOf($"\"{key}\"", start, StringComparison.Ordinal);
        return quoted >= 0 ? quoted : start;
    }

    private static int SkipTrivia(string source, int start)
    {
        var index = start;
        while (index < source.Length)
        {
            while (index < source.Length && char.IsWhiteSpace(source[index]))
            {
                index++;
            }

            if (index + 1 < source.Length && source[index] == '/' && source[index + 1] == '/')
            {
                index = source.IndexOf('\n', index + 2);
                if (index < 0)
                {
                    return source.Length;
                }

                continue;
            }

            if (index + 1 < source.Length && source[index] == '/' && source[index + 1] == '*')
            {
                var end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    throw Failure(source, "块注释缺少结束标记", index);
                }

                index = end + 2;
                continue;
            }

            return index;
        }

        return index;
    }

    private static CoreEnvCatalogException Failure(string source, string message, int index)
    {
        index = Math.Clamp(index, 0, source.Length);
        var line = 1;
        var column = 1;
        for (var cursor = 0; cursor < index; cursor++)
        {
            if (source[cursor] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        return new CoreEnvCatalogException(message, line, column);
    }

    private sealed record GetMetadata(string DefaultValue, bool Encrypted);

    private sealed record ParsedCall(IReadOnlyList<Segment> Arguments, int EndIndex);

    private readonly record struct Segment(int Start, int End)
    {
        public string Text(string source) => source[Start..End];
    }

    private sealed class JsValueParser
    {
        private readonly string _source;
        private readonly IReadOnlyDictionary<string, object?> _symbols;
        private int _index;

        public JsValueParser(string source, int start, IReadOnlyDictionary<string, object?> symbols)
        {
            _source = source;
            _index = start;
            _symbols = symbols;
        }

        public int Position => _index;

        public object? ParseValue()
        {
            _index = SkipTrivia(_source, _index);
            if (_index >= _source.Length)
            {
                throw Failure(_source, "JavaScript 值意外结束", _index);
            }

            return _source[_index] switch
            {
                '{' => ParseObject(),
                '[' => ParseArray(),
                '\'' or '"' or '`' => ParseString(),
                '-' or >= '0' and <= '9' => ParseNumber(),
                _ => ParseIdentifierValue(),
            };
        }

        private Dictionary<string, object?> ParseObject()
        {
            _index++;
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            _index = SkipTrivia(_source, _index);
            while (_index < _source.Length && _source[_index] != '}')
            {
                var keyIndex = _index;
                var key = _source[_index] is '\'' or '"' ? ParseString() : ParseIdentifier();
                _index = SkipTrivia(_source, _index);
                if (_index >= _source.Length || _source[_index] != ':')
                {
                    throw Failure(_source, $"对象字段 {key} 缺少冒号", _index);
                }

                _index++;
                var value = ParseValue();
                if (!result.TryAdd(key, value))
                {
                    throw Failure(_source, $"对象包含重复字段：{key}", keyIndex);
                }

                _index = SkipTrivia(_source, _index);
                if (_index < _source.Length && _source[_index] == ',')
                {
                    _index = SkipTrivia(_source, _index + 1);
                }
                else if (_index >= _source.Length || _source[_index] != '}')
                {
                    throw Failure(_source, $"对象字段 {key} 后缺少逗号或结束括号", _index);
                }
            }

            if (_index >= _source.Length || _source[_index] != '}')
            {
                throw Failure(_source, "对象缺少结束括号", _index);
            }

            _index++;
            return result;
        }

        private IReadOnlyList<object?> ParseArray()
        {
            _index++;
            var result = new List<object?>();
            _index = SkipTrivia(_source, _index);
            while (_index < _source.Length && _source[_index] != ']')
            {
                if (_source.AsSpan(_index).StartsWith("...", StringComparison.Ordinal))
                {
                    var spreadIndex = _index;
                    _index += 3;
                    var identifier = ParseIdentifier();
                    if (!_symbols.TryGetValue(identifier, out var expanded) ||
                        expanded is not IEnumerable values ||
                        expanded is string)
                    {
                        throw Failure(_source, $"不支持的数组展开表达式：{identifier}", spreadIndex);
                    }

                    foreach (var value in values)
                    {
                        result.Add(value);
                    }
                }
                else
                {
                    result.Add(ParseValue());
                }

                _index = SkipTrivia(_source, _index);
                if (_index < _source.Length && _source[_index] == ',')
                {
                    _index = SkipTrivia(_source, _index + 1);
                }
                else if (_index >= _source.Length || _source[_index] != ']')
                {
                    throw Failure(_source, "数组元素后缺少逗号或结束括号", _index);
                }
            }

            if (_index >= _source.Length || _source[_index] != ']')
            {
                throw Failure(_source, "数组缺少结束括号", _index);
            }

            _index++;
            return result;
        }

        private string ParseString()
        {
            var quote = _source[_index++];
            var result = new StringBuilder();
            while (_index < _source.Length)
            {
                var character = _source[_index++];
                if (character == quote)
                {
                    return result.ToString();
                }

                if (quote == '`' && character == '$' && _index < _source.Length && _source[_index] == '{')
                {
                    throw Failure(_source, "模板字符串插值不受支持", _index - 1);
                }

                if (character != '\\')
                {
                    result.Append(character);
                    continue;
                }

                if (_index >= _source.Length)
                {
                    throw Failure(_source, "字符串转义序列不完整", _index - 1);
                }

                var escaped = _source[_index++];
                result.Append(escaped switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'b' => '\b',
                    'f' => '\f',
                    'v' => '\v',
                    '0' => '\0',
                    '\\' => '\\',
                    '\'' => '\'',
                    '"' => '"',
                    '`' => '`',
                    'u' => ParseUnicodeEscape(),
                    _ => escaped,
                });
            }

            throw Failure(_source, "字符串缺少结束引号", _index);
        }

        private char ParseUnicodeEscape()
        {
            if (_index + 4 > _source.Length ||
                !ushort.TryParse(_source.AsSpan(_index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                throw Failure(_source, "Unicode 转义序列非法", _index);
            }

            _index += 4;
            return (char)value;
        }

        private decimal ParseNumber()
        {
            var begin = _index;
            if (_source[_index] == '-')
            {
                _index++;
            }

            while (_index < _source.Length &&
                   (char.IsDigit(_source[_index]) || _source[_index] is '.' or 'e' or 'E' or '+' or '-'))
            {
                _index++;
            }

            var raw = _source[begin.._index];
            if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                throw Failure(_source, $"数字字面量非法：{raw}", begin);
            }

            return value;
        }

        private object? ParseIdentifierValue()
        {
            var start = _index;
            var identifier = ParseIdentifier();
            return identifier switch
            {
                "true" => true,
                "false" => false,
                "null" => null,
                _ when _symbols.TryGetValue(identifier, out var value) => value,
                _ => throw Failure(_source, $"不支持的 envVarConfig 表达式：{identifier}", start),
            };
        }

        private string ParseIdentifier()
        {
            var begin = _index;
            if (_index >= _source.Length ||
                !(char.IsLetter(_source[_index]) || _source[_index] is '_' or '$'))
            {
                throw Failure(_source, "缺少 JavaScript 标识符", _index);
            }

            _index++;
            while (_index < _source.Length &&
                   (char.IsLetterOrDigit(_source[_index]) || _source[_index] is '_' or '$' or '.'))
            {
                _index++;
            }

            return _source[begin.._index];
        }
    }
}
