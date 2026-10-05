namespace DanmuApi.Tests;

/// <summary>服务商样本的结构与注释保留，地址和账号标识使用虚构测试值。</summary>
internal static class FrpProviderSample
{
    internal const string ServerAddress = "frp.example.com";
    internal const string User = "panel-user";
    internal const string ProxyName = "provider-api";

    internal const string ClientToml = """
        serverAddr = "frp.example.com"
        serverPort = 1210
        user ="panel-user"
        loginFailExit = false
        #支持 tcp, kcp, quic, websocket 和 wss 协议, 默认传输协议为tcp
        #如果被限速可以尝试切换websocket协议（去掉下面一行的 '#' 号）
        #transport.protocol = "websocket"

        #上面是认证信息，不要修改
        #下面的是一个完整连接的结构，其他协议请自行搜索frpc配置
        #请先创建隧道，系统将自动生成配置文件

        [[proxies]]
        type = "tcp"
        #每个连接一个名称，不能修改
        name="provider-api"
        localIP = "127.0.0.1"
        #请填写需要远程访问的本地端口
        localPort = 9321
        #每个连接一个远程端口，访问地址 serverAddr:remotePort
        remotePort = 9321
        #启动报错时尝试删除下面两行
        transport.useEncryption = true
        transport.useCompression = true
        """;
}
