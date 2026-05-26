using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace hanabimanga
{
    /// <summary>
    /// 自定义入口:在 Application.Start 之前完成单实例判定与 Magic Link
    /// (hanabimanga://) 协议激活重定向,避免第二个进程把 XAML 框架拉起来后再
    /// 关掉,造成窗口闪烁或多实例残留。
    /// 配合 csproj 的 DISABLE_XAML_GENERATED_MAIN 替换 WinUI 模板生成的 Main。
    /// </summary>
    public static class Program
    {
        private const string ProtocolScheme = "hanabimanga";
        private const string SingleInstanceKey = "hanabimanga-main";
        private const string SingleInstanceMutexName = @"Local\hanabimanga-single-instance";
        private const string SingleInstancePipeName = "hanabimanga-single-instance-pipe";
        private const string SingleInstanceLockFileName = "hanabimanga.instance.lock";
        private const string ActivationInboxDirectoryName = "activation-inbox";

        // MSIX 下 LocalApplicationData 通常映射到 ...\Packages\<PFN>\LocalCache\Local,
        // 这是 packaged app 100% 能写的位置;非 MSIX 跑时就是真正的 %LOCALAPPDATA%。
        private static readonly string TraceFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "hanabi-trace.log");

        private static void Trace(string msg)
        {
            // 故意不 catch:让任何写失败崩到 EventLog,以暴露 trace 路径问题。
            File.AppendAllText(TraceFile,
                $"[{DateTime.Now:HH:mm:ss.fff}] pid={Environment.ProcessId} {msg}{Environment.NewLine}");
        }

        // 把 key holder 实例保活在静态字段:否则 DecideRedirection 返回后
        // 局部 keyInstance 被 GC 终结化,WinRT COM 对象释放可能连带掉 key 注册,
        // 让后续来访的协议激活进程看不到主实例,各自又当 key holder 开新窗口。
        private static AppInstance? _mainAppInstance;
        private static Mutex? _singleInstanceMutex;
        private static CancellationTokenSource? _pipeServerCts;
        private static FileStream? _singleInstanceLockStream;
        private static CancellationTokenSource? _activationInboxCts;
        private static string? _singleInstanceDirectory;

        [STAThread]
        private static int Main(string[] args)
        {
            Trace($"Main start, args=[{string.Join(",", args)}]");
            WinRT.ComWrappersSupport.InitializeComWrappers();
            RegisterProtocolForUnpackagedRuns();

            var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            Trace($"Initial ActivationKind={activatedArgs?.Kind}");

            // -ServerName:... 是 Windows 把 .exe 作为 WinRT/COM out-of-process server 拉起来
            // 用于跨进程 IPC(包括 AppInstance.RedirectActivationToAsync 的目的端)。
            // 这种纯 COM server 进程不做单实例判定,避免污染 key 注册。但如果它携带
            // Protocol/File 等真实用户激活参数,仍必须重定向到当前主窗口。
            bool isComServerActivation = args.Length > 0
                && args[0].StartsWith("-ServerName:", StringComparison.Ordinal);
            bool shouldCheckSingleInstance = !isComServerActivation || IsUserActivation(activatedArgs);

            if (shouldCheckSingleInstance)
            {
                if (!TryClaimSingleInstance(activatedArgs, args))
                {
                    Trace("Exiting (forwarded to existing mutex owner)");
                    return 0;
                }

                bool redirect;
                try
                {
                    redirect = DecideRedirection(activatedArgs);
                }
                catch (Exception ex)
                {
                    Trace($"DecideRedirection threw: {ex.GetType().Name}: {ex.Message}");
                    redirect = false;
                }
                Trace($"DecideRedirection returned redirect={redirect}");

                if (redirect)
                {
                    Trace("Exiting (redirected)");
                    return 0;
                }
            }
            else
            {
                Trace("ServerName COM activation without rich args -> skip single-instance");
            }

            Trace("Calling Application.Start");
            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
            Trace("Application.Start returned");
            _pipeServerCts?.Cancel();
            _activationInboxCts?.Cancel();
            _singleInstanceLockStream?.Dispose();
            _singleInstanceMutex?.Dispose();
            return 0;
        }

        private static bool DecideRedirection(AppActivationArguments? activatedArgs)
        {
            var current = AppInstance.GetCurrent();
            Trace($"GetCurrent().Key='{current.Key}' ProcessId={current.ProcessId}");
            var keyInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
            Trace($"FindOrRegisterForKey: keyInstance.Key='{keyInstance.Key}' ProcessId={keyInstance.ProcessId} IsCurrent={keyInstance.IsCurrent}");

            if (keyInstance.IsCurrent)
            {
                // 关键: Activated 订阅必须在 Main 里 Application.Start 之前完成。
                // 这是 AppLifecycle 把当前进程标记为"活跃 key holder"的必要条件;
                // 没订阅时,其他实例的 FindOrRegisterForKey 看不到这一份注册,
                // 就会自己注册自己,从而开出第二个窗口。
                _mainAppInstance = keyInstance;
                keyInstance.Activated += OnAppInstanceActivated;
                return false;
            }

            if (activatedArgs is null)
            {
                Trace("No activation args; cannot redirect safely");
                return false;
            }

            Trace($"Calling RedirectActivationTo kind={activatedArgs.Kind}");
            RedirectActivationTo(activatedArgs, keyInstance);
            Trace("RedirectActivationTo returned");
            return true;
        }

        private static bool IsUserActivation(AppActivationArguments? args) =>
            args is not null && args.Kind != ExtendedActivationKind.Launch;

        private static bool TryClaimSingleInstance(AppActivationArguments? activatedArgs, string[] args)
        {
            var payload = ExtractProtocolUri(activatedArgs, args) ?? "";

            if (TryAcquireSingleInstanceFileLock())
            {
                Trace("File lock IsFirst=True");
                _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var isFirstMutexOwner);
                Trace($"Mutex '{SingleInstanceMutexName}' IsFirst={isFirstMutexOwner}");
                StartActivationInboxPoller();
                StartPipeServer();
                return true;
            }

            Trace("File lock IsFirst=False");
            if (!WriteActivationPayload(payload) && !ForwardActivationToExistingInstance(payload))
            {
                Trace("ForwardActivationToExistingInstance failed; existing file lock owner still suppresses new UI");
            }

            return false;
        }

        public static bool EnsureSingleInstanceForXamlLaunch(AppActivationArguments? activatedArgs)
        {
            if (_singleInstanceMutex is not null)
            {
                return true;
            }

            var args = Environment.GetCommandLineArgs();
            return TryClaimSingleInstance(activatedArgs, args);
        }

        private static string? ExtractProtocolUri(AppActivationArguments? activatedArgs, string[] args)
        {
            if (activatedArgs?.Kind == ExtendedActivationKind.Protocol
                && activatedArgs.Data is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocolArgs)
            {
                return protocolArgs.Uri?.AbsoluteUri;
            }

            foreach (var arg in args)
            {
                if (arg.StartsWith($"{ProtocolScheme}://", StringComparison.OrdinalIgnoreCase))
                {
                    return arg;
                }
            }

            return null;
        }

        private static bool TryAcquireSingleInstanceFileLock()
        {
            try
            {
                _singleInstanceDirectory = ResolveSingleInstanceDirectory();
                Directory.CreateDirectory(_singleInstanceDirectory);

                var lockPath = Path.Combine(_singleInstanceDirectory, SingleInstanceLockFileName);
                _singleInstanceLockStream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);

                _singleInstanceLockStream.SetLength(0);
                var marker = Encoding.UTF8.GetBytes(
                    $"{Environment.ProcessId} {DateTimeOffset.UtcNow:O}");
                _singleInstanceLockStream.Write(marker, 0, marker.Length);
                _singleInstanceLockStream.Flush();
                return true;
            }
            catch (IOException ex)
            {
                Trace($"File lock unavailable: {ex.Message}");
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                Trace($"File lock unauthorized: {ex.Message}");
                return false;
            }
        }

        private static string ResolveSingleInstanceDirectory()
        {
            try
            {
                var localFolder = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                if (!string.IsNullOrWhiteSpace(localFolder))
                {
                    return Path.Combine(localFolder, "SingleInstance");
                }
            }
            catch
            {
                // Unpackaged runs fall back to the user's local app data directory.
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "hanabimanga", "SingleInstance");
        }

        private static string GetActivationInboxDirectory()
        {
            _singleInstanceDirectory ??= ResolveSingleInstanceDirectory();
            return Path.Combine(_singleInstanceDirectory, ActivationInboxDirectoryName);
        }

        private static bool WriteActivationPayload(string payload)
        {
            try
            {
                var inbox = GetActivationInboxDirectory();
                Directory.CreateDirectory(inbox);
                var fileName = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}.txt";
                File.WriteAllText(Path.Combine(inbox, fileName), payload, new UTF8Encoding(false));
                Trace($"Wrote activation payload length={payload.Length}");
                return true;
            }
            catch (Exception ex)
            {
                Trace($"WriteActivationPayload failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static void StartActivationInboxPoller()
        {
            _activationInboxCts = new CancellationTokenSource();
            var token = _activationInboxCts.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    DrainActivationInbox();

                    try
                    {
                        await Task.Delay(250, token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }, token);
        }

        private static void DrainActivationInbox()
        {
            try
            {
                var inbox = GetActivationInboxDirectory();
                if (!Directory.Exists(inbox))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(inbox, "*.txt"))
                {
                    string payload;
                    try
                    {
                        payload = File.ReadAllText(file, Encoding.UTF8);
                        File.Delete(file);
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }

                    Trace($"Inbox received payload length={payload.Length}");
                    App.HandleForwardedActivation(payload);
                }
            }
            catch (Exception ex)
            {
                Trace($"DrainActivationInbox failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void StartPipeServer()
        {
            _pipeServerCts = new CancellationTokenSource();
            var token = _pipeServerCts.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(
                            SingleInstancePipeName,
                            PipeDirection.In,
                            1,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous);

                        await server.WaitForConnectionAsync(token);
                        using var reader = new StreamReader(server, Encoding.UTF8);
                        var payload = await reader.ReadToEndAsync();
                        Trace($"Pipe received payload length={payload.Length}");
                        App.HandleForwardedActivation(payload);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Trace($"Pipe server error: {ex.GetType().Name}: {ex.Message}");
                        await Task.Delay(250, token).ContinueWith(_ => { }, TaskScheduler.Default);
                    }
                }
            }, token);
        }

        private static bool ForwardActivationToExistingInstance(string payload)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var client = new NamedPipeClientStream(
                        ".",
                        SingleInstancePipeName,
                        PipeDirection.Out,
                        PipeOptions.None);
                    client.Connect(250);

                    using var writer = new StreamWriter(client, new UTF8Encoding(false))
                    {
                        AutoFlush = true,
                    };
                    writer.Write(payload);
                    Trace($"Forwarded payload length={payload.Length}");
                    return true;
                }
                catch (TimeoutException)
                {
                    Thread.Sleep(100);
                }
                catch (IOException ex)
                {
                    Trace($"Pipe connect/write failed: {ex.GetType().Name}: {ex.Message}");
                    Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    Trace($"ForwardActivationToExistingInstance error: {ex.GetType().Name}: {ex.Message}");
                    return false;
                }
            }

            Trace("ForwardActivationToExistingInstance timed out");
            return false;
        }

        private static void RegisterProtocolForUnpackagedRuns()
        {
            if (HasPackageIdentity())
            {
                return;
            }

            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                var logo = string.IsNullOrEmpty(exePath) ? "" : $"{exePath},0";
                ActivationRegistrationManager.RegisterForProtocolActivation(
                    ProtocolScheme,
                    logo,
                    "花火漫画",
                    exePath);
                Trace($"Registered unpackaged protocol handler: {exePath}");
            }
            catch (Exception ex)
            {
                Trace($"RegisterProtocolForUnpackagedRuns failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool HasPackageIdentity()
        {
            try
            {
                _ = Windows.ApplicationModel.Package.Current.Id.FullName;
                return true;
            }
            catch
            {
                return false;
            }
        }

        // 主实例被再次激活(Magic Link 协议回跳)的回调,运行在后台线程,
        // 切回主窗口的 UI 线程处理。窗口可能尚未创建好,做 null 检查。
        private static void OnAppInstanceActivated(object? sender, AppActivationArguments args)
        {
            Trace($"OnAppInstanceActivated kind={args.Kind}");
            App.HandleRedirectedActivation(args);
        }

        // STA 主线程上直接 .Wait() 会与重定向所需的 COM 调用互相死锁;
        // 用 event + CoWaitForMultipleObjects 抽帧等待,这是 MS 官方
        // WinUI 3 单实例示例的标准做法。
        private static void RedirectActivationTo(
            AppActivationArguments args, AppInstance keyInstance)
        {
            IntPtr redirectEventHandle = CreateEvent(IntPtr.Zero, true, false, null);
            Task.Run(() =>
            {
                keyInstance.RedirectActivationToAsync(args).AsTask().Wait();
                SetEvent(redirectEventHandle);
            });

            const uint CWMO_DEFAULT = 0;
            const uint INFINITE = 0xFFFFFFFF;
            _ = CoWaitForMultipleObjects(
                CWMO_DEFAULT,
                INFINITE,
                1,
                new[] { redirectEventHandle },
                out _);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateEvent(
            IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetEvent(IntPtr hEvent);

        [DllImport("ole32.dll")]
        private static extern uint CoWaitForMultipleObjects(
            uint dwFlags, uint dwMilliseconds, ulong nHandles,
            IntPtr[] pHandles, out uint dwIndex);
    }
}
