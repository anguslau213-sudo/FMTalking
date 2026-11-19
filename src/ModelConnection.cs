using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FmPlayerTalk;

internal enum ModelMode { Codex, LocalHttp, RemoteHttp }

internal sealed record ModelConnectionSettings(ModelMode Mode, string BaseUrl, string Model, bool HasApiKey);

// One globally selected connection. Career files store conversation history separately.
internal static class ModelConnection
{
    private const string LocalDefault = "http://127.0.0.1:11434/v1";
    private const string CompletionPath = "/chat/completions";
    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly object Sync = new();
    private static readonly HttpClient LocalClient = CreateHttpClient(useProxy: false);
    private static readonly HttpClient RemoteClient = CreateHttpClient(useProxy: true);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static Configuration? cached;
    private static bool inFlight;

    private static string SettingsDirectory => ModDataPath.Root;
    private static string SettingsPath => Path.Combine(SettingsDirectory, "model-settings.json");

    private sealed record Profile(string BaseUrl = "", string Model = "", string? ProtectedKey = null);

    private sealed record Configuration
    {
        public ModelMode Mode { get; init; }
        public Profile Codex { get; init; } = new();
        public Profile Local { get; init; } = new(LocalDefault);
        public Profile Remote { get; init; } = new();
        public bool HomeNotifications { get; init; } = true;
        public bool EnablePromiseWaivers { get; init; }
        public bool EnableBoardFunding { get; init; }
    }

    internal sealed class Route
    {
        private readonly string? savedKey;
        private readonly string? newKey;

        internal Route(ModelMode mode, string baseUrl, string model, string? savedKey, string? newKey)
        {
            Mode = mode;
            BaseUrl = baseUrl;
            Model = model;
            this.savedKey = savedKey;
            this.newKey = newKey;
        }

        internal ModelMode Mode { get; }
        internal string BaseUrl { get; }
        internal string Model { get; }
        internal string? ProtectedKey() => newKey == null ? savedKey : Secret.Protect(newKey, BaseUrl);
        internal string? ApiKey() => newKey ?? (savedKey == null ? null : Secret.Unprotect(savedKey, BaseUrl));
    }

    internal static ModelConnectionSettings Current
    {
        get
        {
            lock (Sync)
            {
                var configuration = Read();
                return PublicSettings(configuration, configuration.Mode);
            }
        }
    }

    internal static ModelConnectionSettings SettingsFor(ModelMode mode)
    {
        lock (Sync)
            return PublicSettings(Read(), mode);
    }

    internal static bool HomeNotifications
    {
        get
        {
            lock (Sync)
                return Read().HomeNotifications;
        }
    }

    internal static bool IsBusy
    {
        get
        {
            lock (Sync)
                return inFlight;
        }
    }

    internal static void SetHomeNotifications(bool enabled)
    {
        lock (Sync)
        {
            var current = Read();
            if (current.HomeNotifications != enabled)
                Write(current with { HomeNotifications = enabled });
        }
    }

    internal static bool PromiseWaiversEnabled
    {
        get
        {
            lock (Sync)
                return Read().EnablePromiseWaivers;
        }
    }

    internal static void SetPromiseWaiversEnabled(bool enabled)
    {
        lock (Sync)
        {
            var current = Read();
            if (current.EnablePromiseWaivers != enabled)
                Write(current with { EnablePromiseWaivers = enabled });
        }
    }

    internal static bool BoardFundingEnabled
    {
        get { lock (Sync) return Read().EnableBoardFunding; }
    }

    internal static void SetBoardFundingEnabled(bool enabled)
    {
        lock (Sync)
        {
            var current = Read();
            if (current.EnableBoardFunding != enabled)
                Write(current with { EnableBoardFunding = enabled });
        }
    }

    internal static ModelConnectionSettings Save(ModelMode mode, string baseUrl, string model, string? newApiKey,
        bool clearApiKey = false)
    {
        lock (Sync)
        {
            ThrowWhileBusy();
            var prior = Read();
            var route = Prepare(prior, mode, baseUrl, model, newApiKey, clearApiKey);
            var profile = new Profile(route.BaseUrl, route.Model, route.ProtectedKey());
            var next = mode switch
            {
                ModelMode.Codex => prior with { Mode = mode, Codex = profile },
                ModelMode.LocalHttp => prior with { Mode = mode, Local = profile },
                ModelMode.RemoteHttp => prior with { Mode = mode, Remote = profile },
                _ => throw InvalidMode()
            };
            Write(next);
            return PublicSettings(next, mode);
        }
    }

    internal static Route Capture()
    {
        lock (Sync)
        {
            ThrowWhileBusy();
            var current = Read();
            var profile = GetProfile(current, current.Mode);
            return Prepare(current, current.Mode, profile.BaseUrl, profile.Model, null, clearKey: false);
        }
    }

    internal static async Task TestAsync(ModelMode mode, string baseUrl, string model, string? newApiKey,
        bool clearApiKey = false, CancellationToken cancellationToken = default)
    {
        Route route;
        lock (Sync)
        {
            ThrowWhileBusy();
            route = Prepare(Read(), mode, baseUrl, model, newApiKey, clearApiKey);
        }
        var prompt = "这是 FM27 的模拟训练场景。主教练说「早上好」，你是虚构的一线队助理教练。"
            + "用自然的一句话回复。只返回一个 JSON 对象，delta 为 0，kind 为 normal，"
            + "reason 为空字符串，proposal 和 dinner 都为 none。格式如下：" + Conversation.ReplySchema;
        Reply reply;
        try
        {
            reply = await AskAsync(route, prompt, Path.Combine(SettingsDirectory, "connection-test"),
                onRead: null, timingLog: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Conversation.NonDialogueOperationException)
        {
            throw new InvalidOperationException("连接测试返回了游戏对话以外的操作。");
        }
        if (reply.Kind != InteractionKind.Normal || reply.AffinityChange != 0 || reply.TrustChange != 0 ||
            reply.Proposal != RelationshipProposal.None || reply.AcceptDinner || reply.PromiseWaiverId != null)
            throw new InvalidOperationException("已收到测试回复，但模型没有按对话格式返回预期内容。");
    }

    internal static async Task<Reply> AskAsync(Route route, string prompt, string codexDirectory, Action? onRead,
        Action<string>? timingLog, CancellationToken cancellationToken = default, string? replySchema = null,
        string? trustedTaskInstructions = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Sync)
        {
            ThrowWhileBusy();
            inFlight = true;
        }
        try
        {
            var schema = replySchema ?? Conversation.ReplySchema;
            // Only fixed application instructions belong here; player input stays
            // in prompt data. Utility tasks must not inherit the NPC dialogue role.
            var instructions = trustedTaskInstructions ?? Conversation.RoleBoundaryInstructions;
            var transport = route.Mode switch
            {
                ModelMode.Codex => Conversation.AskCodexAsync(prompt, onRead, codexDirectory, route.Model, timingLog,
                    cancellationToken, schema, instructions),
                ModelMode.LocalHttp or ModelMode.RemoteHttp => AskHttpAsync(route, prompt, onRead, cancellationToken, schema, instructions),
                _ => throw InvalidMode()
            };
            return await transport.ConfigureAwait(false);
        }
        finally
        {
            lock (Sync)
                inFlight = false;
        }
    }

    private static void ThrowWhileBusy()
    {
        if (inFlight)
            throw new InvalidOperationException("正在等待模型回复，请等当前请求结束。");
    }

    private static ModelConnectionSettings PublicSettings(Configuration configuration, ModelMode mode)
    {
        var profile = GetProfile(configuration, mode);
        return new ModelConnectionSettings(mode, profile.BaseUrl, profile.Model, profile.ProtectedKey != null);
    }

    private static Profile GetProfile(Configuration configuration, ModelMode mode) => mode switch
    {
        ModelMode.Codex => configuration.Codex ?? new(),
        ModelMode.LocalHttp => configuration.Local ?? new(LocalDefault),
        ModelMode.RemoteHttp => configuration.Remote ?? new(),
        _ => throw InvalidMode()
    };

    private static Route Prepare(Configuration configuration, ModelMode mode, string baseUrl, string model,
        string? newKey, bool clearKey)
    {
        var old = GetProfile(configuration, mode);
        model = (model ?? "").Trim();
        if (model.Length > 200 || model.Any(char.IsControl))
            throw new InvalidOperationException("模型名称过长或包含无效字符。");
        if (mode == ModelMode.Codex)
            return new Route(mode, "", model, null, null);

        baseUrl = (baseUrl ?? "").Trim();
        if (baseUrl.Length > 2000 || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0)
            throw new InvalidOperationException("请输入有效的 API 接口地址，不要在地址里填写密钥或参数。");
        if (mode == ModelMode.LocalHttp && !uri.IsLoopback)
            throw new InvalidOperationException("本地模式只连接本机地址，例如 http://127.0.0.1:11434/v1。");
        if (mode == ModelMode.RemoteHttp && (uri.Scheme != Uri.UriSchemeHttps || uri.IsLoopback))
            throw new InvalidOperationException("远程 API 需要使用 HTTPS 地址。");
        if (model.Length == 0)
            throw new InvalidOperationException("请输入服务提供的准确模型 ID。");

        baseUrl = uri.AbsoluteUri.TrimEnd('/');
        var suppliedKey = string.IsNullOrWhiteSpace(newKey) ? null : newKey.Trim();
        if (suppliedKey != null && (suppliedKey.Length > 4096 || suppliedKey.Any(c => c is < '!' or > '~')))
            throw new InvalidOperationException("API 密钥包含无效字符。");
        var savedKey = !clearKey && old.BaseUrl == baseUrl ? old.ProtectedKey : null;
        if (mode == ModelMode.RemoteHttp && suppliedKey == null && savedKey == null)
            throw new InvalidOperationException("请填写此远程 API 地址的密钥。");
        return new Route(mode, baseUrl, model, savedKey, suppliedKey);
    }

    private static Configuration Read()
    {
        if (cached != null)
            return cached;
        try
        {
            using var stream = File.OpenRead(SettingsPath);
            if (stream.Length > 128 * 1024)
                throw new InvalidDataException();
            var configuration = JsonSerializer.Deserialize<Configuration>(stream, JsonOptions);
            if (configuration == null || !Enum.IsDefined(configuration.Mode))
                throw new InvalidDataException();
            return cached = configuration;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return cached = new Configuration();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("无法读取模型设置。请检查本机设置文件。", error);
        }
    }

    private static void Write(Configuration settings)
    {
        var path = SettingsPath;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            cached = settings;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("无法保存本机模型设置。", error);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static InvalidOperationException InvalidMode() => new("无法识别模型连接模式。");

    private static HttpClient CreateHttpClient(bool useProxy) => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = useProxy
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private static async Task<Reply> AskHttpAsync(Route route, string prompt, Action? onRead,
        CancellationToken cancellationToken, string replySchema, string instructions)
    {
        var endpoint = route.BaseUrl.EndsWith(CompletionPath, StringComparison.Ordinal)
            ? route.BaseUrl : route.BaseUrl + CompletionPath;
        var payload = JsonSerializer.Serialize(new
        {
            model = route.Model,
            messages = new[]
            {
                new { role = "system", content = instructions +
                    "\n只返回符合以下 JSON Schema 的单一 JSON 对象，不要附加文字或 Markdown：" + replySchema },
                new { role = "user", content = prompt }
            },
            response_format = new { type = "json_object" }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        if (route.ApiKey() is { } key)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var timeLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeLimit.CancelAfter(TimeSpan.FromSeconds(100));
        var client = route.Mode == ModelMode.LocalHttp ? LocalClient : RemoteClient;
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeLimit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw HttpError(response.StatusCode);
            onRead?.Invoke();
            return await ReadHttpReplyAsync(response.Content, timeLimit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("模型回复超时，请重试。");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("无法连接模型服务。请检查地址、服务状态和网络。");
        }
        catch (IOException)
        {
            throw new InvalidOperationException("读取模型回复时连接中断，请重试。");
        }
    }

    private static async Task<Reply> ReadHttpReplyAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidOperationException("模型服务返回内容过长，本次未保存。");
        using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        int size;
        while ((size = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (body.Length + size > MaximumResponseBytes)
                throw new InvalidOperationException("模型服务返回内容过长，本次未保存。");
            body.Write(buffer, 0, size);
        }
        body.Position = 0;
        try
        {
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: token).ConfigureAwait(false);
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1)
                throw new JsonException();
            var choice = choices[0];
            var finish = Conversation.ReadString(choice, "finish_reason");
            if (finish is "tool_calls" or "function_call")
                throw new Conversation.NonDialogueOperationException();
            if (finish is "length" or "content_filter" || choice.ValueKind != JsonValueKind.Object ||
                !choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                throw new JsonException();
            if ((message.TryGetProperty("tool_calls", out var tools) &&
                    tools.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined &&
                    (tools.ValueKind != JsonValueKind.Array || tools.GetArrayLength() != 0)) ||
                (message.TryGetProperty("function_call", out var function) &&
                    function.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined))
                throw new Conversation.NonDialogueOperationException();
            return Conversation.ParseReply(Conversation.ReadString(message, "content") ?? "");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("模型服务未返回有效的聊天回复，本次未保存。");
        }
    }

    private static InvalidOperationException HttpError(HttpStatusCode status) => new(status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "模型服务拒绝了认证。请检查 API 密钥和模型权限。",
        HttpStatusCode.PaymentRequired => "模型服务返回用量或余额不足。请检查服务账户。",
        HttpStatusCode.NotFound => "未找到聊天接口或模型。请检查 API 地址与模型 ID。",
        HttpStatusCode.MethodNotAllowed => "此地址不接收聊天请求。请填写 API 服务地址。",
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
            "模型服务不接受当前请求。请核对模型 ID 与 JSON 模式支持。",
        HttpStatusCode.TooManyRequests => "模型服务达到请求或用量限制，请稍后重试。",
        _ when (int)status >= 500 => $"模型服务暂时不可用（HTTP {(int)status}）。",
        _ => $"模型连接未成功（HTTP {(int)status}）。请检查 API 地址。"
    });

    private static class Secret
    {
        private const uint UiForbidden = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct Blob
        {
            public int Length;
            public IntPtr Pointer;
        }

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy,
            IntPtr reserved, IntPtr prompt, uint flags, out Blob output);

        [DllImport("crypt32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy,
            IntPtr reserved, IntPtr prompt, uint flags, out Blob output);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        internal static string Protect(string key, string endpoint)
        {
            var plain = Encoding.UTF8.GetBytes(key);
            try { return Convert.ToBase64String(ProtectBytes(plain, endpoint, encrypt: true)); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }

        internal static string Unprotect(string protectedKey, string endpoint)
        {
            byte[] plain;
            try { plain = ProtectBytes(Convert.FromBase64String(protectedKey), endpoint, encrypt: false); }
            catch (FormatException)
            {
                throw new InvalidOperationException("本机 API 密钥无法读取，请重新输入。");
            }
            try { return Encoding.UTF8.GetString(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }

        private static byte[] ProtectBytes(byte[] bytes, string endpoint, bool encrypt)
        {
            if (!OperatingSystem.IsWindows())
                throw new InvalidOperationException("当前系统无法保护 API 密钥。");
            var entropyBytes = SHA256.HashData(Encoding.UTF8.GetBytes("FMPlayerTalk:API:" + endpoint));
            var inputHandle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            var entropyHandle = GCHandle.Alloc(entropyBytes, GCHandleType.Pinned);
            Blob output = default;
            try
            {
                var input = new Blob { Length = bytes.Length, Pointer = inputHandle.AddrOfPinnedObject() };
                var entropy = new Blob { Length = entropyBytes.Length, Pointer = entropyHandle.AddrOfPinnedObject() };
                var success = encrypt
                    ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
                if (!success || output.Pointer == IntPtr.Zero || output.Length is <= 0 or > 32 * 1024)
                    throw new InvalidOperationException(encrypt
                        ? "Windows 无法保护此 API 密钥。"
                        : "本机 API 密钥无法读取，请重新输入。");
                var result = new byte[output.Length];
                Marshal.Copy(output.Pointer, result, 0, result.Length);
                return result;
            }
            finally
            {
                inputHandle.Free();
                entropyHandle.Free();
                if (output.Pointer != IntPtr.Zero)
                {
                    if (!encrypt && output.Length is > 0 and <= 32 * 1024)
                        for (var index = 0; index < output.Length; index++)
                            Marshal.WriteByte(output.Pointer, index, 0);
                    _ = LocalFree(output.Pointer);
                }
            }
        }
    }
}
