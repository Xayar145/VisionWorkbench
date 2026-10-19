using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using VisionWorkbench.Services;

namespace VisionWorkbench.AI;

/// <summary>仅本机访问的 HTTP 工具接口；不是 MCP 服务。</summary>
public sealed class HarnessServer : IDisposable
{
    private readonly VisionTools tools;
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly string sessionKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private Task? listenTask;
    public string BaseUrl { get; private set; } = "";
    public string SessionFilePath { get; } = Path.Combine(SettingsStore.DirectoryPath, $"harness-{Environment.ProcessId}.json");

    public HarnessServer(WorkbenchController controller) : this(new VisionTools(controller)) { }
    public HarnessServer(VisionTools tools) => this.tools = tools;

    public void Start()
    {
        if (listenTask != null) return;
        // 由操作系统分配空闲端口；HttpListener 仅绑定 IPv4 loopback。
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)probe.LocalEndpoint).Port}/";
        }
        listener.Prefixes.Add(BaseUrl);
        listener.Start();
        Directory.CreateDirectory(SettingsStore.DirectoryPath);
        File.WriteAllText(SessionFilePath, JsonSerializer.Serialize(new { baseUrl = BaseUrl, token = sessionKey,
            processId = Environment.ProcessId }, new JsonSerializerOptions { WriteIndented = true }));
        listenTask = ListenAsync();
    }

    private async Task ListenAsync()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(shutdown.Token);
                await HandleAsync(context);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (HttpListenerException) when (shutdown.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (shutdown.IsCancellationRequested) { }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        try
        {
            var supplied = Encoding.UTF8.GetBytes(request.Headers["Authorization"] ?? "");
            var expected = Encoding.UTF8.GetBytes("Bearer " + sessionKey);
            if (request.RemoteEndPoint?.Address.Equals(IPAddress.Loopback) != true ||
                !CryptographicOperations.FixedTimeEquals(supplied, expected))
            {
                await Reply(context, 401, new { error = "需要本次会话的 Bearer 密钥" });
                return;
            }
            var path = request.Url?.AbsolutePath.TrimEnd('/');
            if (request.HttpMethod == "GET" && path == "/tools")
            {
                await Reply(context, 200, tools.Functions.OfType<AIFunction>().Select(f =>
                    new { name = f.Name, description = f.Description, parameters = f.JsonSchema }));
            }
            else if (request.HttpMethod == "GET" && path == "/state")
            {
                var state = await tools.GetWorkspaceState(shutdown.Token);
                await ReplyJson(context, 200, state);
            }
            else if (request.HttpMethod == "POST" && path == "/tools/call")
            {
                if (request.ContentLength64 > 32768)
                {
                    await Reply(context, 413, new { error = "请求超过 32 KB" });
                    return;
                }
                using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                bodyTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var buffer = new MemoryStream();
                var bytes = new byte[4096];
                int read;
                while ((read = await request.InputStream.ReadAsync(bytes, bodyTimeout.Token)) > 0)
                {
                    if (buffer.Length + read > 32768)
                    {
                        await Reply(context, 413, new { error = "请求超过 32 KB" });
                        return;
                    }
                    buffer.Write(bytes, 0, read);
                }
                using var document = JsonDocument.Parse(buffer.ToArray());
                var root = document.RootElement;
                var name = root.GetProperty("name").GetString() ?? "";
                var arguments = root.TryGetProperty("arguments", out var suppliedArguments)
                    ? suppliedArguments : JsonSerializer.SerializeToElement(new { });
                var result = await tools.InvokeAsync(name, arguments, shutdown.Token);
                await ReplyJson(context, 200, result);
            }
            else await Reply(context, 404, new { error = "未知接口" });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try { await Reply(context, ex is OperationCanceledException ? 408 : 400, new { error = ex.Message }); }
            catch (Exception writeError) when (writeError is HttpListenerException or ObjectDisposedException or IOException) { }
        }
        finally { context.Response.Close(); }
    }

    private static Task Reply(HttpListenerContext context, int status, object value) =>
        ReplyJson(context, status, JsonSerializer.Serialize(value));

    private static async Task ReplyJson(HttpListenerContext context, int status, string json)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }

    public void Dispose()
    {
        shutdown.Cancel();
        listener.Close();
        if (File.Exists(SessionFilePath)) File.Delete(SessionFilePath);
    }
}
