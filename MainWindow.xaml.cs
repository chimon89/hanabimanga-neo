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

        public NotificationsViewModel NotificationsViewModel { get; } = new();

        private enum AuthDialogMode
        {
            SignIn,
            SignUp,
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

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
            _ = RefreshContinueReadingBarAsync();
        }

        private void UseGalleryStyleWindowFrame()
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            var windowHandle = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
            _appWindow = AppWindow.GetFromWindowId(windowId);

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
                else if (RootFrame.Content is FeedbackListPage or FeedbackSubmitPage)
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
                case "task-center" when RootFrame.Content is not TaskCenterPage &&
                    RootFrame.Content is not PointsDetailPage &&
                    RootFrame.Content is not PointsStorePage &&
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

            if (!SupabaseService.Instance.IsInitialized)
            {
                await ShowMessageDialogAsync("认证不可用", "Supabase 尚未初始化,请检查本地配置。");
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
            if (!SupabaseService.Instance.IsInitialized)
            {
                await ShowMessageDialogAsync("认证不可用", "Supabase 尚未初始化,请检查 appsettings.local.json 或环境变量。");
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
                contentPanel.Children.Add(new TextBlock
                {
                    Text = "注册后需要通过邮箱确认账号。",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
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

            contentPanel.Children.Add(feedbackBar);

            var completed = false;
            var dialog = new ContentDialog
            {
                XamlRoot = (Content as FrameworkElement)?.XamlRoot,
                Title = mode == AuthDialogMode.SignIn ? "登录花火漫画" : "注册花火漫画",
                PrimaryButtonText = mode == AuthDialogMode.SignIn ? "登录" : "注册",
                SecondaryButtonText = mode == AuthDialogMode.SignIn ? "去注册" : "去登录",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                Content = contentPanel,
            };

            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                dialog.IsPrimaryButtonEnabled = false;
                try
                {
                    if (!TryValidateAuthInput(mode, emailBox.Text, passwordBox.Password, out var validationMessage))
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
                        await SupabaseService.Instance.SignUpAsync(emailBox.Text.Trim(), passwordBox.Password);
                    }

                    completed = true;
                }
                catch (Exception ex)
                {
                    ShowFeedback(feedbackBar, InfoBarSeverity.Error, "操作失败", ex.Message);
                    args.Cancel = true;
                }
                finally
                {
                    dialog.IsPrimaryButtonEnabled = true;
                    deferral.Complete();
                }
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Secondary)
            {
                await ShowAuthDialogAsync(
                    mode == AuthDialogMode.SignIn ? AuthDialogMode.SignUp : AuthDialogMode.SignIn);
                return;
            }

            if (!completed) return;

            _isContinueReadingBarDismissed = false;
            UpdateAccountFooter();
            _ = RefreshContinueReadingBarAsync();
            if (mode == AuthDialogMode.SignUp)
            {
                await ShowMessageDialogAsync("注册申请已提交", "确认邮件已经发送到你的邮箱,请完成验证后再登录。");
            }
        }

        private async Task ShowMagicLinkDialogAsync()
        {
            if (!SupabaseService.Instance.IsInitialized)
            {
                await ShowMessageDialogAsync("认证不可用", "Supabase 尚未初始化,请检查 appsettings.local.json 或环境变量。");
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
                Title = "Magic Link 登录",
                PrimaryButtonText = "发送邮件",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                Content = contentPanel,
            };

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

            await dialog.ShowAsync();
        }

        private void UpdateAccountFooter()
        {
            var refreshVersion = ++_accountRefreshVersion;
            var accessToken = SupabaseService.Instance.CurrentSession?.AccessToken;
            var isSignedIn = !string.IsNullOrWhiteSpace(accessToken);

            SignedOutAccountActionsPanel.Visibility = isSignedIn ? Visibility.Collapsed : Visibility.Visible;
            SignedInAccountActionsPanel.Visibility = isSignedIn ? Visibility.Visible : Visibility.Collapsed;
            ViewMyProfileButton.IsEnabled = isSignedIn;
            TaskCenterButton.IsEnabled = isSignedIn;
            FeedbackButton.IsEnabled = isSignedIn;
            ViewAccountButton.IsEnabled = isSignedIn;

            if (isSignedIn)
            {
                var claims = ReadJwtUserClaims(accessToken);
                var displayName = string.IsNullOrWhiteSpace(claims.Email) ? "已登录用户" : claims.Email;
                var status = $"{GetRoleDisplayName(claims.Role)} · 会话已同步";

                ApplyAccountDisplay(displayName, status, null);

                if (!string.IsNullOrWhiteSpace(claims.UserId))
                {
                    _ = LoadAccountProfileAsync(claims.UserId, claims.Email, claims.Role, refreshVersion);
                }

                NotificationButton.Visibility = Visibility.Visible;
                _ = SetupNotificationsAsync(accessToken!, claims.UserId);
            }
            else
            {
                ApplyAccountDisplay("登录", "同步收藏和阅读进度", null);
                AccountFlyoutNameTextBlock.Text = "未登录";
                AccountFlyoutStatusTextBlock.Text = "登录后同步收藏和阅读进度";
                AccountPersonPicture.DisplayName = "";
                AccountFlyoutPersonPicture.DisplayName = "";

                NotificationButton.Visibility = Visibility.Collapsed;
                _activeNotificationSessionKey = null;
                SupabaseService.Instance.UnsubscribeNotifications();
                NotificationsViewModel.Clear();
            }

            UpdateNotificationVisualState();
            AppTitleBar.UpdateLayout();
            UpdateTitleBarInteractiveRegions();
            UpdatePaneFooterLayout();
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
                Title = title,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                },
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close,
            };

            await dialog.ShowAsync();
        }

        private static bool TryValidateAuthInput(
            AuthDialogMode mode,
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

            if (mode == AuthDialogMode.SignUp && password.Length < 6)
            {
                message = "Supabase Auth 默认要求密码至少 6 位。";
                return false;
            }

            return true;
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
            string role,
            int refreshVersion)
        {
            try
            {
                var profile = await SupabaseService.Instance.GetCurrentUserProfileAsync(userId);
                if (refreshVersion != _accountRefreshVersion || profile == null) return;

                var displayName = GetAccountDisplayName(profile, email);
                var status = BuildAccountStatus(role, profile);
                System.Diagnostics.Debug.WriteLine(
                    "[account] applying profile: " +
                    $"displayName={displayName}, role={role}, avatar_url={profile.AvatarUrl ?? "(null)"}");
                ApplyAccountDisplay(displayName, status, profile.AvatarUrl);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载用户资料失败: {ex}");
            }
        }

        private void ApplyAccountDisplay(string displayName, string status, string? avatarUrl)
        {
            AccountNameTextBlock.Text = displayName;
            AccountStatusTextBlock.Text = status;
            AccountFlyoutNameTextBlock.Text = displayName;
            AccountFlyoutStatusTextBlock.Text = status;

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

        private static string BuildAccountStatus(string role, UserProfile profile)
        {
            var roleText = GetRoleDisplayName(role);
            if (profile.VipExpirationDate is { } vipExpirationDate && vipExpirationDate > DateTime.UtcNow)
            {
                return $"{roleText} · VIP 至 {vipExpirationDate:yyyy-MM-dd}";
            }

            return $"{roleText} · 会话已同步";
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

        private static string GetRoleDisplayName(string role) => role switch
        {
            "admin" => "管理员",
            "editor" => "编辑",
            _ => "普通用户",
        };
    }
}
