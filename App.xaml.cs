using hanabimanga.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Activation;
using LaunchActivatedEventArgs = Microsoft.UI.Xaml.LaunchActivatedEventArgs;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace hanabimanga
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private static Window? _window;
        private static Task _supabaseInitialization = Task.CompletedTask;
        private static readonly object _pendingProtocolLock = new();
        private static readonly Queue<Uri> _pendingProtocolUris = new();

        /// <summary>
        /// 主窗口引用,供子窗口(如 AnnouncementWindow)定位/居中使用。
        /// </summary>
        public static Window? MainWindow => _window;

        public static Task SupabaseInitialization => _supabaseInitialization;

        /// <summary>
        /// Supabase 初始化失败的具体原因(配置缺失或异常信息);成功时为 null。
        /// 供 UI 在「尚未初始化」提示中展示真实原因,便于排查打包/环境问题。
        /// </summary>
        public static string? SupabaseInitializationError { get; private set; }

        public const string DirectUrl = "https://uhkvqrxmcapgtpspglrp.supabase.co";
        public const string AcceleratedUrl = "https://uhkvqrxmcapgtpspglrp.moedot.net";

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        public static IConfiguration Configuration { get; private set; } = null!;

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            if (!Program.EnsureSingleInstanceForXamlLaunch(
                    AppInstance.GetCurrent().GetActivatedEventArgs()))
            {
                Environment.Exit(0);
                return;
            }

            // 单实例 / 协议激活重定向 + Activated 订阅都已在 Program.Main 里完成;
            // 此处只做应用本身的初始化与窗口创建。
            Configuration = BuildConfiguration();

            try
            {
                var settings = await ReaderStorageService.Instance.LoadSettingsAsync();
                ThemeColorService.Instance.Apply(settings.AccentColor);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[theme] apply accent color failed: {ex.Message}");
            }

            _supabaseInitialization = Task.Run(InitializeSupabaseAsync);

            _window = new MainWindow();
            _window.Activate();

            // 冷启动本身即由协议回跳触发时,窗口就绪后立即处理。
            TryHandleProtocolActivation(AppInstance.GetCurrent().GetActivatedEventArgs());
            FlushPendingProtocolActivations();
        }

        /// <summary>
        /// 由 Program.OnAppInstanceActivated 调用 —— 主实例收到其他进程
        /// 重定向过来的激活事件(磁碰式 Magic Link 邮件回跳等)。
        /// 回调运行在后台线程,内部 DispatcherQueue.TryEnqueue 切回 UI 线程。
        /// </summary>
        public static void HandleRedirectedActivation(AppActivationArguments args)
        {
            TryHandleProtocolActivation(args);
        }

        public static void HandleForwardedActivation(string payload)
        {
            if (!string.IsNullOrWhiteSpace(payload)
                && Uri.TryCreate(payload.Trim(), UriKind.Absolute, out var uri)
                && uri.Scheme.Equals("hanabimanga", StringComparison.OrdinalIgnoreCase))
            {
                HandleProtocolUri(uri);
                return;
            }

            ActivateMainWindow();
        }

        private static void TryHandleProtocolActivation(AppActivationArguments activatedArgs)
        {
            if (activatedArgs.Kind != ExtendedActivationKind.Protocol
                || activatedArgs.Data is not IProtocolActivatedEventArgs protocolArgs)
            {
                return;
            }

            HandleProtocolUri(protocolArgs.Uri);
        }

        private static void HandleProtocolUri(Uri uri)
        {
            if (_window is not MainWindow mainWindow)
            {
                EnqueuePendingProtocolActivation(uri);
                return;
            }

            if (!mainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    _ = mainWindow.HandleMagicLinkCallbackAsync(uri);
                }))
            {
                EnqueuePendingProtocolActivation(uri);
            }
        }

        private static void ActivateMainWindow()
        {
            if (_window is not Window window)
            {
                return;
            }

            window.DispatcherQueue.TryEnqueue(window.Activate);
        }

        private static void EnqueuePendingProtocolActivation(Uri uri)
        {
            lock (_pendingProtocolLock)
            {
                _pendingProtocolUris.Enqueue(uri);
            }
        }

        private static void FlushPendingProtocolActivations()
        {
            List<Uri> pending = new();
            lock (_pendingProtocolLock)
            {
                while (_pendingProtocolUris.Count > 0)
                {
                    pending.Add(_pendingProtocolUris.Dequeue());
                }
            }

            foreach (var uri in pending)
            {
                HandleProtocolUri(uri);
            }
        }

        private static async Task InitializeSupabaseAsync()
        {
            try
            {
                var anonKey = Configuration["Supabase:AnonKey"];
                if (string.IsNullOrWhiteSpace(anonKey))
                {
                    SupabaseInitializationError = "配置缺失:未读取到 Supabase:AnonKey。";
                    Debug.WriteLine("[supabase] anon key missing");
                    return;
                }

                var settings = await ReaderStorageService.Instance.LoadSettingsAsync();
                var url = await ResolveUrlForEndpointAsync(settings.ApiEndpoint);

                await SupabaseService.Instance.InitializeAsync(
                    url,
                    anonKey,
                    readerClientVerifySecret: GetConfigurationValue(
                        "Reader:AppClientVerifySecret",
                        "Reader:ClientVerifySecret",
                        "APP_CLIENT_VERIFY_SECRET"),
                    readerClientFingerprint: GetConfigurationValue(
                        "Reader:ClientFingerprint",
                        "Reader:AllowedSignSha256",
                        "APP_ALLOWED_SIGN_SHA256"));

                SupabaseInitializationError = null;
                Debug.WriteLine($"[supabase] initialized, url={url}");
            }
            catch (Exception ex)
            {
                SupabaseInitializationError = $"{ex.GetType().Name}: {ex.Message}";
                Debug.WriteLine($"[supabase] 初始化失败: {ex}");
            }
        }

        /// <summary>
        /// 将线路偏好("auto" / "direct" / "accelerated")解析为实际接口地址。
        /// "auto" 时并发探测两条线路,取先响应者。
        /// </summary>
        public static async Task<string> ResolveUrlForEndpointAsync(string? endpoint)
        {
            switch (endpoint)
            {
                case "direct":
                    Debug.WriteLine("[api] endpoint=direct");
                    return DirectUrl;
                case "accelerated":
                    Debug.WriteLine("[api] endpoint=accelerated");
                    return AcceleratedUrl;
                default:
                    return await AutoDetectEndpointAsync();
            }
        }

        private static async Task<string> AutoDetectEndpointAsync()
        {
            Debug.WriteLine("[api] auto-detecting best endpoint...");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            var directTask = ProbeEndpointAsync(http, DirectUrl, cts.Token);
            var accelTask = ProbeEndpointAsync(http, AcceleratedUrl, cts.Token);

            var completed = await Task.WhenAny(directTask, accelTask);
            if (completed.Result is not null)
            {
                Debug.WriteLine($"[api] auto-detected winner: {completed.Result}");
                return completed.Result;
            }

            // 先到的失败了,等另一个
            var other = completed == directTask ? accelTask : directTask;
            try { await other; } catch { /* best-effort */ }
            if (other.IsCompletedSuccessfully && other.Result is not null)
            {
                Debug.WriteLine($"[api] auto-detected fallback: {other.Result}");
                return other.Result;
            }

            Debug.WriteLine("[api] auto-detect failed, defaulting to direct");
            return DirectUrl;
        }

        private static async Task<string?> ProbeEndpointAsync(
            HttpClient http, string baseUrl, CancellationToken ct)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, $"{baseUrl}/rest/v1/");
                using var response = await http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct);
                return baseUrl;
            }
            catch
            {
                return null;
            }
        }

        private static IConfiguration BuildConfiguration()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory);

            // 嵌入资源兜底:MSIX 未带上松散 appsettings.json 时仍能拿到配置。
            // 作为最底层来源,松散文件与环境变量仍可覆盖它。
            var embedded = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("hanabimanga.appsettings.json");
            if (embedded is not null)
            {
                builder.AddJsonStream(embedded);
            }

            return builder
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false)
                .AddEnvironmentVariables()
                .Build();
        }

        private static string? GetConfigurationValue(params string[] keys)
        {
            foreach (var key in keys)
            {
                var value = Configuration[key];
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }
    }
}
