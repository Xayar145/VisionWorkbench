using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace VisionWorkbench.Communication;

public sealed class TcpInspectionServer : IAsyncDisposable
{
    public const int MaximumLineBytes = 16 * 1024;
    public const int MaximumClients = 8;
    private readonly ConcurrentDictionary<TcpClient, Task> clients = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private TcpListener? listener;
    private CancellationTokenSource? cancellation;
    private Task? acceptTask;
    public event Action<string>? Logged;
    public bool IsRunning => listener is not null;
    public int BoundPort { get; private set; }

    public Task StartAsync(string address, int port, Func<DeviceRequest, CancellationToken, Task<DeviceReply>> handler)
    {
        if (IsRunning) throw new InvalidOperationException("TCP 服务已经启动。");
        var active = new TcpListener(IPAddress.Parse(address), port);
        active.Start();
        cancellation = new();
        listener = active;
        BoundPort = ((IPEndPoint)active.LocalEndpoint).Port;
        acceptTask = AcceptAsync(active, handler, cancellation.Token);
        Log($"TCP 监听 {address}:{BoundPort}");
        return Task.CompletedTask;
    }

    private async Task AcceptAsync(TcpListener active, Func<DeviceRequest, CancellationToken, Task<DeviceReply>> handler, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await active.AcceptTcpClientAsync(token);
                if (clients.Count >= MaximumClients) { client.Dispose(); continue; }
                // 注册后再启动，避免快速断开的连接在字典内遗留。
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                clients[client] = completion.Task;
                _ = ServeAsync(client, handler, token, completion);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (SocketException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Log($"TCP 监听失败：{ex.Message}"); }
    }

    private async Task ServeAsync(TcpClient client, Func<DeviceRequest, CancellationToken, Task<DeviceReply>> handler,
        CancellationToken token, TaskCompletionSource completion)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                byte[] buffer = new byte[2048];
                using var line = new MemoryStream();
                while (!token.IsCancellationRequested)
                {
                    int count = await stream.ReadAsync(buffer, token);
                    if (count == 0) break;
                    for (int i = 0; i < count; i++)
                    {
                        if (buffer[i] != '\n')
                        {
                            if (line.Length >= MaximumLineBytes)
                            {
                                await ReplyAsync(stream, new("", false, "ERROR", Error: "请求行超过 16 KiB。"), token);
                                return;
                            }
                            line.WriteByte(buffer[i]);
                            continue;
                        }
                        DeviceReply reply;
                        string requestId = "";
                        try
                        {
                            var request = JsonSerializer.Deserialize<DeviceRequest>(line.GetBuffer().AsSpan(0, (int)line.Length), JsonOptions);
                            if (request is null || string.IsNullOrWhiteSpace(request.Command)) throw new JsonException("缺少 command。");
                            requestId = request.RequestId ?? "";
                            // 停止时取消并等待业务真正结束，避免拍照检测成为遗留后台任务。
                            reply = await handler(request, token);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception ex) { reply = new(requestId, false, "ERROR", Error: ex.Message); }
                        line.SetLength(0);
                        line.Position = 0;
                        await ReplyAsync(stream, reply, token);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) Log($"TCP 连接退出：{ex.Message}"); }
        finally
        {
            client.Dispose();
            clients.TryRemove(client, out _);
            completion.TrySetResult();
        }
    }

    private static async Task ReplyAsync(NetworkStream stream, DeviceReply reply, CancellationToken token)
        => await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply, JsonOptions) + "\n"), token);

    public async Task StopAsync()
    {
        if (listener is null) return;
        cancellation!.Cancel();
        listener.Stop();
        listener = null;
        BoundPort = 0;
        foreach (var client in clients.Keys) client.Dispose();
        if (acceptTask is not null) await acceptTask;
        await Task.WhenAll(clients.Values);
        cancellation.Dispose();
        cancellation = null;
        Log("TCP 服务已停止");
    }

    private void Log(string text) { try { Logged?.Invoke(text); } catch { /* 日志订阅者不影响通信。 */ } }
    public async ValueTask DisposeAsync() => await StopAsync();
}
