using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace DanmuApi.Platform;

public enum InstanceCommand
{
    SHOW_OVERVIEW,
    SHOW_SETTINGS,
    APPLY_CORE_UPDATE,
    SHOW_APP_UPDATE,
    /// <summary>安装器在用：请求正在运行的实例安全停止服务并退出，以便替换程序文件。</summary>
    REQUEST_EXIT,
}

public sealed record AppInstanceLockResult(bool Succeeded, string Diagnostic, bool AlreadyOwned = false)
{
    public string? FailureCode { get; init; }
    public string? FailureType { get; init; }
    public int? FailureHResult { get; init; }
    public static AppInstanceLockResult Success(string diagnostic = "操作成功") => new(true, diagnostic);
}

/// <summary>
/// Owns the desktop process lock and the loopback-only wake-up channel.
/// </summary>
public sealed class AppInstanceLock : IDisposable
{
    private const int ConnectionTimeoutMilliseconds = 350;
    private const int RequestTimeoutMilliseconds = 3_000;
    private const int RetryWindowMilliseconds = 3_000;
    private const int RetryDelayMilliseconds = 100;
    private const int MaximumDiagnosticLength = 2_000;

    private readonly object _gate = new();
    private readonly AppPaths _paths;
    private readonly Action<string> _diagnosticLogger;
    private FileStream? _lockStream;
    private FileStream? _legacyLockStream;
    private TcpListener? _listener;
    private CancellationTokenSource? _serverCancellation;
    private Task? _serverTask;
    private string? _token;
    private string _lastDiagnostic = string.Empty;

    public AppInstanceLock(AppPaths paths, Action<string>? diagnosticLogger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _diagnosticLogger = diagnosticLogger ?? (message => AppendDefaultLog(_paths.TrayLogFile, message));
    }

    public bool IsAcquired
    {
        get
        {
            lock (_gate)
            {
                return _lockStream is not null;
            }
        }
    }

    public string LastDiagnostic => Volatile.Read(ref _lastDiagnostic);

    public bool TryAcquire() => TryAcquireDetailed().Succeeded;

    public bool TryAcquire(out AppInstanceLockResult result)
    {
        result = TryAcquireDetailed();
        return result.Succeeded;
    }

    public AppInstanceLockResult TryAcquireDetailed()
    {
        lock (_gate)
        {
            if (_lockStream is not null)
            {
                return AppInstanceLockResult.Success("当前实例已经持有单实例锁");
            }

            try
            {
                var directory = Path.GetDirectoryName(_paths.InstanceLockFile)
                    ?? throw new IOException($"单实例锁路径没有父目录: {_paths.InstanceLockFile}");
                Directory.CreateDirectory(directory);
                _lockStream = new FileStream(
                    _paths.InstanceLockFile,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    options: FileOptions.SequentialScan);
                _legacyLockStream = new FileStream(Path.Combine(directory, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                _legacyLockStream.Lock(0, long.MaxValue);
                return AppInstanceLockResult.Success();
            }
            catch (IOException error) when (IsSharingViolation(error) || (error.HResult & 0xffff) == 33)
            {
                _legacyLockStream?.Dispose();
                _legacyLockStream = null;
                _lockStream?.Dispose();
                _lockStream = null;
                var diagnostic = $"单实例锁已被其他实例持有: {_paths.InstanceLockFile}";
                RecordDiagnostic(diagnostic, error);
                return CommandFailure(diagnostic, error) with { AlreadyOwned = true };
            }
            catch (Exception error)
            {
                _legacyLockStream?.Dispose();
                _legacyLockStream = null;
                _lockStream?.Dispose();
                _lockStream = null;
                var diagnostic = $"获取单实例锁失败: {Describe(error)}";
                RecordDiagnostic(diagnostic, error);
                return CommandFailure(diagnostic, error);
            }
        }
    }

    public AppInstanceLockResult StartControlServer(Action<InstanceCommand> onCommand)
    {
        ArgumentNullException.ThrowIfNull(onCommand);
        lock (_gate)
        {
            if (_lockStream is null)
            {
                return Fail("未持有单实例锁，拒绝启动唤醒通道");
            }

            if (_listener is not null)
            {
                return AppInstanceLockResult.Success("本地唤醒通道已经启动");
            }

            TcpListener? listener = null;
            CancellationTokenSource? cancellation = null;
            string? token = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                token = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
                WriteEndpointAtomically(_paths.InstanceEndpointFile, listener.LocalEndpoint is IPEndPoint endpoint
                    ? endpoint.Port
                    : throw new InvalidOperationException("本地唤醒通道没有返回 TCP 端口"), token);

                cancellation = new CancellationTokenSource();
                _listener = listener;
                _serverCancellation = cancellation;
                _token = token;
                _serverTask = RunServerAsync(listener, token, onCommand, cancellation.Token);
                return AppInstanceLockResult.Success();
            }            catch (Exception error)
            {
                listener?.Stop();
                cancellation?.Dispose();
                if (token is not null)
                {
                    TryDeleteEndpointIfToken(token);
                }

                var diagnostic = $"启动本地唤醒通道失败: {Describe(error)}";
                RecordDiagnostic(diagnostic, error);
                return new AppInstanceLockResult(false, diagnostic);
            }
        }
    }

    public AppInstanceLockResult SendCommand(InstanceCommand command, CancellationToken cancellationToken = default, int? expectedProcessId = null)
    {
        try
        {
            return SendCommandAsync(command, cancellationToken, expectedProcessId).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException error)
        {
            var diagnostic = "发送本地唤醒命令已取消";
            RecordDiagnostic(diagnostic, error);
            return CommandFailure(diagnostic, error);
        }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or UnauthorizedAccessException or FormatException)
        {
            var diagnostic = $"发送本地唤醒命令异常: {Describe(error)}";
            RecordDiagnostic(diagnostic, error);
            return CommandFailure(diagnostic, error);
        }
    }

    public async Task<AppInstanceLockResult> SendCommandAsync(
        InstanceCommand command,
        CancellationToken cancellationToken = default,
        int? expectedProcessId = null)
    {
        if (expectedProcessId is <= 0)
            return Fail("本地退出请求的目标进程 ID 无效", "endpointowner");
        if (!Enum.IsDefined(command))
        {
            return Fail($"未知本地唤醒命令: {command}");
        }

        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * RetryWindowMilliseconds / 1_000;
        Exception? lastError = null;
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                var diagnostic = "发送本地唤醒命令已取消";
                RecordDiagnostic(diagnostic);
                return new AppInstanceLockResult(false, diagnostic);
            }

            var remaining = RemainingMilliseconds(deadline);
            if (remaining <= 0)
            {
                break;
            }

            var requestStarted = false;
            try
            {
                var endpoint = ReadEndpoint(_paths.InstanceEndpointFile);
                using var client = new TcpClient(AddressFamily.InterNetwork);
                using (var connectCancellation = CreateTimeoutCancellation(cancellationToken, Math.Min(ConnectionTimeoutMilliseconds, remaining)))
                {
                    try
                    {
                        await client.ConnectAsync(IPAddress.Loopback, endpoint.Port, connectCancellation.Token).ConfigureAwait(false);
                        // .NET 8 can complete ConnectAsync successfully while its cancellation
                        // callback closes the socket. Cancellation must win before GetStream.
                        connectCancellation.Token.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException error)
                    {
                        var diagnostic = cancellationToken.IsCancellationRequested
                            ? "发送本地唤醒命令已取消"
                            : "本地唤醒通道连接超时";
                        RecordDiagnostic(diagnostic, error);
                        return CommandFailure(diagnostic, error);
                    }
                }
                if (expectedProcessId is { } ownerPid)
                {
                    try
                    {
                        if (!WindowsTcpPeerIdentity.MatchesConnectedServer(client, ownerPid))
                            return Fail("本地退出通道不属于已验证的目标进程，未发送退出请求", "endpointowner");
                    }
                    catch (IOException error)
                    {
                        RecordDiagnostic($"验证本地退出通道所属进程失败: {Describe(error)}", error);
                        return new AppInstanceLockResult(false, LastDiagnostic)
                        {
                            FailureCode = "endpointowner",
                            FailureType = error.InnerException is System.ComponentModel.Win32Exception ? nameof(System.ComponentModel.Win32Exception) : error.GetType().Name,
                            FailureHResult = error.InnerException is System.ComponentModel.Win32Exception native && native.NativeErrorCode is > 0 and <= 65535
                                ? unchecked((int)(0x80070000u | (uint)native.NativeErrorCode)) : error.HResult
                        };
                    }
                }
                using var stream = client.GetStream();
                using var requestCancellation = CreateTimeoutCancellation(cancellationToken, Math.Min(RequestTimeoutMilliseconds, RemainingMilliseconds(deadline)));
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
                {
                    NewLine = "\n",
                    AutoFlush = true,
                };
                // Once writing starts the command may have reached its owner; never dispatch it twice.
                requestStarted = true;
                await writer.WriteLineAsync($"{endpoint.Token}\t{command}").WaitAsync(requestCancellation.Token).ConfigureAwait(false);
                using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                var response = await reader.ReadLineAsync(requestCancellation.Token).ConfigureAwait(false);
                if (string.Equals(response, "OK", StringComparison.Ordinal))
                {
                    return AppInstanceLockResult.Success();
                }

                if (response?.StartsWith("ERROR ", StringComparison.Ordinal) == true)
                {
                    var diagnostic = expectedProcessId is not null
                        ? "已验证的运行实例拒绝退出请求；该版本可能不支持安全退出，或当前操作不允许退出"
                        : $"本地唤醒通道拒绝请求: {LimitDiagnostic(response[6..])}";
                    RecordDiagnostic(diagnostic);
                    return new AppInstanceLockResult(false, diagnostic) { FailureCode = "commandrejected" };
                }

                throw new IOException(response is null
                    ? "本地唤醒通道未返回结果"
                    : expectedProcessId is not null ? "已验证的运行实例返回了未知退出响应"
                    : $"本地唤醒通道返回未知结果: {LimitDiagnostic(response)}");
            }
            catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
            {
                var diagnostic = "发送本地唤醒命令已取消";
                RecordDiagnostic(diagnostic, error);
                return CommandFailure(diagnostic, error);
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                var diagnostic = "本地唤醒通道请求超时";
                RecordDiagnostic(diagnostic, error);
                return CommandFailure(diagnostic, error);
            }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or UnauthorizedAccessException or FormatException)
            {
                if (requestStarted)
                {
                    var diagnostic = $"发送本地唤醒命令失败: {Describe(error)}";
                    RecordDiagnostic(diagnostic, error);
                    return CommandFailure(diagnostic, error);
                }

                lastError = error;
            }

            remaining = RemainingMilliseconds(deadline);
            if (remaining <= 0)
            {
                break;
            }

            try
            {
                await Task.Delay(Math.Min(RetryDelayMilliseconds, remaining), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException error)
            {
                var diagnostic = "发送本地唤醒命令已取消";
                RecordDiagnostic(diagnostic, error);
                return CommandFailure(diagnostic, error);
            }
        }

        var failure = $"无法在 {RetryWindowMilliseconds}ms 内唤醒已运行的弹幕 API 实例: {Describe(lastError)}";
        RecordDiagnostic(failure, lastError);
        return lastError is null ? new AppInstanceLockResult(false, failure) : CommandFailure(failure, lastError);
    }

    public AppInstanceLockResult Release()
    {
        Task? serverTask;
        CancellationTokenSource? cancellation;
        TcpListener? listener;
        FileStream? lockStream;
        FileStream? legacyLockStream;
        string? token;

        lock (_gate)
        {
            serverTask = _serverTask;
            cancellation = _serverCancellation;
            listener = _listener;
            lockStream = _lockStream;
            legacyLockStream = _legacyLockStream;
            _legacyLockStream = null;
            token = _token;
            _serverTask = null;
            _serverCancellation = null;
            _listener = null;
            _lockStream = null;
            _token = null;
        }

        var succeeded = true;
        var releaseDiagnostic = "操作成功";
        try
        {
            cancellation?.Cancel();
            listener?.Stop();
            if (serverTask is not null)
            {
                serverTask.GetAwaiter().GetResult();
            }
        }
        catch (Exception error)
        {
            succeeded = false;
            releaseDiagnostic = $"停止本地唤醒通道失败: {Describe(error)}";
            RecordDiagnostic(releaseDiagnostic, error);
        }
        finally
        {
            cancellation?.Dispose();
        }

        if (token is not null)
        {
            try
            {
                if (TryReadEndpointToken(_paths.InstanceEndpointFile, out var endpointToken) &&
                    string.Equals(endpointToken, token, StringComparison.Ordinal))
                {
                    File.Delete(_paths.InstanceEndpointFile);
                }
                else if (endpointToken is not null)
                {
                    releaseDiagnostic = "端点令牌已变化，保留新的 instance.endpoint 文件";
                    RecordDiagnostic(releaseDiagnostic);
                }
            }
            catch (FileNotFoundException)
            {
                // The endpoint was already removed by its owner.
            }
            catch (DirectoryNotFoundException)
            {
                // The settings directory was removed during shutdown.
            }
            catch (Exception error)
            {
                succeeded = false;
                releaseDiagnostic = $"删除本地唤醒端点失败: {Describe(error)}";
                RecordDiagnostic(releaseDiagnostic, error);
            }
        }

        try
        {
            try { legacyLockStream?.Dispose(); }
            finally { lockStream?.Dispose(); }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            succeeded = false;
            releaseDiagnostic = $"释放单实例锁失败: {Describe(error)}";
            RecordDiagnostic(releaseDiagnostic, error);
        }

        return succeeded
            ? AppInstanceLockResult.Success()
            : new AppInstanceLockResult(false, LastDiagnostic);
    }

    public void Dispose() => Release();

    private async Task RunServerAsync(
        TcpListener listener,
        string expectedToken,
        Action<InstanceCommand> onCommand,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error) when (error is SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    RecordDiagnostic($"本地唤醒通道接受连接失败: {Describe(error)}", error);
                    break;
                }

                await HandleClientAsync(client, expectedToken, onCommand, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                RecordDiagnostic($"本地唤醒通道停止: {Describe(error)}", error);
            }
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        string expectedToken,
        Action<InstanceCommand> onCommand,
        CancellationToken serverCancellation)
    {
        using (client)
        using (var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation))
        {
            requestCancellation.CancelAfter(RequestTimeoutMilliseconds);
            NetworkStream? networkStream = null;
            try
            {
                networkStream = client.GetStream();
                using var reader = new StreamReader(networkStream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                var request = await reader.ReadLineAsync(requestCancellation.Token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(request))
                {
                    throw new IOException("本地唤醒请求为空");
                }

                var separator = request.IndexOf('\t');
                if (separator <= 0 || separator == request.Length - 1 || request.IndexOf('\t', separator + 1) >= 0)
                {
                    throw new IOException("本地唤醒请求格式无效");
                }

                var suppliedToken = request[..separator];
                var suppliedTokenBytes = Encoding.UTF8.GetBytes(suppliedToken);
                var expectedTokenBytes = Encoding.UTF8.GetBytes(expectedToken);
                if (suppliedTokenBytes.Length != expectedTokenBytes.Length ||
                    !CryptographicOperations.FixedTimeEquals(suppliedTokenBytes, expectedTokenBytes))
                {
                    throw new IOException("本地唤醒令牌不匹配");
                }

                var commandName = request[(separator + 1)..];
                if (!Enum.TryParse<InstanceCommand>(commandName, ignoreCase: false, out var command) || !Enum.IsDefined(command))
                {
                    throw new IOException($"未知本地唤醒命令: {LimitDiagnostic(commandName)}");
                }

                onCommand(command);
                using var writer = new StreamWriter(networkStream, new UTF8Encoding(false), leaveOpen: true)
                {
                    NewLine = "\n",
                    AutoFlush = true,
                };
                await writer.WriteLineAsync("OK").WaitAsync(requestCancellation.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                var reason = error is OperationCanceledException
                    ? "本地唤醒请求超时"
                    : Describe(error);
                try
                {
                    using var writer = new StreamWriter(networkStream ?? client.GetStream(), new UTF8Encoding(false), leaveOpen: true)
                    {
                        NewLine = "\n",
                        AutoFlush = true,
                    };
                    await writer.WriteLineAsync($"ERROR {LimitDiagnostic(reason)}").WaitAsync(requestCancellation.Token).ConfigureAwait(false);
                }
                catch (Exception responseError) when (responseError is IOException or SocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
                {
                    RecordDiagnostic($"本地唤醒错误响应写入失败: {Describe(responseError)}", responseError);
                }

                RecordDiagnostic($"本地唤醒请求失败: {reason}", error);
            }
        }
    }

    private static AppInstanceLockResult CommandFailure(string diagnostic, Exception error) => new(false, diagnostic)
    {
        FailureType = error.GetType().Name,
        FailureHResult = error is System.ComponentModel.Win32Exception native && native.NativeErrorCode is > 0 and <= 65535
            ? unchecked((int)(0x80070000u | (uint)native.NativeErrorCode)) : error.HResult
    };

    private AppInstanceLockResult Fail(string diagnostic, string? failureCode = null)
    {
        RecordDiagnostic(diagnostic);
        return new AppInstanceLockResult(false, diagnostic) { FailureCode = failureCode };
    }

    private void RecordDiagnostic(string diagnostic, Exception? error = null)
    {
        var message = error is null ? diagnostic : $"{diagnostic}; exception={error.GetType().Name}: {error.Message}";
        message = LimitDiagnostic(message);
        Volatile.Write(ref _lastDiagnostic, message);
        try
        {
            _diagnosticLogger(message);
        }
        catch (Exception loggerError)
        {
            var loggerDiagnostic = LimitDiagnostic($"{message}; 诊断日志写入失败: {Describe(loggerError)}");
            Volatile.Write(ref _lastDiagnostic, loggerDiagnostic);
        }
    }

    private static void WriteEndpointAtomically(string endpointFile, int port, string token)
    {
        var directory = Path.GetDirectoryName(endpointFile)
            ?? throw new IOException($"本地唤醒端点路径没有父目录: {endpointFile}");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(endpointFile)}.tmp-{Guid.NewGuid():N}");
        try
        {
            // exe 记录「当前正在运行的是哪个可执行文件」：安装器要请这个实例退出时，
            // 靠它拿到准确路径（比注册表更可靠——便携副本不写卸载键，协议注册也要等首次通知）。
            var executable = Environment.ProcessPath;
            var content = $"port={port.ToString(CultureInfo.InvariantCulture)}\ntoken={token}\n"
                + (string.IsNullOrWhiteSpace(executable) ? string.Empty : $"exe={executable}\n");
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, endpointFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// 读取「当前运行实例的可执行文件路径」，供安装器在替换文件前请它退出。
    /// 读不到或内容不合法时返回 false，不猜路径——安装器会退回注册表来源或让用户手动退出。
    /// </summary>
    public static bool TryReadRunningExecutable(string endpointFile, out string? executable)
    {
        executable = null;
        try
        {
            if (!File.Exists(endpointFile))
            {
                return false;
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var rawLine in File.ReadAllLines(endpointFile, new UTF8Encoding(false, true)))
            {
                var line = rawLine.Trim();
                var separator = line.IndexOf('=');
                if (separator <= 0 || separator == line.Length - 1)
                {
                    continue;
                }

                values[line[..separator]] = line[(separator + 1)..];
            }

            if (!values.TryGetValue("exe", out var path) || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var full = Path.GetFullPath(path);
            executable = full;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or ArgumentException or NotSupportedException)
        {
            executable = null;
            return false;
        }
    }

    private static InstanceEndpoint ReadEndpoint(string endpointFile)
    {
        if (!File.Exists(endpointFile))
        {
            throw new FileNotFoundException($"本地唤醒端点不存在: {endpointFile}", endpointFile);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in File.ReadAllLines(endpointFile, new UTF8Encoding(false, true)))
        {
            var line = rawLine.Trim();
            var separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
            {
                throw new FormatException("本地唤醒端点格式无效");
            }

            var key = line[..separator];
            var value = line[(separator + 1)..];
            if (!values.TryAdd(key, value))
            {
                throw new FormatException($"本地唤醒端点包含重复字段: {key}");
            }
        }

        if (!values.TryGetValue("port", out var portText) ||
            !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65_535)
        {
            throw new FormatException("本地唤醒端点端口无效");
        }

        if (!values.TryGetValue("token", out var token) || string.IsNullOrWhiteSpace(token))
        {
            throw new FormatException("本地唤醒端点令牌缺失");
        }

        return new InstanceEndpoint(port, token);
    }

    private static bool TryReadEndpointToken(string endpointFile, out string? token)
    {
        token = null;
        if (!File.Exists(endpointFile))
        {
            return false;
        }

        token = ReadEndpoint(endpointFile).Token;
        return true;
    }

    private void TryDeleteEndpointIfToken(string expectedToken)
    {
        try
        {
            if (TryReadEndpointToken(_paths.InstanceEndpointFile, out var endpointToken) &&
                string.Equals(endpointToken, expectedToken, StringComparison.Ordinal))
            {
                File.Delete(_paths.InstanceEndpointFile);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
        {
            RecordDiagnostic($"清理失败的本地唤醒端点失败: {Describe(error)}", error);
        }
    }

    private static CancellationTokenSource CreateTimeoutCancellation(CancellationToken cancellationToken, int milliseconds)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(Math.Max(1, milliseconds));
        return source;
    }

    private static int RemainingMilliseconds(long deadline)
    {
        var remainingTicks = deadline - Stopwatch.GetTimestamp();
        if (remainingTicks <= 0)
        {
            return 0;
        }

        var milliseconds = remainingTicks * 1_000 / Stopwatch.Frequency;
        return milliseconds > int.MaxValue ? int.MaxValue : Math.Max(1, (int)milliseconds);
    }

    private static bool IsSharingViolation(IOException error)
    {
        var code = error.HResult & 0xFFFF;
        return code is 32 or 33;
    }

    private static string Describe(Exception? error) => error is null
        ? "未知错误"
        : string.IsNullOrWhiteSpace(error.Message)
            ? error.GetType().Name
            : LimitDiagnostic(error.Message);

    private static string LimitDiagnostic(string value) => value.Length <= MaximumDiagnosticLength
        ? value.Replace('\r', ' ').Replace('\n', ' ')
        : value[..MaximumDiagnosticLength].Replace('\r', ' ').Replace('\n', ' ') + "...";

    private static void AppendDefaultLog(string path, string message)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new IOException($"宿主日志路径没有父目录: {path}");
        Directory.CreateDirectory(directory);
        File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}  本地唤醒通道：{message}{Environment.NewLine}", new UTF8Encoding(false));
    }

    private sealed record InstanceEndpoint(int Port, string Token);
}
