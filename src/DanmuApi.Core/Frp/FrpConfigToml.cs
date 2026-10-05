using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace DanmuApi.Core.Frp;

/// <summary>
/// frp 的 TOML 子集转为完整 JSON 文档；不支持的语法带行号拒绝，不跳过或猜值。
/// 原生运行路径使用 <see cref="FrpNativeConfig"/>，不得从单代理表单导入结果重建配置。
/// 支持基本/字面字符串、整数、布尔、数组、内联表、点分键、普通表及数组表的子表。
/// </summary>
public static class FrpConfigToml
{
    public static bool TryParse(string text, out JsonObject document, out IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(text);
        document = new JsonObject();
        var errors = new List<string>();
        new Parser(text, document, errors).Run();
        problems = errors;
        return errors.Count == 0;
    }

    private sealed class Parser
    {
        /// <summary>显式打开过的表对象：同一数组表的不同条目可拥有同名子表，同一对象不许重复打开。</summary>
        private readonly HashSet<JsonObject> _openedTables = new(ReferenceEqualityComparer.Instance);

        /// <summary>点分赋值已定义的父表不能再被表头重新定义；表头隐式创建的父表仍可显式打开。</summary>
        private readonly HashSet<JsonObject> _dottedTables = new(ReferenceEqualityComparer.Instance);

        /// <summary>完整的内联表及其子表不可在定义之外扩展，包括通过点分键或表头下钻。</summary>
        private readonly HashSet<JsonObject> _sealedTables = new(ReferenceEqualityComparer.Instance);

        /// <summary>仅 [[...]] 声明的数组可沿表头下钻到最近条目；普通值数组不猜它的表语义。</summary>
        private readonly HashSet<JsonArray> _arrayTables = new(ReferenceEqualityComparer.Instance);

        private readonly string _text;
        private readonly JsonObject _root;
        private readonly List<string> _problems;
        private int _index;
        private int _line = 1;
        private JsonObject _current;

        public Parser(string text, JsonObject root, List<string> problems)
        {
            _text = text;
            _root = root;
            _problems = problems;
            _current = root;
        }

        public void Run()
        {
            // 粘贴内容可能带 BOM（Windows 剪贴板常见），先吃掉再解析。
            if (_index < _text.Length && _text[_index] == '\uFEFF') _index++;

            while (true)
            {
                SkipBlank();
                if (AtEnd) return;

                if (Peek() == '[')
                {
                    if (!ReadTableHeader()) return;
                    continue;
                }

                if (!ReadAssignment()) return;
            }
        }

        private bool ReadTableHeader()
        {
            var headerLine = _line;
            Advance(); // '['
            var isArrayTable = Peek() == '[';
            if (isArrayTable) Advance();
            SkipInlineWhitespace();
            if (!TryReadKeyPath(out var path)) return false;
            SkipInlineWhitespace();
            if (Peek() != ']')
            {
                return Fail(headerLine, "表头没有闭合（缺少 ']'）");
            }

            Advance();
            if (isArrayTable)
            {
                if (Peek() != ']') return Fail(headerLine, "数组表头没有闭合（缺少 ']]'）");
                Advance();
            }

            if (!RequireLineEnd(headerLine, "表头")) return false;

            var table = isArrayTable ? AppendArrayTable(path, headerLine) : OpenTable(path, headerLine);
            if (table is null) return false;
            _current = table;
            return true;
        }

        private bool ReadAssignment()
        {
            var startLine = _line;
            if (!TryReadKeyPath(out var path)) return false;
            SkipInlineWhitespace();
            if (Peek() != '=')
            {
                return Fail(startLine, "键后面缺少 '='（TOML 的一行要么是键值对，要么是表头）");
            }

            Advance();
            var value = ReadValue();
            if (value is null) return false;
            if (!RequireLineEnd(startLine, "值")) return false;
            return Assign(_current, path, value, startLine);
        }

        /// <summary>数组表：<c>[[proxies]]</c> 每次出现都往数组里追加一个新的表对象。</summary>
        private JsonObject? AppendArrayTable(IReadOnlyList<string> path, int line)
        {
            var parent = Descend(path, line, createIfMissing: true);
            if (parent is null) return null;
            var name = path[^1];
            if (!parent.TryGetPropertyValue(name, out var existing) || existing is null)
            {
                var array = new JsonArray();
                var entry = new JsonObject();
                array.Add(entry);
                parent[name] = array;
                _arrayTables.Add(array);
                return entry;
            }

            if (existing is JsonArray arrayValue && _arrayTables.Contains(arrayValue))
            {
                var entry = new JsonObject();
                arrayValue.Add(entry);
                return entry;
            }

            return FailValue<JsonObject>(line, "该路径已经被定义成普通值，不能再当作数组表（[[...]]）使用");
        }

        /// <summary>表头：<c>[a.b]</c> 打开（或在前缀已存在时复用）一个表。同一路径重复定义必须报错。</summary>
        private JsonObject? OpenTable(IReadOnlyList<string> path, int line)
        {
            var parent = Descend(path, line, createIfMissing: true);
            if (parent is null) return null;
            var name = path[^1];
            if (parent.TryGetPropertyValue(name, out var existing) && existing is not null)
            {
                if (existing is JsonArray)
                {
                    return FailValue<JsonObject>(line, "该路径是数组表，不能再用单表头（[...]）打开");
                }

                if (existing is not JsonObject existingTable)
                    return FailValue<JsonObject>(line, "该路径已经被定义成普通值，不能再当作表使用");
                if (_sealedTables.Contains(existingTable))
                    return FailValue<JsonObject>(line, "内联表已完整定义，不能再通过表头打开或扩展");
                if (_dottedTables.Contains(existingTable) || !_openedTables.Add(existingTable))
                    return FailValue<JsonObject>(line, "表重复定义（包括已通过点分键定义的表）");
                return existingTable;
            }

            var table = new JsonObject();
            parent[name] = table;
            _openedTables.Add(table);
            return table;
        }

        /// <summary>沿点分路径下钻到父表；中间缺失的段按表创建。</summary>
        private JsonObject? Descend(IReadOnlyList<string> path, int line, bool createIfMissing)
        {
            var node = _root;
            for (var index = 0; index < path.Count - 1; index++)
            {
                var name = path[index];
                if (!node.TryGetPropertyValue(name, out var child) || child is null)
                {
                    if (!createIfMissing) return null;
                    var created = new JsonObject();
                    node[name] = created;
                    node = created;
                    continue;
                }

                if (child is JsonArray array && _arrayTables.Contains(array) && array.Count > 0
                    && array[^1] is JsonObject lastTable)
                {
                    // TOML [proxies.transport] refers to the last [[proxies]] item, never to every item.
                    node = lastTable;
                }
                else if (child is JsonObject childObject)
                {
                    if (_sealedTables.Contains(childObject))
                        return FailValue<JsonObject>(line, "内联表已完整定义，不能再通过表头下钻或扩展");
                    node = childObject;
                }
                else
                {
                    return FailValue<JsonObject>(line, "表头路径的中间段不是已声明的表或数组表，无法继续定义");
                }
            }

            return node;
        }

        private bool Assign(JsonObject table, IReadOnlyList<string> path, JsonNode value, int line)
        {
            var node = table;
            for (var index = 0; index < path.Count - 1; index++)
            {
                var name = path[index];
                if (!node.TryGetPropertyValue(name, out var child) || child is null)
                {
                    var created = new JsonObject();
                    node[name] = created;
                    _dottedTables.Add(created);
                    node = created;
                    continue;
                }

                if (child is not JsonObject childObject)
                {
                    return Fail(line, "点分键的中间段已经被定义成普通值");
                }
                if (_sealedTables.Contains(childObject))
                    return Fail(line, "内联表已完整定义，不能再通过点分键扩展");

                _dottedTables.Add(childObject);
                node = childObject;
            }

            var leaf = path[^1];
            if (node.ContainsKey(leaf))
            {
                return Fail(line, "重复的键");
            }

            node[leaf] = value;
            return true;
        }

        private JsonNode? ReadValue()
        {
            SkipInlineWhitespace();
            var line = _line;
            var character = Peek();
            switch (character)
            {
                case '"':
                    if (Peek(1) == '"' && Peek(2) == '"')
                    {
                        return FailValue<JsonNode>(line, "不支持多行字符串（\"\"\"）");
                    }

                    return ReadBasicString();
                case '\'':
                    if (Peek(1) == '\'' && Peek(2) == '\'')
                    {
                        return FailValue<JsonNode>(line, "不支持多行字符串（'''）");
                    }

                    return ReadLiteralString();
                case '[':
                    return ReadArray();
                case '{':
                    return ReadInlineTable();
                case 't':
                case 'f':
                    return ReadBoolean();
                default:
                    if (character is '+' or '-' || char.IsAsciiDigit(character)) return ReadInteger();
                    return FailValue<JsonNode>(line, $"无法识别的值：{Describe(character)}");
            }
        }

        private JsonNode? ReadBoolean()
        {
            var line = _line;
            var word = Peek() == 't' ? "true" : "false";
            foreach (var expected in word)
            {
                if (Peek() != expected) return FailValue<JsonNode>(line, "无法识别的布尔值");
                Advance();
            }

            if (IsBareKeyCharacter(Peek()))
            {
                return FailValue<JsonNode>(line, "无法识别的布尔值");
            }

            return JsonValue.Create(word == "true");
        }

        /// <summary>整数：支持下划线分隔与正负号；小数/日期/十六进制等一律显式报错，不做近似解释。</summary>
        private JsonNode? ReadInteger()
        {
            var line = _line;
            var builder = new StringBuilder();
            while (!AtEnd)
            {
                var character = Peek();
                if (char.IsAsciiDigit(character) || character is '_' or '+' or '-')
                {
                    builder.Append(character);
                    Advance();
                    continue;
                }

                if (character is '.' or 'e' or 'E')
                {
                    return FailValue<JsonNode>(line, "不支持小数值（端口、数量这类字段必须是整数）");
                }

                if (character == ':')
                {
                    return FailValue<JsonNode>(line, "不支持日期/时间值");
                }

                break;
            }

            var literal = builder.ToString();
            // 0x/0o/0b 以数字开头，会被上面收成 "0"，这里显式拒绝而不是留给"多余内容"的通用报错。
            if (literal == "0" && Peek() is 'x' or 'X' or 'o' or 'O' or 'b' or 'B')
            {
                return FailValue<JsonNode>(line, "不支持十六进制/八进制/二进制整数");
            }

            // 日期/时间（2026-10-04）会被收成 "2026-10-04"：负号只允许出现在最前面。
            var signOffset = literal.Length > 0 && (literal[0] == '+' || literal[0] == '-') ? 1 : 0;
            if (literal.IndexOf('-', signOffset) >= 0)
            {
                return FailValue<JsonNode>(line, "不支持日期/时间值");
            }

            // 下划线只能出现在数字之间：开头、结尾或连续都算非法。
            if (literal.Contains("__", StringComparison.Ordinal)
                || literal.StartsWith('_')
                || literal.EndsWith('_')
                || literal.Contains("-_", StringComparison.Ordinal)
                || literal.Contains("+_", StringComparison.Ordinal))
            {
                return FailValue<JsonNode>(line, "整数字面量无效");
            }

            var digits = literal.Replace("_", string.Empty, StringComparison.Ordinal);
            if (digits.Length - signOffset > 1 && digits[signOffset] == '0')
                return FailValue<JsonNode>(line, "整数字面量无效：十进制整数不允许前导零");
            if (!long.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
            {
                return FailValue<JsonNode>(line, "整数字面量无效");
            }

            return JsonValue.Create(number);
        }

        private JsonNode? ReadArray()
        {
            var line = _line;
            Advance(); // '['
            var array = new JsonArray();
            while (true)
            {
                SkipBlank();
                if (AtEnd) return FailValue<JsonNode>(line, "数组没有闭合（缺少 ']'）");
                if (Peek() == ']')
                {
                    Advance();
                    return array;
                }

                var item = ReadValue();
                if (item is null) return null;
                array.Add(item);
                SkipBlank();
                if (Peek() == ',')
                {
                    Advance();
                    continue;
                }

                if (Peek() == ']')
                {
                    // TOML 允许尾随逗号。
                    Advance();
                    return array;
                }

                return FailValue<JsonNode>(_line, "数组元素之间缺少逗号");
            }
        }

        private JsonNode? ReadInlineTable()
        {
            var line = _line;
            Advance(); // '{'
            var table = new JsonObject();
            while (true)
            {
                SkipBlank();
                if (AtEnd) return FailValue<JsonNode>(line, "内联表没有闭合（缺少 '}'）");
                if (Peek() == '}')
                {
                    Advance();
                    return SealInlineTable(table);
                }

                if (!TryReadKeyPath(out var path)) return null;
                SkipInlineWhitespace();
                if (Peek() != '=')
                {
                    return FailValue<JsonNode>(_line, "内联表里的键后面缺少 '='");
                }

                Advance();
                var value = ReadValue();
                if (value is null) return null;
                if (!Assign(table, path, value, _line)) return null;
                SkipBlank();
                if (Peek() == ',')
                {
                    Advance();
                    continue;
                }

                if (Peek() == '}')
                {
                    Advance();
                    return SealInlineTable(table);
                }

                return FailValue<JsonNode>(_line, "内联表的键值对之间缺少逗号");
            }
        }

        private JsonObject SealInlineTable(JsonObject table)
        {
            var pending = new Stack<JsonNode>();
            pending.Push(table);
            while (pending.TryPop(out var node))
            {
                if (node is JsonObject obj)
                {
                    _sealedTables.Add(obj);
                    foreach (var child in obj.Select(pair => pair.Value))
                        if (child is JsonObject or JsonArray) pending.Push(child);
                }
                else if (node is JsonArray array)
                {
                    foreach (var child in array)
                        if (child is JsonObject or JsonArray) pending.Push(child);
                }
            }
            return table;
        }

        private JsonNode? ReadBasicString()
        {
            var line = _line;
            Advance(); // '"'
            var builder = new StringBuilder();
            while (true)
            {
                if (AtEnd) return FailValue<JsonNode>(line, "字符串没有闭合");
                var character = Peek();
                if (character == '\n') return FailValue<JsonNode>(line, "字符串没有闭合");
                if (IsForbiddenRawControl(character))
                    return FailValue<JsonNode>(line, "字符串含不允许的原始控制字符");
                if (character == '"')
                {
                    Advance();
                    return JsonValue.Create(builder.ToString());
                }

                if (character != '\\')
                {
                    builder.Append(character);
                    Advance();
                    continue;
                }

                Advance(); // '\'
                if (AtEnd) return FailValue<JsonNode>(line, "字符串没有闭合");
                var escape = Peek();
                Advance();
                switch (escape)
                {
                    case 'b': builder.Append('\b'); break;
                    case 't': builder.Append('\t'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'r': builder.Append('\r'); break;
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case 'u': if (!AppendUnicode(builder, 4, line)) return null; break;
                    case 'U': if (!AppendUnicode(builder, 8, line)) return null; break;
                    default: return FailValue<JsonNode>(line, "不支持的转义序列");
                }
            }
        }

        private bool AppendUnicode(StringBuilder builder, int digits, int line)
        {
            // Eight hex digits can exceed Int32 and wrap negative before validation (for example \\UFFFFFFFF).
            // Keep the full code point so malformed input is an explicit line problem, never an escaped exception.
            long code = 0;
            for (var index = 0; index < digits; index++)
            {
                var character = Peek();
                if (!Uri.IsHexDigit(character))
                {
                    return Fail(line, $"\\u 转义需要 {digits} 位十六进制数字");
                }

                code = (code << 4) + Convert.ToInt32(character.ToString(), 16);
                Advance();
            }

            if (code > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF)
            {
                return Fail(line, "转义出来的 Unicode 码位无效");
            }

            builder.Append(char.ConvertFromUtf32((int)code));
            return true;
        }

        private JsonNode? ReadLiteralString()
        {
            var line = _line;
            Advance(); // '\''
            var builder = new StringBuilder();
            while (true)
            {
                if (AtEnd) return FailValue<JsonNode>(line, "字符串没有闭合");
                var character = Peek();
                if (character == '\n') return FailValue<JsonNode>(line, "字符串没有闭合");
                if (IsForbiddenRawControl(character))
                    return FailValue<JsonNode>(line, "字符串含不允许的原始控制字符");
                if (character == '\'')
                {
                    Advance();
                    return JsonValue.Create(builder.ToString());
                }

                builder.Append(character);
                Advance();
            }
        }

        /// <summary>键路径：<c>a.b</c>、<c>"quoted key".b</c> 都支持；空段直接报错。</summary>
        private bool TryReadKeyPath(out List<string> path)
        {
            path = [];
            while (true)
            {
                var line = _line;
                var segment = ReadKeySegment();
                if (segment is null) return false;
                if (segment.Length == 0) return Fail(line, "键名不能为空");
                path.Add(segment);
                SkipInlineWhitespace();
                if (Peek() != '.') return true;
                Advance();
                SkipInlineWhitespace();
            }
        }

        private string? ReadKeySegment()
        {
            var line = _line;
            if (Peek() == '"')
            {
                return ReadBasicString()?.GetValue<string>();
            }

            if (Peek() == '\'')
            {
                return ReadLiteralString()?.GetValue<string>();
            }

            var builder = new StringBuilder();
            while (IsBareKeyCharacter(Peek()))
            {
                builder.Append(Peek());
                Advance();
            }

            if (builder.Length > 0) return builder.ToString();
            return FailValue<string>(line, $"这里应该是键名，实际是 {Describe(Peek())}");
        }

        private static bool IsForbiddenRawControl(char character) =>
            character is < ' ' and not '\t' or '\u007F';

        private static bool IsBareKeyCharacter(char character) =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_';

        private bool RequireLineEnd(int line, string context)
        {
            SkipInlineWhitespace();
            if (!AtEnd && Peek() == '#') SkipComment();
            if (AtEnd || Peek() == '\n') return true;
            return Fail(line, $"{context}后面有多余内容：{Describe(Peek())}（键与值必须写在同一行）");
        }

        private void SkipInlineWhitespace()
        {
            while (!AtEnd && Peek() is ' ' or '\t' or '\r') Advance();
        }

        private void SkipComment()
        {
            while (!AtEnd && Peek() != '\n') Advance();
        }

        private void SkipBlank()
        {
            while (!AtEnd)
            {
                SkipInlineWhitespace();
                if (Peek() == '#')
                {
                    SkipComment();
                    continue;
                }

                if (Peek() == '\n')
                {
                    Advance();
                    continue;
                }

                return;
            }
        }

        private bool AtEnd => _index >= _text.Length;

        private char Peek(int offset = 0)
        {
            var position = _index + offset;
            return position < _text.Length ? _text[position] : '\0';
        }

        private void Advance()
        {
            if (AtEnd) return;
            if (_text[_index] == '\n') _line++;
            _index++;
        }

        /// <summary>报错只返回字符类别；单个源字符也可能是凭据的一部分，不能回显。</summary>
        private static string Describe(char character) => character switch
        {
            '\0' => "文本结束或空字符",
            '\n' => "换行",
            _ => "非预期字符",
        };

        private bool Fail(int line, string message)
        {
            _problems.Add($"第 {line} 行：{message}");
            return false;
        }

        private T? FailValue<T>(int line, string message)
        {
            Fail(line, message);
            return default;
        }
    }
}
