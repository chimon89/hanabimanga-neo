using hanabimanga.Services;
using hanabimanga.Pages;
using hanabimanga.Models;
using hanabimanga.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace hanabimanga
{
    public sealed partial class MainWindow : Window
    {
        private const string DefaultAvatarAssetName = "ic_avatar_default.webp";
        private static readonly string[] AvatarAssetExtensions = [".webp", ".png", ".jpg", ".jpeg"];

        private AppWindow? _appWindow;
        private ComicDetailPage? _currentDetailPage;
        private ComicReaderPage? _currentReaderPage;
        private ComicCommentsPage? _currentCommentsPage;
        private UserProfilePage? _currentUserProfilePage;
        private RecentReadingProgress? _recentReadingProgress;
        private bool _isContinueReadingBarDismissed;
        private bool _paneFooterCompact;
        private int _accountRefreshVersion;
        private bool _isSettingUpNotifications;
        private string? _activeNotificationSessionKey;
        private ContentDialog? _activeAuthDialog;

        public NotificationsViewModel NotificationsViewModel { get; } = new();

        private enum AuthDialogMode
        {
            SignIn,
            SignUp,
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        public MainWindow()
        {
            InitializeComponent();
            NotificationList.ItemsSource = NotificationsViewModel.Items;
            NotificationsViewModel.PropertyChanged += NotificationsViewModel_PropertyChanged;
            NotificationsViewModel.Items.CollectionChanged += NotificationsViewModelItems_CollectionChanged;
            UseGalleryStyleWindowFrame();
            RootFrame.Navigated += RootFrame_Navigated;
            NavigationRoot.SelectedItem = HomeNavigationItem;
            RootFrame.Navigate(typeof(HomePage));
            UpdateAccountFooter();
            UpdateNotificationVisualState();
            SupabaseService.Instance.EndpointChanged += SupabaseService_EndpointChanged;
            _ = RefreshAccountStateAfterSupabaseInitializationAsync();
        }

        // 接口线路实时切换后,客户端已重建:重置通知会话标识以强制重新订阅,
        // 并刷新账户区与继续阅读栏。事件可能来自后台线程,切回 UI 线程处理。
        private void SupabaseService_EndpointChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _activeNotificationSessionKey = null;
                UpdateAccountFooter();
                _ = RefreshContinueReadingBarAsync();
            });
        }

        private async Task RefreshAccountStateAfterSupabaseInitializationAsync()
        {
            await App.SupabaseInitialization;
            UpdateAccountFooter();
            WarmUpTaskCenterIfSignedIn();
            await RefreshContinueReadingBarAsync();
        }

        private void UseGalleryStyleWindowFrame()
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            var windowHandle = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            WindowIconService.ApplyTo(this, _appWindow);

            _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            ApplyCaptionButtonColors();
            if (Content is FrameworkElement rootElement)
            {
                rootElement.ActualThemeChanged += (_, _) => ApplyCaptionButtonColors();
            }
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: true);
                // PreferredMinimum* 单位是物理像素(非 DIP),按窗口 DPI 缩放,
                // 这样在 125%/150% 缩放下最小尺寸的有效 DIP 仍为 1280x720,
                // 避免窗口被缩到内容区放不下、提示被裁切。
                var dpi = GetDpiForWindow(windowHandle);
                var scale = dpi > 0 ? dpi / 96.0 : 1.0;
                presenter.PreferredMinimumWidth = (int)Math.Round(1280 * scale);
                presenter.PreferredMinimumHeight = (int)Math.Round(720 * scale);
            }
        }

        // 标题栏标题/最大化/关闭按钮的字形色需随主题显式设置,
        // 否则在亮色模式下与樱花粉标题栏对比度不足。
        private void ApplyCaptionButtonColors()
        {
            if (_appWindow == null) return;

            var titleBar = _appWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

            var isDark = Content is FrameworkElement root && root.ActualTheme == ElementTheme.Dark;
            if (isDark)
            {
                titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xF7, 0xFA);
                titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0xC9, 0x97, 0xA9);
            }
            else
            {
                titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0x2B, 0x14, 0x20);
                titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0x80, 0x56, 0x6A);
            }
        }

        private void AppTitleBar_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateTitleBarInteractiveRegions();
        }

        private void AppTitleBar_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateTitleBarInteractiveRegions();
        }

        private void UpdateTitleBarInteractiveRegions()
        {
            if (_appWindow == null || AppTitleBar.XamlRoot == null) return;

            var scaleAdjustment = AppTitleBar.XamlRoot.RasterizationScale;
            if (scaleAdjustment <= 0) return;

            LeftPaddingColumn.Width = new GridLength(_appWindow.TitleBar.LeftInset / scaleAdjustment);
            RightPaddingColumn.Width = new GridLength(_appWindow.TitleBar.RightInset / scaleAdjustment);

            var nonClientInputSource = InputNonClientPointerSource.GetForWindowId(_appWindow.Id);
            var passthroughRects = new List<RectInt32>();

            AddPassthroughRegion(passthroughRects, PaneToggleButton, scaleAdjustment);
            AddPassthroughRegion(passthroughRects, NotificationButton, scaleAdjustment);
            AddPassthroughRegion(passthroughRects, DetailTitleBarBackButton, scaleAdjustment);
            AddPassthroughRegion(passthroughRects, DetailTitleBarHomeButton, scaleAdjustment);

            nonClientInputSource.SetRegionRects(NonClientRegionKind.Passthrough, passthroughRects.ToArray());
        }

        private static void AddPassthroughRegion(
            List<RectInt32> passthroughRects,
            FrameworkElement element,
            double scale)
        {
            if (element.Visibility != Visibility.Visible ||
                element.ActualWidth < 1 ||
                element.ActualHeight < 1)
            {
                return;
            }

            var transform = element.TransformToVisual(null);
            var bounds = transform.TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            if (bounds.Width < 1 || bounds.Height < 1) return;

            passthroughRects.Add(ToRectInt32(bounds, scale));
        }

        private static RectInt32 ToRectInt32(Rect bounds, double scale)
        {
            return new RectInt32(
                _X: (int)Math.Round(bounds.X * scale),
                _Y: (int)Math.Round(bounds.Y * scale),
                _Width: (int)Math.Round(bounds.Width * scale),
                _Height: (int)Math.Round(bounds.Height * scale));
        }

        private void RootFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            if (_currentDetailPage != null)
            {
                _currentDetailPage.TitleBarInfoChanged -= DetailPage_TitleBarInfoChanged;
                _currentDetailPage = null;
            }
            if (_currentReaderPage != null)
            {
                _currentReaderPage.TitleBarInfoChanged -= ReaderPage_TitleBarInfoChanged;
                _currentReaderPage.ReadingProgressChanged -= ReaderPage_ReadingProgressChanged;
                _currentReaderPage = null;
            }
            if (_currentCommentsPage != null)
            {
                _currentCommentsPage.TitleBarInfoChanged -= CommentsPage_TitleBarInfoChanged;
                _currentCommentsPage = null;
            }
            if (_currentUserProfilePage != null)
            {
                _currentUserProfilePage.TitleBarInfoChanged -= UserProfilePage_TitleBarInfoChanged;
                _currentUserProfilePage = null;
            }

            if (RootFrame.Content is ComicDetailPage detailPage)
            {
                _currentDetailPage = detailPage;
                _currentDetailPage.TitleBarInfoChanged += DetailPage_TitleBarInfoChanged;
                ShowDetailTitleBar(detailPage);
            }
            else if (RootFrame.Content is ComicCommentsPage commentsPage)
            {
                _currentCommentsPage = commentsPage;
                _currentCommentsPage.TitleBarInfoChanged += CommentsPage_TitleBarInfoChanged;
                ShowCommentsTitleBar(commentsPage);
            }
            else if (RootFrame.Content is UserProfilePage userProfilePage)
            {
                _currentUserProfilePage = userProfilePage;
                _currentUserProfilePage.TitleBarInfoChanged += UserProfilePage_TitleBarInfoChanged;
                ShowUserProfileTitleBar(userProfilePage);
            }
            else if (RootFrame.Content is ComicReaderPage readerPage)
            {
                _currentReaderPage = readerPage;
                _currentReaderPage.TitleBarInfoChanged += ReaderPage_TitleBarInfoChanged;
                _currentReaderPage.ReadingProgressChanged += ReaderPage_ReadingProgressChanged;
                ShowReaderTitleBar(readerPage);
            }
            else
            {
                ShowDefaultTitleBar();
                if (RootFrame.Content is HomePage)
                {
                    NavigationRoot.SelectedItem = HomeNavigationItem;
                }
                else if (RootFrame.Content is DiscoverPage)
                {
                    NavigationRoot.SelectedItem = DiscoverNavigationItem;
                }
                else if (RootFrame.Content is CategoryPage)
                {
                    NavigationRoot.SelectedItem = CategoryNavigationItem;
                }
                else if (RootFrame.Content is BookshelfPage)
                {
                    NavigationRoot.SelectedItem = BookshelfNavigationItem;
                }
                else if (RootFrame.Content is RankingPage)
                {
                    NavigationRoot.SelectedItem = RankingNavigationItem;
                }
                else if (RootFrame.Content is TaskCenterPage ||
                    RootFrame.Content is PointsDetailPage ||
                    RootFrame.Content is PointsStorePage ||
                    RootFrame.Content is InvitePage ||
                    RootFrame.Content is ExchangeHistoryPage)
                {
                    NavigationRoot.SelectedItem = TaskCenterNavigationItem;
                }
                else if (RootFrame.Content is AppSettingsPage)
                {
                    NavigationRoot.SelectedItem = AppSettingsNavigationItem;
                }
                else if (RootFrame.Content is UserSettingsPage)
                {
                    NavigationRoot.SelectedItem = null;
                }
                else if (RootFrame.Content is FeedbackListPage or FeedbackSubmitPage or FeedbackDetailPage)
                {
                    NavigationRoot.SelectedItem = null;
                }
            }

            _ = RefreshContinueReadingBarAsync();
        }

        private void NavigationRoot_SelectionChanged(
            NavigationView sender,
            NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is not NavigationViewItem item ||
                item.Tag is not string tag)
            {
                return;
            }

            switch (tag)
            {
                case "home" when RootFrame.Content is not HomePage:
                    RootFrame.Navigate(typeof(HomePage));
                    RootFrame.BackStack.Clear();
                    break;
                case "discover" when RootFrame.Content is not DiscoverPage:
                    RootFrame.Navigate(typeof(DiscoverPage));
                    break;
                case "category" when RootFrame.Content is not CategoryPage:
                    RootFrame.Navigate(typeof(CategoryPage));
                    break;
                case "bookshelf" when RootFrame.Content is not BookshelfPage:
                    RootFrame.Navigate(typeof(BookshelfPage));
                    break;
                case "ranking" when RootFrame.Content is not RankingPage:
                    RootFrame.Navigate(typeof(RankingPage));
                    break;
                case "task-center" when !CanUseTaskCenter():
                    ShowAccountFlyout();
                    NavigationRoot.SelectedItem = null;
                    break;
                case "task-center" when RootFrame.Content is not TaskCenterPage &&
                    RootFrame.Content is not PointsDetailPage &&
                    RootFrame.Content is not PointsStorePage &&
                    RootFrame.Content is not InvitePage &&
                    RootFrame.Content is not ExchangeHistoryPage:
                    RootFrame.Navigate(typeof(TaskCenterPage));
                    break;
                case "app-settings" when RootFrame.Content is not AppSettingsPage:
                    RootFrame.Navigate(typeof(AppSettingsPage));
                    break;
            }
        }

        // 列表页(收藏 / 点赞)未登录态触发左下角账户 Flyout
        public void ShowAccountFlyout()
        {
            AccountFlyout.ShowAt(AccountFooterButton);
        }

        public void RefreshAccountDisplay()
        {
            UpdateAccountFooter();
        }

        private static async Task<bool> EnsureSupabaseInitializedAsync()
        {
            await App.SupabaseInitialization;
            return SupabaseService.Instance.IsInitialized;
        }

        // 在「尚未初始化」提示后追加真实失败原因,便于排查打包/环境问题。
        private static string AppendSupabaseError(string baseMessage)
        {
            var error = App.SupabaseInitializationError;
            return string.IsNullOrWhiteSpace(error)
                ? baseMessage
                : $"{baseMessage}\n\n失败原因:{error}";
        }

        private void ReaderPage_TitleBarInfoChanged(object? sender, EventArgs e)
        {
            if (sender is ComicReaderPage readerPage)
            {
                ShowReaderTitleBar(readerPage);
            }
        }

        private void ReaderPage_ReadingProgressChanged(object? sender, EventArgs e)
        {
            _ = RefreshContinueReadingBarAsync();
        }

        private void DetailPage_TitleBarInfoChanged(object? sender, EventArgs e)
        {
            if (sender is ComicDetailPage detailPage)
            {
                ShowDetailTitleBar(detailPage);
            }
        }

        private void CommentsPage_TitleBarInfoChanged(object? sender, EventArgs e)
        {
            if (sender is ComicCommentsPage commentsPage)
            {
                ShowCommentsTitleBar(commentsPage);
            }
        }

        private void UserProfilePage_TitleBarInfoChanged(object? sender, EventArgs e)
        {
            if (sender is UserProfilePage userProfilePage)
            {
                ShowUserProfileTitleBar(userProfilePage);
            }
        }

        private void ShowDefaultTitleBar()
        {
            TitleBarLogoImage.Visibility = Visibility.Visible;
            TitleBarAppNameTextBlock.Visibility = Visibility.Visible;
            DetailTitleBarBreadcrumb.Visibility = Visibility.Collapsed;
            AppTitleBar.UpdateLayout();
            UpdateTitleBarInteractiveRegions();
        }

        private void ShowDetailTitleBar(ComicDetailPage detailPage)
        {
            TitleBarLogoImage.Visibility = Visibility.Collapsed;
            TitleBarAppNameTextBlock.Visibility = Visibility.Collapsed;
            DetailTitleBarBreadcrumb.Visibility = Visibility.Visible;

            DetailTitleBarCategoryTextBlock.Text = detailPage.TitleBarCategory;
            DetailTitleBarTitleTextBlock.Text = detailPage.TitleBarTitle;
            AppTitleBar.UpdateLayout();
            UpdateTitleBarInteractiveRegions();
        }

        private void ShowCommentsTitleBar(ComicCommentsPage commentsPage)
        {
            TitleBarLogoImage.Visibility = Visibility.Collapsed;
            TitleBarAppNameTextBlock.Visibility = Visibility.Collapsed;
            DetailTitleBarBreadcrumb.Visibility = Visibility.Visible;

            DetailTitleBarCategoryTextBlock.Text = commentsPage.TitleBarCategory;
            DetailTitleBarTitleTextBlock.Text = commentsPage.TitleBarTitle;
            AppTitleBar.UpdateLayout();
            UpdateTitleBarInteractiveRegions();
        }

        private void ShowUserProfileTitleBar(UserProfilePage userProfilePage)
        {
            TitleBarLogoImage.Visibility = Visibility.Collapsed;
            TitleBarAppNameTextBlock.Visibility = Visibility.Collapsed;
            DetailTitleBarBreadcrumb.Visibility = Visibility.Visible;

            DetailTitleBarCategoryTextBlock.Text = userProfilePage.TitleBarCategory;
            DetailTitleBarTitleTextBlock.Text = userProfilePage.TitleBarTitle;
            AppTitleBar.UpdateLayout();
            UpdateTitleBarInteractiveRegions();
        }

        private void ShowReaderTitleBar(ComicReaderPage readerPage)
        {
            TitleBarLogoImage.Visibility = Visibility.Collapsed;
            TitleBarAppNameTextBlock.Visibility = Visibility.Collapsed;
            DetailTitleBarBreadcrumb.Visibility = Visibility.Visible;

            DetailTitleBarCategoryTextBlock.Text = readerPage.TitleBarCategory;
            DetailTitleBarTitleTextBlock.Text = readerPage.TitleBarTitle;
            AppTitleBar.UpdateLayout();
            UpdateTitleBarInteractiveRegions();
        }

        private void DetailTitleBarBackButton_Click(object sender, RoutedEventArgs e)
        {
            if (RootFrame.CanGoBack)
            {
                RootFrame.GoBack();
                return;
            }

            NavigateHomeFromTitleBar();
        }

        private void DetailTitleBarHomeButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateHomeFromTitleBar();
        }

        private void NavigateHomeFromTitleBar()
        {
            if (RootFrame.Content is not HomePage)
            {
                RootFrame.Navigate(typeof(HomePage));
            }

            RootFrame.BackStack.Clear();
            NavigationRoot.SelectedItem = HomeNavigationItem;
        }

        private async Task RefreshContinueReadingBarAsync()
        {
            await App.SupabaseInitialization;

            if (_isContinueReadingBarDismissed ||
                RootFrame.Content is ComicReaderPage ||
                !SupabaseService.Instance.IsInitialized ||
                string.IsNullOrWhiteSpace(SupabaseService.Instance.CurrentSession?.AccessToken))
            {
                HideContinueReadingBar();
                return;
            }

            try
            {
                _recentReadingProgress = await SupabaseService.Instance.GetRecentReadingProgressAsync();
                if (_recentReadingProgress == null)
                {
                    HideContinueReadingBar();
                    return;
                }

                ContinueReadingTitleTextBlock.Text = _recentReadingProgress.ComicTitle;
                ContinueReadingSubtitleTextBlock.Text = string.IsNullOrWhiteSpace(_recentReadingProgress.ProgressText)
                    ? _recentReadingProgress.ChapterTitle
                    : $"{_recentReadingProgress.ChapterTitle} · {_recentReadingProgress.ProgressText}";
                ContinueReadingCoverImage.Source = !string.IsNullOrWhiteSpace(_recentReadingProgress.CoverUrl)
                    ? new BitmapImage(new Uri(_recentReadingProgress.CoverUrl))
                    : null;
                ContinueReadingBar.Visibility = Visibility.Visible;
                UpdatePaneFooterLayout();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[reading-history] refresh failed: {ex.Message}");
                HideContinueReadingBar();
            }
        }

        private void HideContinueReadingBar()
        {
            ContinueReadingBar.Visibility = Visibility.Collapsed;
            ContinueReadingCoverImage.Source = null;
        }

        private void ContinueReadingButton_Click(object sender, RoutedEventArgs e)
        {
            if (_recentReadingProgress == null) return;

            RootFrame.Navigate(typeof(ComicReaderPage), new ComicReaderNavigationParameter
            {
                ComicId = _recentReadingProgress.ComicId,
                ChapterId = _recentReadingProgress.ChapterId,
                StartPage = Math.Max(_recentReadingProgress.PageIndex, 1),
            });
        }

        private void DismissContinueReadingBar_Click(object sender, RoutedEventArgs e)
        {
            _isContinueReadingBarDismissed = true;
            HideContinueReadingBar();
        }

        private void ContinueReadingBar_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_paneFooterCompact) return;
            ContinueReadingBar.Background = (Brush)Application.Current.Resources["ContinueReadingBarPointerOverBackgroundBrush"];
        }

        private void ContinueReadingBar_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_paneFooterCompact) return;
            ContinueReadingBar.Background = (Brush)Application.Current.Resources["ContinueReadingBarBackgroundBrush"];
        }

        private void PaneToggleButton_Click(object sender, RoutedEventArgs e)
        {
            NavigationRoot.IsPaneOpen = !NavigationRoot.IsPaneOpen;
            UpdatePaneFooterLayout();
        }

        private void NavigationRoot_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
        {
            UpdatePaneFooterLayout();
        }

        private void AccountFooterButton_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdatePaneFooterLayout();
        }

        private async void SignInMenuButton_Click(object sender, RoutedEventArgs e)
        {
            AccountFlyout.Hide();
            await ShowAuthDialogAsync(AuthDialogMode.SignIn);
        }

        private async void SignUpMenuButton_Click(object sender, RoutedEventArgs e)
        {
            AccountFlyout.Hide();
            await ShowAuthDialogAsync(AuthDialogMode.SignUp);
        }

        private async void MagicLinkMenuButton_Click(object sender, RoutedEventArgs e)
        {
            AccountFlyout.Hide();
            await ShowMagicLinkDialogAsync();
        }

        private void ViewAccountButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateUserSettings();
        }

        private void ViewMyProfileButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateCurrentUserProfile();
        }

        private void TaskCenterButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateTaskCenter();
        }

        private void FeedbackButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateFeedback();
        }

        private void NavigateCurrentUserProfile()
        {
            AccountFlyout.Hide();

            var userId = SupabaseService.Instance.CurrentUser?.Id;
            if (string.IsNullOrWhiteSpace(userId))
            {
                userId = ReadJwtUserClaims(SupabaseService.Instance.CurrentSession?.AccessToken).UserId;
            }

            if (string.IsNullOrWhiteSpace(userId)) return;

            RootFrame.Navigate(typeof(UserProfilePage), userId);
        }

        private void NavigateUserSettings()
        {
            AccountFlyout.Hide();
            if (RootFrame.Content is not UserSettingsPage)
            {
                RootFrame.Navigate(typeof(UserSettingsPage));
            }
        }

        private void NavigateTaskCenter()
        {
            AccountFlyout.Hide();
            if (!CanUseTaskCenter())
            {
                ShowAccountFlyout();
                return;
            }

            if (RootFrame.Content is not TaskCenterPage)
            {
                RootFrame.Navigate(typeof(TaskCenterPage));
            }
        }

        private void NavigateFeedback()
        {
            AccountFlyout.Hide();
            if (RootFrame.Content is not FeedbackListPage)
            {
                RootFrame.Navigate(typeof(FeedbackListPage));
            }
        }

        private async void SignOutMenuButton_Click(object sender, RoutedEventArgs e)
        {
            AccountFlyout.Hide();

            if (!await EnsureSupabaseInitializedAsync())
            {
                await ShowMessageDialogAsync(
                    "认证不可用",
                    AppendSupabaseError("Supabase 尚未初始化,请检查本地配置。"));
                return;
            }

            SignOutMenuButton.IsEnabled = false;
            try
            {
                await SupabaseService.Instance.SignOutAsync();
                _recentReadingProgress = null;
                _isContinueReadingBarDismissed = false;
                HideContinueReadingBar();
                UpdateAccountFooter();
            }
            catch (Exception ex)
            {
                await ShowMessageDialogAsync("退出登录失败", ex.Message);
            }
            finally
            {
                SignOutMenuButton.IsEnabled = true;
            }
        }

        private async Task ShowAuthDialogAsync(AuthDialogMode mode)
        {
            if (!await EnsureSupabaseInitializedAsync())
            {
                await ShowMessageDialogAsync(
                    "认证不可用",
                    AppendSupabaseError("Supabase 尚未初始化,请检查 appsettings.local.json 或环境变量。"));
                return;
            }

            var emailBox = new TextBox
            {
                Header = "邮箱",
                PlaceholderText = "name@example.com",
            };

            var passwordBox = new PasswordBox
            {
                Header = "密码",
                PlaceholderText = mode == AuthDialogMode.SignUp ? "至少 6 位字符" : "输入密码",
            };
            PasswordBox? confirmPasswordBox = null;
            TextBox? userIdBox = null;
            TextBox? nicknameBox = null;
            TextBox? inviteCodeBox = null;
            TextBlock? usernameStatusTextBlock = null;
            CheckBox? termsCheckBox = null;
            DispatcherTimer? usernameCheckTimer = null;
            UsernameCheckResult? lastUsernameCheckResult = null;
            string lastUsernameCheckValue = "";
            var usernameCheckVersion = 0;

            var feedbackBar = new InfoBar
            {
                IsOpen = false,
                IsClosable = false,
            };

            var contentPanel = new StackPanel
            {
                Spacing = 12,
                MinWidth = 320,
            };
            contentPanel.Children.Add(emailBox);
            contentPanel.Children.Add(passwordBox);

            if (mode == AuthDialogMode.SignUp)
            {
                confirmPasswordBox = new PasswordBox
                {
                    Header = "确认密码",
                    PlaceholderText = "再次输入至少 6 位字符",
                };
                userIdBox = new TextBox
                {
                    Header = "用户名",
                    PlaceholderText = "3-20 个字符",
                };
                usernameStatusTextBlock = new TextBlock
                {
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemedBrush("TextFillColorSecondaryBrush"),
                };
                nicknameBox = new TextBox
                {
                    Header = "昵称（选填）",
                    PlaceholderText = "展示昵称",
                };
                inviteCodeBox = new TextBox
                {
                    Header = "邀请码（选填）",
                    PlaceholderText = "填入可获 3 天 VIP",
                };
                termsCheckBox = new CheckBox
                {
                    Content = "我已阅读并同意用户协议和隐私政策",
                };

                usernameCheckTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(500),
                };

                contentPanel.Children.Add(confirmPasswordBox);
                contentPanel.Children.Add(userIdBox);
                contentPanel.Children.Add(usernameStatusTextBlock);
                contentPanel.Children.Add(nicknameBox);
                contentPanel.Children.Add(inviteCodeBox);
                contentPanel.Children.Add(termsCheckBox);
                contentPanel.Children.Add(new TextBlock
                {
                    Text = "注册后可登录账号,邮箱验证在个人资料页完成。",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemedBrush("TextFillColorSecondaryBrush"),
                });
            }
            else
            {
                var magicLinkButton = new HyperlinkButton
                {
                    Content = "发送 Magic Link",
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(0),
                };
                magicLinkButton.Click += async (_, _) =>
                {
                    if (!TryValidateEmail(emailBox.Text, out var validationMessage))
                    {
                        ShowFeedback(feedbackBar, InfoBarSeverity.Warning, "邮箱格式不正确", validationMessage);
                        return;
                    }

                    magicLinkButton.IsEnabled = false;
                    try
                    {
                        await SupabaseService.Instance.SendMagicLinkAsync(emailBox.Text.Trim());
                        ShowFeedback(feedbackBar, InfoBarSeverity.Success, "Magic Link 已发送", "请打开邮箱中的链接完成登录。");
                    }
                    catch (Exception ex)
                    {
                        ShowFeedback(feedbackBar, InfoBarSeverity.Error, "发送失败", ex.Message);
                    }
                    finally
                    {
                        magicLinkButton.IsEnabled = true;
                    }
                };

                contentPanel.Children.Add(magicLinkButton);
            }

            void SetUsernameStatus(string message)
            {
                if (usernameStatusTextBlock == null) return;
                usernameStatusTextBlock.Text = message;
                usernameStatusTextBlock.Visibility = string.IsNullOrWhiteSpace(message)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            async Task<UsernameCheckResult?> CheckUsernameAvailabilityForSignUpAsync(bool force)
            {
                if (mode != AuthDialogMode.SignUp || userIdBox == null)
                {
                    return null;
                }

                var username = userIdBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(username))
                {
                    lastUsernameCheckValue = "";
                    lastUsernameCheckResult = null;
                    SetUsernameStatus("");
                    return null;
                }

                if (!TryValidateUsername(username, out var validationMessage))
                {
                    lastUsernameCheckValue = "";
                    lastUsernameCheckResult = null;
                    SetUsernameStatus(validationMessage);
                    return new UsernameCheckResult
                    {
                        Available = false,
                        Reason = "length",
                    };
                }

                if (!force &&
                    string.Equals(lastUsernameCheckValue, username, StringComparison.Ordinal) &&
                    lastUsernameCheckResult != null)
                {
                    return lastUsernameCheckResult;
                }

                var requestVersion = ++usernameCheckVersion;
                SetUsernameStatus("正在检查用户名...");
                try
                {
                    var result = await SupabaseService.Instance.CheckUsernameAvailableAsync(username);
                    if (requestVersion != usernameCheckVersion ||
                        !string.Equals(userIdBox.Text.Trim(), username, StringComparison.Ordinal))
                    {
                        return result;
                    }

                    lastUsernameCheckValue = username;
                    lastUsernameCheckResult = result;
                    SetUsernameStatus(result.Available
                        ? "用户名可用"
                        : FormatUsernameUnavailableMessage(result.Reason));
                    return result;
                }
                catch (Exception ex)
                {
                    if (requestVersion == usernameCheckVersion)
                    {
                        lastUsernameCheckValue = username;
                        lastUsernameCheckResult = new UsernameCheckResult
                        {
                            Available = false,
                            Reason = "check_failed",
                        };
                        SetUsernameStatus("用户名检查失败,请稍后再试。");
                    }

                    System.Diagnostics.Debug.WriteLine($"[auth-signup] username check failed: {ex.Message}");
                    return lastUsernameCheckResult;
                }
            }

            if (mode == AuthDialogMode.SignUp && userIdBox != null && usernameCheckTimer != null)
            {
                usernameStatusTextBlock!.Visibility = Visibility.Collapsed;
                usernameCheckTimer.Tick += async (_, _) =>
                {
                    usernameCheckTimer.Stop();
                    await CheckUsernameAvailabilityForSignUpAsync(force: false);
                };
                userIdBox.TextChanged += (_, _) =>
                {
                    usernameCheckTimer.Stop();
                    usernameCheckVersion++;
                    lastUsernameCheckValue = "";
                    lastUsernameCheckResult = null;

                    var username = userIdBox.Text.Trim();
                    if (string.IsNullOrWhiteSpace(username))
                    {
                        SetUsernameStatus("");
                    }
                    else if (!TryValidateUsername(username, out var validationMessage))
                    {
                        SetUsernameStatus(validationMessage);
                    }
                    else
                    {
                        SetUsernameStatus("停止输入后检查用户名...");
                        usernameCheckTimer.Start();
                    }
                };
                userIdBox.LostFocus += async (_, _) =>
                {
                    usernameCheckTimer.Stop();
                    await CheckUsernameAvailabilityForSignUpAsync(force: true);
                };
            }

            contentPanel.Children.Add(feedbackBar);

            var completed = false;
            var dialog = new ContentDialog
            {
                XamlRoot = (Content as FrameworkElement)?.XamlRoot,
                RequestedTheme = (Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
                Title = mode == AuthDialogMode.SignIn ? "登录花火漫画" : "注册花火漫画",
                PrimaryButtonText = mode == AuthDialogMode.SignIn ? "登录" : "注册",
                SecondaryButtonText = mode == AuthDialogMode.SignIn ? "去注册" : "去登录",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                Content = contentPanel,
            };
            SyncDialogThemeOnOpen(dialog);

            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                dialog.IsPrimaryButtonEnabled = false;
                try
                {
                    if (mode == AuthDialogMode.SignIn)
                    {
                        if (!TryValidateSignInInput(emailBox.Text, passwordBox.Password, out var validationMessage))
                        {
                            ShowFeedback(feedbackBar, InfoBarSeverity.Warning, "请检查输入", validationMessage);
                            args.Cancel = true;
                            return;
                        }
                    }
                    else if (!TryValidateSignUpInput(
                                 emailBox.Text,
                                 passwordBox.Password,
                                 confirmPasswordBox?.Password ?? "",
                                 userIdBox?.Text ?? "",
                                 nicknameBox?.Text ?? "",
                                 inviteCodeBox?.Text ?? "",
                                 termsCheckBox?.IsChecked == true,
                                 out var validationMessage))
                    {
                        ShowFeedback(feedbackBar, InfoBarSeverity.Warning, "请检查输入", validationMessage);
                        args.Cancel = true;
                        return;
                    }

                    if (mode == AuthDialogMode.SignIn)
                    {
                        await SupabaseService.Instance.SignInAsync(emailBox.Text.Trim(), passwordBox.Password);
                    }
                    else
                    {
                        var usernameCheck = await CheckUsernameAvailabilityForSignUpAsync(force: true);
                        if (usernameCheck?.Available != true)
                        {
                            ShowFeedback(
                                feedbackBar,
                                InfoBarSeverity.Warning,
                                "用户名不可用",
                                FormatUsernameUnavailableMessage(usernameCheck?.Reason));
                            userIdBox?.Focus(FocusState.Programmatic);
                            args.Cancel = true;
                            return;
                        }

                        StorePendingInviteCode(inviteCodeBox?.Text);
                        await SupabaseService.Instance.SignUpAsync(
                            emailBox.Text.Trim(),
                            passwordBox.Password,
                            userIdBox?.Text.Trim() ?? "",
                            nicknameBox?.Text.Trim(),
                            inviteCodeBox?.Text.Trim());
                    }

                    completed = true;
                }
                catch (Exception ex)
                {
                    ShowFeedback(feedbackBar, InfoBarSeverity.Error, "操作失败", ex.Message);
                    if (IsUsernameTakenMessage(ex.Message))
                    {
                        userIdBox?.Focus(FocusState.Programmatic);
                    }
                    args.Cancel = true;
                }
                finally
                {
                    dialog.IsPrimaryButtonEnabled = true;
                    deferral.Complete();
                }
            };

            _activeAuthDialog = dialog;
            ContentDialogResult result;
            try
            {
                result = await dialog.ShowAsync();
            }
            finally
            {
                if (ReferenceEquals(_activeAuthDialog, dialog))
                {
                    _activeAuthDialog = null;
                }
            }

            if (result == ContentDialogResult.Secondary)
            {
                await ShowAuthDialogAsync(
                    mode == AuthDialogMode.SignIn ? AuthDialogMode.SignUp : AuthDialogMode.SignIn);
                return;
            }

            if (!completed) return;

            _isContinueReadingBarDismissed = false;
            UpdateAccountFooter();
            WarmUpTaskCenterIfSignedIn();
            _ = RefreshContinueReadingBarAsync();
            if (mode == AuthDialogMode.SignUp)
            {
                await ShowMessageDialogAsync("注册成功", "注册成功。");
            }
        }

        private async Task ShowMagicLinkDialogAsync()
        {
            if (!await EnsureSupabaseInitializedAsync())
            {
                await ShowMessageDialogAsync(
                    "认证不可用",
                    AppendSupabaseError("Supabase 尚未初始化,请检查 appsettings.local.json 或环境变量。"));
                return;
            }

            var emailBox = new TextBox
            {
                Header = "邮箱",
                PlaceholderText = "name@example.com",
            };

            var feedbackBar = new InfoBar
            {
                IsOpen = false,
                IsClosable = false,
            };

            var contentPanel = new StackPanel
            {
                Spacing = 12,
                MinWidth = 320,
            };
            contentPanel.Children.Add(emailBox);
            contentPanel.Children.Add(new TextBlock
            {
                Text = "我们会发送一封一次性登录邮件。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            contentPanel.Children.Add(feedbackBar);

            var dialog = new ContentDialog
            {
                XamlRoot = (Content as FrameworkElement)?.XamlRoot,
                RequestedTheme = (Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
                Title = "Magic Link 登录",
                PrimaryButtonText = "发送邮件",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                Content = contentPanel,
            };
            SyncDialogThemeOnOpen(dialog);

            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                dialog.IsPrimaryButtonEnabled = false;
                try
                {
                    if (!TryValidateEmail(emailBox.Text, out var validationMessage))
                    {
                        ShowFeedback(feedbackBar, InfoBarSeverity.Warning, "邮箱格式不正确", validationMessage);
                        args.Cancel = true;
                        return;
                    }

                    await SupabaseService.Instance.SendMagicLinkAsync(emailBox.Text.Trim());
                    ShowFeedback(feedbackBar, InfoBarSeverity.Success, "Magic Link 已发送", "请打开邮箱中的链接完成登录。");
                    args.Cancel = true;
                }
                catch (Exception ex)
                {
                    ShowFeedback(feedbackBar, InfoBarSeverity.Error, "发送失败", ex.Message);
                    args.Cancel = true;
                }
                finally
                {
                    dialog.IsPrimaryButtonEnabled = true;
                    deferral.Complete();
                }
            };

            _activeAuthDialog = dialog;
            try
            {
                await dialog.ShowAsync();
            }
            finally
            {
                if (ReferenceEquals(_activeAuthDialog, dialog))
                {
                    _activeAuthDialog = null;
                }
            }
        }

        /// <summary>
        /// 处理 Magic Link 邮件回跳(hanabimanga://auth/callback):完成登录并刷新账户区。
        /// 由 App 的协议激活路由在 UI 线程上调用。
        /// </summary>
        public async Task HandleMagicLinkCallbackAsync(Uri uri)
        {
            BringToForeground();

            // 回跳时「发送邮件」对话框可能仍开着;同一时刻只能存在一个 ContentDialog,
            // 先关掉它再弹结果提示,否则 ShowAsync 会抛 COMException。
            _activeAuthDialog?.Hide();

            if (!await EnsureSupabaseInitializedAsync())
            {
                await ShowMessageDialogAsync(
                    "认证不可用",
                    AppendSupabaseError("Supabase 尚未初始化,无法完成 Magic Link 登录。"));
                return;
            }

            try
            {
                await SupabaseService.Instance.CompleteMagicLinkAsync(uri);
            }
            catch (Exception ex)
            {
                await ShowMessageDialogAsync("登录失败", ex.Message);
                return;
            }

            UpdateAccountFooter();
            WarmUpTaskCenterIfSignedIn();
            await ShowMessageDialogAsync("登录成功", "已通过邮件链接完成登录。");
        }

        // 从邮件回跳激活时,把窗口从最小化/后台恢复到前台。
        private void BringToForeground()
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }

        private void UpdateAccountFooter()
        {
            var refreshVersion = ++_accountRefreshVersion;
            var accessToken = SupabaseService.Instance.IsInitialized
                ? SupabaseService.Instance.CurrentSession?.AccessToken
                : null;
            var isSignedIn = !string.IsNullOrWhiteSpace(accessToken);

            TaskCenterNavigationItem.IsEnabled = isSignedIn;

            SignedOutAccountActionsPanel.Visibility = isSignedIn ? Visibility.Collapsed : Visibility.Visible;
            SignedInAccountActionsPanel.Visibility = isSignedIn ? Visibility.Visible : Visibility.Collapsed;

            if (isSignedIn)
            {
                var claims = ReadJwtUserClaims(accessToken);
                var displayName = string.IsNullOrWhiteSpace(claims.Email) ? "已登录用户" : claims.Email;
                var status = "会话已同步";

                ApplyAccountDisplay(
                    displayName,
                    status,
                    null,
                    SupabaseService.Instance.IsCurrentUserEmailVerified);

                if (!string.IsNullOrWhiteSpace(claims.UserId))
                {
                    _ = LoadAccountProfileAsync(claims.UserId, claims.Email, refreshVersion);
                }

                NotificationButton.Visibility = Visibility.Visible;
                _ = SetupNotificationsAsync(accessToken!, claims.UserId);
                WarmUpTaskCenterIfSignedIn();
            }
            else
            {
                TaskCenterService.Instance.ClearCache();
                ApplyAccountDisplay("登录", "同步收藏和阅读进度", null, false);
                AccountFlyoutNameTextBlock.Text = "未登录";
                AccountFlyoutStatusTextBlock.Text = "登录后同步收藏和阅读进度";
                AccountPersonPicture.DisplayName = "";
                AccountFlyoutPersonPicture.DisplayName = "";

                NotificationButton.Visibility = Visibility.Collapsed;
                _activeNotificationSessionKey = null;
                SupabaseService.Instance.UnsubscribeNotifications();
                NotificationsViewModel.Clear();

                if (IsLoginRequiredContent(RootFrame.Content))
                {
                    RootFrame.Navigate(typeof(HomePage));
                    RootFrame.BackStack.Clear();
                    NavigationRoot.SelectedItem = HomeNavigationItem;
                }
            }

            UpdateNotificationVisualState();
            AppTitleBar.UpdateLayout();
            UpdateTitleBarInteractiveRegions();
            UpdatePaneFooterLayout();
        }

        private static bool CanUseTaskCenter()
            => SupabaseService.Instance.IsInitialized && SupabaseService.Instance.IsSignedIn;

        private static bool IsTaskCenterContent(object? content)
            => content is TaskCenterPage or PointsDetailPage or PointsStorePage or InvitePage or ExchangeHistoryPage;

        // 退出登录后,这些页面失去前置条件,统一退回主页避免显示陈旧数据或报错。
        private static bool IsLoginRequiredContent(object? content)
        {
            if (IsTaskCenterContent(content)) return true;
            if (content is UserSettingsPage) return true;
            if (content is FeedbackListPage or FeedbackSubmitPage or FeedbackDetailPage) return true;
            if (content is UserProfilePage profilePage && profilePage.ViewModel.IsSelf) return true;
            return false;
        }

        private static void WarmUpTaskCenterIfSignedIn()
        {
            if (!SupabaseService.Instance.IsInitialized || !SupabaseService.Instance.IsSignedIn)
            {
                return;
            }

            _ = TaskCenterService.Instance.PreloadAsync();
        }

        private async Task SetupNotificationsAsync(string accessToken, string? userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return;

            var sessionKey = $"{userId}:{accessToken}";
            if (_activeNotificationSessionKey == sessionKey || _isSettingUpNotifications)
            {
                return;
            }

            _isSettingUpNotifications = true;
            try
            {
                await NotificationsViewModel.LoadAsync();
                await SupabaseService.Instance.SubscribeNotificationsAsync(OnNotificationReceived);
                _activeNotificationSessionKey = sessionKey;
            }
            catch (Exception ex)
            {
                _activeNotificationSessionKey = null;
                System.Diagnostics.Debug.WriteLine($"[notifications] setup failed: {ex.Message}");
            }
            finally
            {
                _isSettingUpNotifications = false;
                UpdateNotificationVisualState();
            }
        }

        // Realtime 回调可能在后台线程,切回 UI 线程更新通知列表。
        private void OnNotificationReceived(NotificationItem item)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                NotificationsViewModel.AddRealtime(item);
                UpdateNotificationVisualState();
            });
        }

        private async void NotificationFlyout_Opened(object sender, object e)
        {
            await NotificationsViewModel.LoadAsync();
            UpdateNotificationVisualState();
        }

        private async void MarkAllNotificationsRead_Click(object sender, RoutedEventArgs e)
        {
            await NotificationsViewModel.MarkAllReadAsync();
            UpdateNotificationVisualState();
        }

        private async void NotificationItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is NotificationItem item)
            {
                await NotificationsViewModel.MarkReadAsync(item);
                UpdateNotificationVisualState();
            }
        }

        private void NotificationsViewModel_PropertyChanged(
            object? sender,
            PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(NotificationsViewModel.UnreadCount) or
                nameof(NotificationsViewModel.HasUnread) or
                nameof(NotificationsViewModel.IsLoading) or
                nameof(NotificationsViewModel.IsEmpty) or
                nameof(NotificationsViewModel.HasError) or
                nameof(NotificationsViewModel.ErrorMessage))
            {
                UpdateNotificationVisualState();
            }
        }

        private void NotificationsViewModelItems_CollectionChanged(
            object? sender,
            NotifyCollectionChangedEventArgs e)
        {
            UpdateNotificationVisualState();
            UpdateNotificationListHeight();
        }

        private void NotificationItemContainer_Loaded(object sender, RoutedEventArgs e)
            => UpdateNotificationListHeight();

        // 通知列表最多显示 5 条,超过则在 ScrollViewer 内滚动。
        // 条目高度不固定(正文 0~3 行),按已实现的前 5 条实际高度累加得出上限。
        private void UpdateNotificationListHeight()
        {
            const int maxVisibleItems = 5;
            var count = NotificationsViewModel.Items.Count;
            if (count <= maxVisibleItems)
            {
                NotificationScrollViewer.MaxHeight = double.PositiveInfinity;
                return;
            }

            double height = 0;
            for (var i = 0; i < maxVisibleItems; i++)
            {
                if (NotificationList.TryGetElement(i) is not FrameworkElement element ||
                    element.ActualHeight <= 0)
                {
                    return; // 前 5 条尚未实现/测量,等待容器 Loaded 后再计算
                }

                height += element.ActualHeight;
            }

            if (Math.Abs(NotificationScrollViewer.MaxHeight - height) > 0.5)
            {
                NotificationScrollViewer.MaxHeight = height;
            }
        }

        private void UpdateNotificationVisualState()
        {
            var isSignedIn = NotificationButton.Visibility == Visibility.Visible;
            var hasItems = NotificationsViewModel.Items.Count > 0;
            var isLoading = NotificationsViewModel.IsLoading;
            var hasError = NotificationsViewModel.HasError;

            NotificationBadge.Value = NotificationsViewModel.UnreadCount;
            NotificationBadge.Visibility = isSignedIn && NotificationsViewModel.HasUnread
                ? Visibility.Visible
                : Visibility.Collapsed;

            NotificationLoadingRing.IsActive = isLoading;
            NotificationLoadingRing.Visibility = isLoading
                ? Visibility.Visible
                : Visibility.Collapsed;

            NotificationScrollViewer.Visibility = hasItems
                ? Visibility.Visible
                : Visibility.Collapsed;

            NotificationEmptyTextBlock.Text = hasError
                ? NotificationsViewModel.ErrorMessage ?? "通知加载失败"
                : "暂无通知";
            NotificationEmptyPanel.Visibility = !isLoading && (hasError || !hasItems)
                ? Visibility.Visible
                : Visibility.Collapsed;

            MarkAllNotificationsReadButton.IsEnabled =
                isSignedIn && !isLoading && NotificationsViewModel.HasUnread;
        }

        private void UpdatePaneFooterLayout()
        {
            var compact = !NavigationRoot.IsPaneOpen;
            _paneFooterCompact = compact;

            PaneFooterStack.Width = compact ? NavigationRoot.CompactPaneLength : double.NaN;

            AccountFooterDetailsPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            AccountFooterChevron.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            AccountFooterContentGrid.ColumnSpacing = compact ? 0 : 12;
            AccountFooterMiddleColumn.Width = compact
                ? new GridLength(0)
                : new GridLength(1, GridUnitType.Star);
            AccountFooterButton.HorizontalAlignment = compact
                ? HorizontalAlignment.Center
                : HorizontalAlignment.Stretch;
            AccountFooterButton.HorizontalContentAlignment = compact
                ? HorizontalAlignment.Center
                : HorizontalAlignment.Stretch;
            AccountFooterButton.Margin = compact
                ? new Thickness(0, 8, 0, 0)
                : new Thickness(4, 8, 4, 0);
            AccountFooterButton.Padding = compact
                ? new Thickness(2)
                : new Thickness(8, 6, 8, 6);

            ContinueReadingTextButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ContinueReadingDismissButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ContinueReadingContentGrid.ColumnSpacing = compact ? 0 : 8;
            ContinueReadingMiddleColumn.Width = compact
                ? new GridLength(0)
                : new GridLength(1, GridUnitType.Star);
            ContinueReadingBar.HorizontalAlignment = compact
                ? HorizontalAlignment.Center
                : HorizontalAlignment.Stretch;
            ContinueReadingBar.Margin = compact
                ? new Thickness(0, 0, 0, 4)
                : new Thickness(4, 0, 4, 4);
            ContinueReadingBar.BorderThickness = compact
                ? new Thickness(0)
                : new Thickness(1);
            ContinueReadingBar.Background = compact
                ? null
                : (Brush)Application.Current.Resources["ContinueReadingBarBackgroundBrush"];
            ContinueReadingBar.Padding = compact
                ? new Thickness(0)
                : new Thickness(6);
        }

        private async Task ShowMessageDialogAsync(string title, string message)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = (Content as FrameworkElement)?.XamlRoot,
                RequestedTheme = (Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
                Title = title,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                },
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close,
            };
            SyncDialogThemeOnOpen(dialog);

            await dialog.ShowAsync();
        }

        private async void VerifyEmailButton_Click(object sender, RoutedEventArgs e)
        {
            AccountFlyout.Hide();
            await StartEmailVerificationFromUserActionAsync();
        }

        // 供外部页面(如个人主页)主动触发验证流程。
        public async Task StartEmailVerificationFromUserActionAsync()
        {
            if (!await EnsureSupabaseInitializedAsync())
            {
                await ShowMessageDialogAsync(
                    "认证不可用",
                    AppendSupabaseError("Supabase 尚未初始化,无法发送验证邮件。"));
                return;
            }

            if (!SupabaseService.Instance.IsSignedIn)
            {
                ShowAccountFlyout();
                return;
            }

            if (await SupabaseService.Instance.RefreshCurrentUserEmailVerificationAsync())
            {
                UpdateAccountFooter();
                await ShowMessageDialogAsync("无需验证", "当前邮箱已通过验证。");
                return;
            }

            await StartEmailVerificationFlowAsync(SupabaseService.Instance.CurrentEmail);
        }

        private async Task StartEmailVerificationFlowAsync(string? email)
        {
            hanabimanga.Models.EmailVerificationDispatchResult dispatch;
            try
            {
                dispatch = await SupabaseService.Instance.SendCurrentEmailVerificationOtpAsync(email);
            }
            catch (Exception ex)
            {
                await ShowMessageDialogAsync("验证码发送失败", ex.Message);
                return;
            }

            var codeBox = new TextBox
            {
                Header = "邮箱验证码",
                PlaceholderText = "输入邮箱中的 6 位验证码",
                MaxLength = 12,
            };
            var feedbackBar = new InfoBar
            {
                IsOpen = false,
                IsClosable = false,
            };
            var contentPanel = new StackPanel
            {
                Spacing = 12,
                MinWidth = 320,
            };

            // 优先展示 masked_email,让用户能确认验证码寄到了正确邮箱;再附上过期时间。
            var intro = string.IsNullOrWhiteSpace(dispatch.MaskedEmail)
                ? "验证码已发送到你的邮箱。"
                : $"验证码已发送至 {dispatch.MaskedEmail}。";
            if (dispatch.ExpiresAt is { } expiresAt)
            {
                var remaining = expiresAt.ToUniversalTime() - DateTime.UtcNow;
                if (remaining.TotalSeconds > 0)
                {
                    intro += $"该验证码 {(int)Math.Ceiling(remaining.TotalMinutes)} 分钟内有效。";
                }
            }
            intro += "完成验证后,积分奖励到账情况可在任务中心查看。";
            contentPanel.Children.Add(new TextBlock
            {
                Text = intro,
                TextWrapping = TextWrapping.Wrap,
            });
            contentPanel.Children.Add(codeBox);
            contentPanel.Children.Add(feedbackBar);

            var completed = false;
            var dialog = new ContentDialog
            {
                XamlRoot = (Content as FrameworkElement)?.XamlRoot,
                RequestedTheme = (Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
                Title = "输入邮箱验证码",
                Content = contentPanel,
                PrimaryButtonText = "验证",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };
            SyncDialogThemeOnOpen(dialog);

            dialog.PrimaryButtonClick += async (_, args) =>
            {
                args.Cancel = true;
                var code = codeBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(code))
                {
                    ShowFeedback(feedbackBar, InfoBarSeverity.Warning, "请检查输入", "请输入邮箱验证码。");
                    return;
                }

                dialog.IsPrimaryButtonEnabled = false;
                codeBox.IsEnabled = false;
                try
                {
                    await SupabaseService.Instance.VerifyCurrentEmailOtpAsync(code, email);
                    completed = true;
                    dialog.Hide();
                }
                catch (Exception ex)
                {
                    ShowFeedback(feedbackBar, InfoBarSeverity.Error, "验证失败", ex.Message);
                }
                finally
                {
                    if (!completed)
                    {
                        dialog.IsPrimaryButtonEnabled = true;
                        codeBox.IsEnabled = true;
                    }
                }
            };

            await dialog.ShowAsync();
            if (!completed)
            {
                return;
            }

            UpdateAccountFooter();
            await ShowMessageDialogAsync("验证成功", "邮箱已验证,积分奖励到账后可在任务中心查看。");
        }

        // ContentDialog 经 XamlRoot 弹出时,命令栏等模板部件不随 dialog.RequestedTheme,
        // 会停留在应用启动时的主题,导致上半内容区与下半按钮区主题割裂。
        // 打开后对模板根(LayoutRoot)整体再赋一次当前主题,强制所有已实例化部件重解析。
        private void SyncDialogThemeOnOpen(ContentDialog dialog)
        {
            dialog.Opened += (sender, _) =>
            {
                var theme = (Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;
                if (VisualTreeHelper.GetChildrenCount(sender) > 0
                    && VisualTreeHelper.GetChild(sender, 0) is FrameworkElement templateRoot)
                {
                    templateRoot.RequestedTheme = theme;
                }
            };
        }

        // 按窗口当前实际主题从对应 ThemeDictionary 取画刷;
        // 不能用 Application.Current.Resources[key],它只返回应用启动时主题的画刷。
        private Brush ThemedBrush(string key)
        {
            var isDark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
            var dictKey = isDark ? "Dark" : "Light";
            var themeDictionaries = Application.Current.Resources.ThemeDictionaries;
            if (themeDictionaries.TryGetValue(dictKey, out var raw)
                && raw is ResourceDictionary dictionary
                && dictionary.TryGetValue(key, out var value)
                && value is Brush brush)
            {
                return brush;
            }
            return (Brush)Application.Current.Resources[key];
        }

        private static bool TryValidateSignInInput(
            string email,
            string password,
            out string message)
        {
            if (!TryValidateEmail(email, out message)) return false;

            if (string.IsNullOrWhiteSpace(password))
            {
                message = "请输入密码。";
                return false;
            }

            message = "";
            return true;
        }

        private static bool TryValidateSignUpInput(
            string email,
            string password,
            string confirmPassword,
            string userId,
            string nickname,
            string inviteCode,
            bool acceptedTerms,
            out string message)
        {
            if (!TryValidateSignInInput(email, password, out message)) return false;

            if (password.Length < 6)
            {
                message = "密码至少需要 6 位。";
                return false;
            }

            if (confirmPassword.Length < 6)
            {
                message = "确认密码至少需要 6 位。";
                return false;
            }

            if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
            {
                message = "两次输入的密码不一致。";
                return false;
            }

            var normalizedUserId = userId.Trim();
            if (string.IsNullOrWhiteSpace(normalizedUserId))
            {
                message = "请输入用户名。";
                return false;
            }

            if (!TryValidateUsername(normalizedUserId, out message))
            {
                return false;
            }

            if (nickname.Trim().Length > 32)
            {
                message = "昵称不能超过 32 个字符。";
                return false;
            }

            if (inviteCode.Trim().Length > 64)
            {
                message = "邀请码不能超过 64 个字符。";
                return false;
            }

            if (!acceptedTerms)
            {
                message = "请先阅读并同意用户协议和隐私政策。";
                return false;
            }

            return true;
        }

        private static bool TryValidateUsername(string username, out string message)
        {
            var normalized = username.Trim();
            if (normalized.Length < 3 || normalized.Length > 20)
            {
                message = "用户名需要 3-20 个字符。";
                return false;
            }

            message = "";
            return true;
        }

        private static string FormatUsernameUnavailableMessage(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return "用户名已被占用,请换一个。";

            var normalized = reason.Trim().ToLowerInvariant();
            if (normalized.Contains("available", StringComparison.Ordinal) &&
                normalized.Contains("false", StringComparison.Ordinal))
            {
                return "用户名已被占用,请换一个。";
            }

            return normalized switch
            {
                "taken" or "username_taken" or "duplicate" => "用户名已被占用,请换一个。",
                "length" or "invalid_length" => "用户名需要 3-20 个字符。",
                "invalid" or "invalid_format" => "用户名格式不符合要求。",
                "reserved" => "该用户名不可使用,请换一个。",
                "check_failed" => "用户名检查失败,请稍后再试。",
                _ => reason!,
            };
        }

        private static bool IsUsernameTakenMessage(string? message)
            => !string.IsNullOrWhiteSpace(message) &&
                (message.Contains("用户名已被占用", StringComparison.Ordinal) ||
                 message.Contains("USERNAME_RACE_TAKEN", StringComparison.OrdinalIgnoreCase));

        private static void StorePendingInviteCode(string? inviteCode)
        {
            const string key = "invite_prompt.pending_invite_code";
            try
            {
                var values = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                var normalized = inviteCode?.Trim();
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    values.Remove(key);
                    return;
                }

                values[key] = normalized;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[auth-signup] pending invite persistence failed: {ex.Message}");
            }
        }

        private static bool TryValidateEmail(string email, out string message)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                message = "请输入邮箱。";
                return false;
            }

            var trimmedEmail = email.Trim();
            if (!trimmedEmail.Contains('@') || trimmedEmail.StartsWith('@') || trimmedEmail.EndsWith('@'))
            {
                message = "请输入有效的邮箱地址。";
                return false;
            }

            message = "";
            return true;
        }

        private static void ShowFeedback(InfoBar feedbackBar, InfoBarSeverity severity, string title, string message)
        {
            feedbackBar.Severity = severity;
            feedbackBar.Title = title;
            feedbackBar.Message = message;
            feedbackBar.IsOpen = true;
        }

        private async Task LoadAccountProfileAsync(
            string userId,
            string? email,
            int refreshVersion)
        {
            try
            {
                var profile = await SupabaseService.Instance.GetCurrentUserProfileAsync(userId);
                if (refreshVersion != _accountRefreshVersion || profile == null) return;

                var displayName = GetAccountDisplayName(profile, email);
                var status = BuildAccountStatus(profile);
                var isEmailVerified = await SupabaseService.Instance.RefreshCurrentUserEmailVerificationAsync();
                System.Diagnostics.Debug.WriteLine(
                    "[account] applying profile: " +
                    $"displayName={displayName}, avatar_url={profile.AvatarUrl ?? "(null)"}");
                ApplyAccountDisplay(displayName, status, profile.AvatarUrl, isEmailVerified);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载用户资料失败: {ex}");
            }
        }

        private void ApplyAccountDisplay(
            string displayName,
            string status,
            string? avatarUrl,
            bool isEmailVerified)
        {
            AccountNameTextBlock.Text = displayName;
            AccountStatusTextBlock.Text = status;
            AccountFlyoutNameTextBlock.Text = displayName;
            AccountFlyoutStatusTextBlock.Text = status;

            // 已验证徽标对应蓝色对勾;未登录或未验证时不显示徽标,不再额外展示警示图标。
            // 账户菜单里的「验证邮箱」按钮仍对未验证账号显示,作为统一的验证入口。
            var isSignedIn = SupabaseService.Instance.IsSignedIn;
            var showVerifyButton = isSignedIn && !isEmailVerified;
            AccountVerifiedBadge.Visibility = isEmailVerified ? Visibility.Visible : Visibility.Collapsed;
            AccountFlyoutVerifiedBadge.Visibility = isEmailVerified ? Visibility.Visible : Visibility.Collapsed;
            AccountFlyoutVerifyEmailButton.Visibility = showVerifyButton ? Visibility.Visible : Visibility.Collapsed;

            AccountPersonPicture.DisplayName = displayName;
            AccountFlyoutPersonPicture.DisplayName = displayName;

            var avatarSource = CreateAvatarSource(avatarUrl);
            AccountPersonPicture.ProfilePicture = avatarSource;
            AccountFlyoutPersonPicture.ProfilePicture = avatarSource;
        }

        private static ImageSource? CreateAvatarSource(string? avatarUrl)
        {
            if (string.IsNullOrWhiteSpace(avatarUrl))
            {
                return CreateLocalAvatarSource(DefaultAvatarAssetName, isFallback: true);
            }

            if (!Uri.TryCreate(avatarUrl, UriKind.Absolute, out var uri))
            {
                return CreateLocalAvatarSource(avatarUrl, isFallback: false);
            }

            return CreateBitmapImage(uri, avatarUrl);
        }

        private static ImageSource CreateLocalAvatarSource(string avatarName, bool isFallback)
        {
            var resolvedName = ResolveLocalAvatarAssetName(avatarName) ?? DefaultAvatarAssetName;
            if (!isFallback && resolvedName == DefaultAvatarAssetName)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[account-avatar] local avatar not found: {avatarName}; fallback={DefaultAvatarAssetName}");
            }

            var uri = new Uri($"ms-appx:///Assets/avatar/{resolvedName}", UriKind.Absolute);
            return CreateBitmapImage(uri, $"Assets/avatar/{resolvedName}");
        }

        private static string? ResolveLocalAvatarAssetName(string avatarName)
        {
            var fileName = Path.GetFileName(avatarName.Trim().Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(fileName)) return null;

            var candidates = string.IsNullOrWhiteSpace(Path.GetExtension(fileName))
                ? AvatarAssetExtensions.Select(extension => fileName + extension)
                : [fileName];

            foreach (var candidate in candidates)
            {
                var candidatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "avatar", candidate);
                if (File.Exists(candidatePath)) return candidate;
            }

            return null;
        }

        private static BitmapImage CreateBitmapImage(Uri uri, string debugSource)
        {
            var source = new BitmapImage(uri);
            source.ImageOpened += (_, _) =>
                System.Diagnostics.Debug.WriteLine($"[account-avatar] opened: {debugSource}");
            source.ImageFailed += (_, args) =>
                System.Diagnostics.Debug.WriteLine($"[account-avatar] failed: {debugSource}; {args.ErrorMessage}");

            return source;
        }

        private static string GetAccountDisplayName(UserProfile profile, string? email)
        {
            if (!string.IsNullOrWhiteSpace(profile.DisplayName)) return profile.DisplayName;
            if (!string.IsNullOrWhiteSpace(profile.Username)) return profile.Username;
            if (!string.IsNullOrWhiteSpace(email)) return email;

            return "已登录用户";
        }

        private static string BuildAccountStatus(UserProfile profile)
        {
            if (profile.IsPermanentVip)
            {
                return "永久会员";
            }

            if (profile.VipExpirationDate is { } vipExpirationDate && vipExpirationDate > DateTime.UtcNow)
            {
                return $"VIP 至 {vipExpirationDate:yyyy-MM-dd}";
            }

            return "普通用户";
        }

        private static (string? UserId, string? Email, string Role) ReadJwtUserClaims(string? accessToken)
        {
            if (string.IsNullOrWhiteSpace(accessToken)) return (null, null, "user");

            try
            {
                var parts = accessToken.Split('.');
                if (parts.Length < 2) return (null, null, "user");

                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');

                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
                var root = doc.RootElement;

                var email = root.TryGetProperty("email", out var emailElement)
                    ? emailElement.GetString()
                    : null;
                var userId = root.TryGetProperty("sub", out var subElement)
                    ? subElement.GetString()
                    : null;

                var role = "user";
                if (root.TryGetProperty("app_metadata", out var metadata)
                    && metadata.ValueKind == JsonValueKind.Object
                    && metadata.TryGetProperty("role", out var roleElement))
                {
                    role = roleElement.GetString() ?? "user";
                }

                return (userId, email, role);
            }
            catch
            {
                return (null, null, "user");
            }
        }
    }
}
