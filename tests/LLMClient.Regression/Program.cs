using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using GiantessLLMMod.Core;
using GiantessLLMMod.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class Program
{
    private static int Main()
    {
        var tests = new Dictionary<string, Action>
        {
            ["GPT-6 Luna request matches the rejected-parameter contract"] = ModernRequest,
            ["Local OpenAI-compatible endpoint retains legacy parameters"] = LocalRequest,
            ["Prompt error releases busy state"] = PromptFailure,
            ["Odd history limit retains complete turns"] = OddHistory,
            ["Negative history limit disables history without failing"] = NegativeHistory,
            ["Callbacks do not hold the producer queue lock"] = CallbackLock,
            ["Clearing history during a request stays cleared"] = ClearDuringRequest,
            ["Explicit token parameter overrides Auto"] = TokenOverrides,
            ["Reasoning requests omit temperature"] = ReasoningParameters,
            ["Empty tool lists are omitted"] = EmptyTools,
            ["Ollama generate keeps its native options"] = OllamaRequest,
            ["HTTP 400 detail preserved without automatic retries"] = HttpError,
            ["Tool results run on main thread and preserve call IDs"] = ToolRoundTrip,
            ["Expired queued tools are skipped"] = ExpiredTool,
            ["Configuration is frozen across tool rounds"] = FrozenConfig,
            ["Invalid response is corrected on the next round"] = CorrectiveRetry,
            ["Invalid responses stop after four attempts"] = RetryLimit,
            ["Token exhaustion is diagnosed without JSON repair retries"] = TokenExhaustion,
            ["Callbacks added during dispatch wait for next frame"] = BoundedCallbacks,
            ["Null state releases busy and permits the next request"] = NullState,
            ["Reduced history limits apply to the next outgoing request"] = ReducedHistory,
            ["Dialogue length follows configuration"] = DialogueLimit
        };
        int failed = 0;
        foreach (var test in tests)
        {
            try { test.Value(); Console.WriteLine("PASS " + test.Key); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + test.Key + ": " + ex.Message); }
        }
        Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed");
        return failed == 0 ? 0 : 1;
    }

    private static ConfigManager Config(MockServer server, string model = "local-model")
    {
        var config = new ConfigManager();
        config.ApiBaseUrl.Value = server.Url;
        config.ModelName.Value = model;
        return config;
    }

    private static LLMClient Client(ConfigManager config) => new LLMClient(new ManualLogSource(), config);
    private static GameStateSnapshot State() => new GameStateSnapshot { PlayerInput = "hello" };
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Pump(LLMClient client, Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.ElapsedMilliseconds < 6000)
        {
            client.ProcessMainThreadCallbacks();
            Thread.Sleep(2);
        }
        Assert(done(), "Callback timed out");
    }

    private static void Send(LLMClient client, Action beforePump = null, List<ToolDefinition> tools = null)
    {
        bool complete = false;
        string error = null;
        int caller = Thread.CurrentThread.ManagedThreadId;
        client.SendRequest(State(), _ =>
        {
            Assert(Thread.CurrentThread.ManagedThreadId == caller, "Success ran off the main thread");
            complete = true;
        }, value => { error = value; complete = true; }, tools);
        beforePump?.Invoke();
        Pump(client, () => complete);
        Assert(error == null, error);
        Assert(!client.IsBusy, "Client stayed busy after completion");
    }

    private static void ModernRequest()
    {
        using (var server = new MockServer(body =>
        {
            if (body["max_tokens"] != null)
                return new Reply(400, "{\"error\":{\"message\":\"Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.\",\"param\":\"max_tokens\",\"code\":\"unsupported_parameter\"}}");
            Assert((int?)body["max_completion_tokens"] == 700, "Missing completion budget");
            Assert((string)body["reasoning_effort"] == "none", "Chat tools need reasoning_effort=none for Luna");
            return Reply.Success();
        }))
        {
            Send(Client(Config(server, "gpt-6-luna")), tools: Tools());
        }
    }

    private static List<ToolDefinition> Tools() => new List<ToolDefinition>
    {
        new ToolDefinition { Function = new FunctionDefinition { Name = "test", Parameters = new { type = "object" } } }
    };

    private static void LocalRequest()
    {
        using (var server = new MockServer(body =>
        {
            Assert((int?)body["max_tokens"] == 700, "Legacy budget changed");
            Assert(body["max_completion_tokens"] == null, "Sent both budgets");
            Assert(body["temperature"] != null, "Local temperature omitted");
            return Reply.Success();
        })) Send(Client(Config(server)));
    }

    private static void PromptFailure()
    {
        var config = new ConfigManager { PromptError = new FileNotFoundException("fixture prompt missing") };
        var client = Client(config);
        bool errorCalled = false;
        client.SendRequest(State(), _ => { }, _ => errorCalled = true);
        Assert(errorCalled, "Missing prompt error callback");
        Assert(!client.IsBusy, "Prompt failure leaves busy=true permanently");
    }

    private static void OddHistory()
    {
        using (var server = new MockServer(_ => Reply.Success()))
        {
            var config = Config(server);
            config.MaxConversationHistory.Value = 3;
            var client = Client(config);
            Send(client); Send(client);
            Assert(client.HistoryCount == 2, "History starts with an orphan assistant message");
        }
    }

    private static void NegativeHistory()
    {
        using (var server = new MockServer(_ => Reply.Success()))
        {
            var config = Config(server);
            config.MaxConversationHistory.Value = -1;
            var client = Client(config);
            Send(client);
            Assert(client.HistoryCount == 0, "History should be disabled");
        }
    }

    private static void CallbackLock()
    {
        var client = Client(new ConfigManager());
        var enqueue = typeof(LLMClient).GetMethod("EnqueueMainThread", BindingFlags.NonPublic | BindingFlags.Instance);
        Task producer = null;
        bool lockFree = false;
        enqueue.Invoke(client, new object[] { (Action)(() =>
        {
            producer = Task.Run(() => enqueue.Invoke(client, new object[] { (Action)(() => { }) }));
            lockFree = producer.Wait(500);
        }) });
        client.ProcessMainThreadCallbacks();
        producer.Wait(1000);
        Assert(lockFree, "Producer blocked on an executing callback");
    }

    private static void ClearDuringRequest()
    {
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        using (var server = new MockServer(_ =>
        {
            entered.Set();
            release.Wait(3000);
            return Reply.Success();
        }))
        {
            var client = Client(Config(server));
            Send(client, () =>
            {
                Assert(entered.Wait(3000), "Request did not enter server");
                client.ClearHistory();
                release.Set();
            });
            Assert(client.HistoryCount == 0, "In-flight response restored cleared history");
        }
    }

    private static JObject Serialize(ChatCompletionRequest request) => JObject.Parse(JsonConvert.SerializeObject(request));

    private static void TokenOverrides()
    {
        var messages = new List<ChatMessage>();
        var legacy = Serialize(ChatRequestBuilder.Build("http://localhost/v1/chat/completions", "gpt-6-luna", messages, 0.7f, 512, null, "max_tokens"));
        Assert((int?)legacy["max_tokens"] == 512 && legacy["max_completion_tokens"] == null, "Legacy override ignored");
        var completion = Serialize(ChatRequestBuilder.Build("http://localhost/v1/chat/completions", "custom-alias", messages, 0.7f, 512, null, "max_completion_tokens"));
        Assert((int?)completion["max_completion_tokens"] == 512 && completion["max_tokens"] == null, "Completion override ignored");
        var official = Serialize(ChatRequestBuilder.Build("https://api.openai.com/v1/chat/completions", "custom-alias", messages, 0.7f, 512, null, "Auto"));
        Assert(official["max_tokens"] == null, "Official endpoint used the deprecated parameter");
    }

    private static void ReasoningParameters()
    {
        foreach (string model in new[] { "gpt-5", "gpt-5-mini", "gpt-6-luna", "gpt-6-sol", "o3", "o4-mini" })
        {
            var body = Serialize(ChatRequestBuilder.Build("http://localhost/v1/chat/completions", model,
                new List<ChatMessage>(), 0.7f, 700, Tools(), "Auto"));
            Assert(body["temperature"] == null, "Sampling parameter sent to " + model);
            Assert(body["max_completion_tokens"] != null && body["max_tokens"] == null, "Wrong budget for " + model);
        }
    }

    private static void EmptyTools()
    {
        var request = ChatRequestBuilder.Build("http://localhost/", "local-model", new List<ChatMessage>(), 0.7f, 700, new List<ToolDefinition>(), "Auto");
        Assert(request.Tools == null, "Empty tools should be omitted");
    }

    private static void OllamaRequest()
    {
        using (var server = new MockServer(body =>
        {
            Assert(body["messages"] == null && body["max_tokens"] == null, "Chat fields leaked into Ollama generate");
            Assert((int?)body["options"]?["num_predict"] == 700, "Ollama budget missing");
            Assert(body["options"]?["temperature"] != null, "Ollama temperature missing");
            return new Reply(200, "{\"response\":\"{\\\"action\\\":\\\"idle\\\",\\\"dialogue\\\":\\\"hello\\\"}\",\"done\":true}");
        }, "api/generate")) Send(Client(Config(server)));
    }

    private static string SendError(LLMClient client)
    {
        string error = null;
        bool complete = false;
        client.SendRequest(State(), _ => complete = true, value => { error = value; complete = true; });
        Pump(client, () => complete);
        Assert(!client.IsBusy, "Error kept busy state set");
        Assert(error != null, "Expected an error callback");
        return error;
    }

    private static void HttpError()
    {
        using (var server = new MockServer(_ => new Reply(400, "{\"error\":{\"message\":\"fixture rejection\"}}")))
        {
            string error = SendError(Client(Config(server)));
            Assert(error.Contains("HTTP 400") && error.Contains("fixture rejection"), "HTTP details lost: " + error);
            Assert(server.Requests.Count == 1, "Configuration errors were retried");
        }
    }

    private static Reply ToolReply() => new Reply(200, JsonConvert.SerializeObject(new
    {
        choices = new[] { new { message = new { role = "assistant", tool_calls = new[]
            { new { id = "call_fixture", type = "function", function = new { name = "test", arguments = "{}" } } } }, finish_reason = "tool_calls" } }
    }));

    private static void ToolRoundTrip()
    {
        ToolBridge.Calls = 0;
        ToolBridge.LastThreadId = 0;
        int round = 0;
        using (var server = new MockServer(body =>
        {
            if (++round == 1) return ToolReply();
            var messages = (JArray)body["messages"];
            Assert((string)messages[messages.Count - 1]["tool_call_id"] == "call_fixture", "Tool call ID was lost");
            Assert((string)messages[messages.Count - 1]["role"] == "tool", "Wrong tool result role");
            return Reply.Success();
        }))
        {
            Send(Client(Config(server)), tools: Tools());
            Assert(ToolBridge.Calls == 1 && round == 2, "Tool round count mismatch");
            Assert(ToolBridge.LastThreadId == Thread.CurrentThread.ManagedThreadId, "Tool ran off the main thread");
        }
    }

    private static void ExpiredTool()
    {
        ToolBridge.Calls = 0;
        var client = Client(new ConfigManager());
        var execute = typeof(LLMClient).GetMethod("ExecuteToolCallsOnMainThread", BindingFlags.NonPublic | BindingFlags.Instance);
        var calls = new List<ToolCall> { new ToolCall { Id = "fixture", Function = new FunctionCall { Name = "test", Arguments = "{}" } } };
        Exception failure = null;
        Task.Run(() =>
        {
            try { execute.Invoke(client, new object[] { calls, 50 }); }
            catch (TargetInvocationException ex) { failure = ex.InnerException; }
        }).GetAwaiter().GetResult();
        Assert(failure is TimeoutException, "Expected tool dispatch timeout");
        client.ProcessMainThreadCallbacks();
        Assert(ToolBridge.Calls == 0, "Timed-out tool executed late");
    }

    private static void FrozenConfig()
    {
        ConfigManager config = null;
        int round = 0;
        using (var server = new MockServer(body =>
        {
            Assert((string)body["model"] == "local-model", "Model changed mid transaction");
            Assert((int?)body["max_tokens"] == 700, "Budget changed mid transaction");
            if (++round == 1)
            {
                config.ModelName.Value = "changed-model";
                config.MaxTokens.Value = 900;
                config.ApiBaseUrl.Value = "http://127.0.0.1:1/changed";
                return ToolReply();
            }
            return Reply.Success();
        }))
        {
            config = Config(server);
            Send(Client(config), tools: Tools());
            Assert(round == 2, "Second round missing");
        }
    }

    private static void CorrectiveRetry()
    {
        int round = 0;
        using (var server = new MockServer(body =>
        {
            if (++round == 1) return Reply.Success("not json");
            var messages = (JArray)body["messages"];
            Assert(((string)messages[messages.Count - 1]["content"]).Contains("previous response was invalid"), "Retry not corrective");
            return Reply.Success();
        }))
        {
            Send(Client(Config(server)));
            Assert(round == 2, "Unexpected retries");
        }
    }

    private static void RetryLimit()
    {
        using (var server = new MockServer(_ => Reply.Success("{\"action\":\"not_allowlisted\",\"dialogue\":\"hello\"}")))
        {
            string error = SendError(Client(Config(server)));
            Assert(server.Requests.Count == 4 && error.Contains("no usable response"), "Retry limit not enforced");
        }
    }

    private static void TokenExhaustion()
    {
        using (var server = new MockServer(_ => Reply.Success("", "length")))
        {
            string error = SendError(Client(Config(server)));
            Assert(error.Contains("token budget") && server.Requests.Count == 1, "Token exhaustion retried as malformed JSON");
        }
    }

    private static void BoundedCallbacks()
    {
        var client = Client(new ConfigManager());
        var enqueue = typeof(LLMClient).GetMethod("EnqueueMainThread", BindingFlags.NonPublic | BindingFlags.Instance);
        int calls = 0;
        enqueue.Invoke(client, new object[] { (Action)(() =>
        {
            calls++;
            enqueue.Invoke(client, new object[] { (Action)(() => calls++) });
        }) });
        client.ProcessMainThreadCallbacks();
        Assert(calls == 1, "New callbacks drained in same frame");
        client.ProcessMainThreadCallbacks();
        Assert(calls == 2, "Deferred callback missing");
    }

    private static void NullState()
    {
        using (var server = new MockServer(_ => Reply.Success()))
        {
            var client = Client(Config(server));
            bool error = false;
            client.SendRequest(null, _ => { }, _ => error = true);
            Assert(error && !client.IsBusy, "Null input left client busy");
            Send(client);
        }
    }

    private static void ReducedHistory()
    {
        using (var server = new MockServer(_ => Reply.Success()))
        {
            var config = Config(server);
            var client = Client(config);
            Send(client); Send(client);
            config.MaxConversationHistory.Value = 1;
            Send(client);
            var bodies = server.Requests.ToArray();
            Assert(((JArray)bodies[2]["messages"]).Count == 2 && client.HistoryCount == 0, "Old history still sent after reducing its limit");
        }
    }

    private static void DialogueLimit()
    {
        using (var server = new MockServer(_ => Reply.Success("{\"action\":\"idle\",\"dialogue\":\"This is a long fixture dialogue.\"}")))
        {
            var config = Config(server);
            config.MaxDialogueLength.Value = 12;
            var client = Client(config);
            LLMActionResponse response = null;
            string error = null;
            client.SendRequest(State(), value => response = value, value => error = value);
            Pump(client, () => response != null || error != null);
            Assert(error == null && response.Dialogue.Length == 12 && response.Dialogue.EndsWith("..."), "Dialogue limit ignored");
        }
    }

    private sealed class Reply
    {
        public int Status;
        public string Body;
        public Reply(int status, string body) { Status = status; Body = body; }
        public static Reply Success(string content = "{\"action\":\"idle\",\"dialogue\":\"hello\"}", string finish = "stop") =>
            new Reply(200, JsonConvert.SerializeObject(new { choices = new[] { new { message = new { role = "assistant", content }, finish_reason = finish } } }));
    }

    private sealed class MockServer : IDisposable
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly Task _worker;
        public readonly ConcurrentQueue<JObject> Requests = new ConcurrentQueue<JObject>();
        public readonly string Url;
        public MockServer(Func<JObject, Reply> handler, string path = "v1/chat/completions")
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            string root = $"http://127.0.0.1:{port}/";
            Url = root + path;
            _listener.Prefixes.Add(root);
            _listener.Start();
            _worker = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch (Exception) when (!_listener.IsListening) { break; }
                    Reply reply;
                    try
                    {
                        using (var reader = new StreamReader(context.Request.InputStream))
                        {
                            var body = JObject.Parse(await reader.ReadToEndAsync());
                            Requests.Enqueue(body);
                            reply = handler(body);
                        }
                    }
                    catch (Exception ex) { reply = new Reply(500, ex.Message); }
                    byte[] bytes = Encoding.UTF8.GetBytes(reply.Body);
                    context.Response.StatusCode = reply.Status;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    context.Response.Close();
                }
            });
        }
        public void Dispose()
        {
            _listener.Close();
            try { _worker.GetAwaiter().GetResult(); }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }
    }
}
