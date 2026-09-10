namespace DanmuApi.App.Services;

/// <summary>Classifies known failure conditions without returning external payloads, paths or credentials.</summary>
public static class DependencyMaintenanceDiagnostics
{
    public static string Describe(Exception error)
    {
        if (error.Message.StartsWith("核心依赖已修复并生效，但", StringComparison.Ordinal))
            return "核心依赖已修复并生效，但清理或完成通知失败；请检查诊断";
        if (error is AggregateException aggregate)
            return string.Join("；", aggregate.Flatten().InnerExceptions.Select(Describe).Distinct());
        var message = error.Message;
        if (message.Contains("恢复失败", StringComparison.Ordinal) || message.Contains("恢复未完成", StringComparison.Ordinal))
            return "修复失败且恢复未完成，已保留事务和备份；请勿启动服务，先重新执行修复恢复事务";
        if (message.Contains("签名", StringComparison.Ordinal) && (message.Contains("无效", StringComparison.Ordinal) || message.Contains("失败", StringComparison.Ordinal)))
            return "可信依赖包签名验证失败，已拒绝安装";
        if (message.Contains("未覆盖", StringComparison.Ordinal)) return "可信依赖包未包含所需依赖，需要发布方提供覆盖该依赖的 Windows 包";
        if (message.Contains("不能满足", StringComparison.Ordinal) || message.Contains("不满足", StringComparison.Ordinal))
            return "可信依赖包不满足核心要求的版本或入口，需要兼容的 Windows 依赖包";
        if (message.Contains("回滚", StringComparison.Ordinal) || message.Contains("serial", StringComparison.OrdinalIgnoreCase))
            return "依赖包版本或防回退记录校验失败，已拒绝安装";
        if (message.Contains("中断", StringComparison.Ordinal) || message.Contains("事务未完成", StringComparison.Ordinal))
            return "检测到未完成的修复事务，请停止服务后执行修复恢复，再重新检查";
        if (message.Contains("先停止", StringComparison.Ordinal) || message.Contains("Node 运行中", StringComparison.Ordinal))
            return "请先停止服务；不会自动停止正在运行的服务";
        if (message.Contains("核心正在更新", StringComparison.Ordinal)) return "核心正在更新，请等待更新结束后再维护依赖";
        if (message.Contains("自定义", StringComparison.Ordinal) || message.Contains("非受支持", StringComparison.Ordinal))
            return "自定义或非受支持来源的核心禁止在线自动修复";
        if (message.Contains("清单", StringComparison.Ordinal) || message.Contains("manifest", StringComparison.OrdinalIgnoreCase))
            return "依赖清单或核心来源记录缺失、格式错误或未通过验证";
        if (message.Contains("内置运行环境校验失败", StringComparison.Ordinal)) return "随应用提供的依赖文件已损坏，需要完整的应用文件后才能修复";
        if (message.Contains("重解析", StringComparison.Ordinal) || message.Contains("链接", StringComparison.Ordinal) || message.Contains("越", StringComparison.Ordinal))
            return "检测到链接或越界路径，已拒绝读取或替换依赖";
        if (error is HttpRequestException http)
            return http.StatusCode is { } status ? $"下载可信依赖包失败，HTTP {(int)status}" : "无法连接可信依赖包下载服务";
        if (error is UnauthorizedAccessException) return "运行依赖目录访问被拒绝，请检查目录权限";
        if (error is FileNotFoundException or DirectoryNotFoundException) return "必需的 Node、核心、随包清单或目录不存在";
        if (message.Contains("Node", StringComparison.Ordinal) || message.Contains("检查进程", StringComparison.Ordinal))
            return "Node 依赖检查失败，请确认随包 Node 版本及核心依赖声明受支持";
        if (error.InnerException is not null) return Describe(error.InnerException);
        return "依赖维护失败；具体异常链已保留，请检查文件访问、清单及维护状态";
    }
}
