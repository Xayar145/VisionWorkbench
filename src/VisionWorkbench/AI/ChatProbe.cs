using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VisionWorkbench.Services;

namespace VisionWorkbench.AI;

/// <summary>使用本机假模型响应验证工具调用协议，不连接真实模型、不保存设置。</summary>
public static class ChatProbe
{
    public static async Task<string> RunAsync(WorkbenchController controller, CancellationToken token = default)
    {
        var threshold = controller.Document.Steps.First(s => s.Kind == "halcon.threshold");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var listener = new HttpListener();
        string endpoint;
        using (var port = new TcpListener(IPAddress.Loopback, 0))
        {
            port.Start();
            endpoint = $"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}/";
        }
        listener.Prefixes.Add(endpoint);
        listener.Start();
        string? observedRunId = null;
        var serve = Task.Run(async () =>
        {
            for (int index = 0; index < 3; index++)
            {
                var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                try
                {
                    if (context.Request.HttpMethod != "POST" || context.Request.Url?.AbsolutePath != "/v1/chat/completions")
                        throw new InvalidOperationException("探针收到了非预期的模型接口请求");
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    using var request = JsonDocument.Parse(await reader.ReadToEndAsync(timeout.Token));
                    var root = request.RootElement;
                    if (index == 0)
                    {
                        var names = root.GetProperty("tools").EnumerateArray()
                            .Select(t => t.GetProperty("function").GetProperty("name").GetString()).ToArray();
                        if (!names.Contains("SetToolParameter") || !names.Contains("RunPipeline"))
                            throw new InvalidOperationException("工具定义没有发送到模型接口");
                    }
                    else if (index == 1)
                    {
                        var result = ReadToolResult(root, "probe_set");
                        if (result.GetProperty("Value").GetDouble() != 120)
                            throw new InvalidOperationException("模型接口未收到实际的参数修改结果");
                    }
                    else
                    {
                        var result = ReadToolResult(root, "probe_run");
                        if (!result.GetProperty("Success").GetBoolean() || !result.GetProperty("Accepted").GetBoolean())
                            throw new InvalidOperationException("模型接口收到的运行结果不是成功且 OK");
                        observedRunId = result.GetProperty("RunId").GetString();
                    }

                    object message = index switch
                    {
                        0 => ToolMessage("probe_set", "SetToolParameter", new { stepId = threshold.Id, key = "min", value = 120 }),
                        1 => ToolMessage("probe_run", "RunPipeline", new { }),
                        _ => new { role = "assistant", content = "本机协议探针：参数已修改，流程执行成功。" }
                    };
                    string json = JsonSerializer.Serialize(new
                    {
                        id = "chatcmpl-probe-" + index, @object = "chat.completion",
                        created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), model = "local-probe",
                        choices = new[] { new { index = 0, message, finish_reason = index < 2 ? "tool_calls" : "stop" } }
                    });
                    context.Response.ContentType = "application/json";
                    var responseBytes = Encoding.UTF8.GetBytes(json);
                    context.Response.ContentLength64 = responseBytes.Length;
                    await context.Response.OutputStream.WriteAsync(responseBytes, timeout.Token);
                }
                finally { context.Response.Close(); }
            }
        }, timeout.Token);
        try
        {
            var chat = new ChatService(controller);
            var replyTask = chat.SendAsync("把阈值下限改为 120，运行一次并报告结果。", new AiSettings
            {
                Endpoint = endpoint + "v1", Model = "local-probe", ApiKey = "local-probe-not-a-secret"
            }, timeout.Token);
            if (await Task.WhenAny(replyTask, serve) == serve) await serve;
            var reply = await replyTask;
            await serve.WaitAsync(timeout.Token);
            var report = controller.LastReport;
            if (threshold.Get("min") != 120 || report == null || !report.Success || !report.Accepted ||
                report.RunId != observedRunId || !reply.Contains("本机协议探针"))
                throw new InvalidOperationException("模型返回与控制器实际执行状态不一致");
            return "本机协议探针通过：3 次 Chat Completions 请求，参数 min=120，真实流程 OK，工具结果回传与运行编号一致。";
        }
        finally
        {
            timeout.Cancel();
            listener.Close();
            try { await serve; }
            catch when (timeout.IsCancellationRequested) { }
        }
    }

    private static object ToolMessage(string id, string name, object arguments) => new
    {
        role = "assistant", content = (string?)null,
        tool_calls = new[] { new { id, type = "function", function = new { name, arguments = JsonSerializer.Serialize(arguments) } } }
    };

    private static JsonElement ReadToolResult(JsonElement request, string callId)
    {
        var message = request.GetProperty("messages").EnumerateArray().LastOrDefault(m =>
            m.GetProperty("role").GetString() == "tool" &&
            m.TryGetProperty("tool_call_id", out var id) && id.GetString() == callId);
        if (message.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("模型接口没有收到对应的工具结果：" + callId);
        using var content = JsonDocument.Parse(message.GetProperty("content").GetString()!);
        // 字符串类型的 AIFunction 返回值在不同适配器中可能再次 JSON 编码。
        if (content.RootElement.ValueKind == JsonValueKind.String)
        {
            using var nested = JsonDocument.Parse(content.RootElement.GetString()!);
            return nested.RootElement.Clone();
        }
        return content.RootElement.Clone();
    }
}
