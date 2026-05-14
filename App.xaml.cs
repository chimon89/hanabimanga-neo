using hanabimanga.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace hanabimanga
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

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

        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            Configuration = BuildConfiguration();

            var url = Configuration["Supabase:Url"];
            var anonKey = Configuration["Supabase:AnonKey"];

            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(anonKey))
            {
                System.Diagnostics.Debug.WriteLine(
                    "Supabase 配置缺失:请检查 appsettings.local.json 或环境变量 Supabase__Url / Supabase__AnonKey。");
            }
            else
            {
                try
                {
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
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Supabase 初始化失败: {ex}");
                }
            }

            _window = new MainWindow();
            _window.Activate();
        }

        private static IConfiguration BuildConfiguration()
        {
            return new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
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
