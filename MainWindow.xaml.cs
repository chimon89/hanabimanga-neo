using hanabimanga.Services;
using hanabimanga.Pages;
using hanabimanga.Models;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private RecentReadingProgress? _recentReadingProgress;
        private bool _isContinueReadingBarDismissed;
        private int _accountRefreshVersion;

        private enum AuthDialogMode
        {
            SignIn,
            SignUp,
        }

        public MainWindow()
        {
            InitializeComponent();
            UseGalleryStyleWindowFrame();
            RootFrame.Navigated += RootFrame_Navigated;
            NavigationRoot.SelectedItem = HomeNavigationItem;
            RootFrame.Navigate(typeof(HomePage));
            UpdateAccountFooter();
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
            _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: true);
                // 单位是物理像素(非 DIP),所以在 125%/150% DPI 下实际 DIP 更小。
                // 1280x720 = NavigationView 展开后 (320) 留 ~960 给内容,刚好
                // 容纳 5 列新作卡片(5*184 + 4*12 = 968)和首屏推荐区 + 部分章节。
                presenter.PreferredMinimumWidth = 1280;
                presenter.PreferredMinimumHeight = 720;
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
            AddPassthroughRegion(passthroughRects, TitleBarSearchBox, scaleAdjustment);
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

            if (RootFrame.Content is ComicDetailPage detailPage)
            {
                _currentDetailPage = detailPage;
                _currentDetailPage.TitleBarInfoChanged += DetailPage_TitleBarInfoChanged;
                ShowDetailTitleBar(detailPage);
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
                else if (RootFrame.Content is HistoryPage)
                {
                    NavigationRoot.SelectedItem = HistoryNavigationItem;
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
                case "history" when RootFrame.Content is not HistoryPage:
                    RootFrame.Navigate(typeof(HistoryPage));
                    break;
            }
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

        private void ShowDefaultTitleBar()
        {
            TitleBarLogoImage.Visibility = Visibility.Visible;
            TitleBarAppNameTextBlock.Visibility = Visibility.Visible;
            TitleBarSearchBox.Visibility = Visibility.Visible;
            DetailTitleBarBreadcrumb.Visibility = Visibility.Collapsed;
            AppTitleBar.UpdateLayout();
            UpdateTitleBarInteractiveRegions();
        }

        private void ShowDetailTitleBar(ComicDetailPage detailPage)
        {
            TitleBarLogoImage.Visibility = Visibility.Collapsed;
            TitleBarAppNameTextBlock.Visibility = Visibility.Collapsed;
            TitleBarSearchBox.Visibility = Visibility.Collapsed;
            DetailTitleBarBreadcrumb.Visibility = Visibility.Visible;

            DetailTitleBarCategoryTextBlock.Text = detailPage.TitleBarCategory;
            DetailTitleBarTitleTextBlock.Text = detailPage.TitleBarTitle;
            AppTitleBar.UpdateLayout();
            UpdateTitleBarInteractiveRegions();
        }

        private void ShowReaderTitleBar(ComicReaderPage readerPage)
        {
            TitleBarLogoImage.Visibility = Visibility.Collapsed;
            TitleBarAppNameTextBlock.Visibility = Visibility.Collapsed;
            TitleBarSearchBox.Visibility = Visibility.Collapsed;
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

        private void TitleBarSearchBox_QuerySubmitted(
            AutoSuggestBox sender,
            AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            var query = args.QueryText?.Trim();
            if (string.IsNullOrWhiteSpace(query)) return;

            if (RootFrame.Content is SearchPage)
            {
                RootFrame.Navigate(typeof(SearchPage), query);
                return;
            }

            RootFrame.Navigate(typeof(SearchPage), query);
        }

        private async Task RefreshContinueReadingBarAsync()
        {
            if (_isContinueReadingBarDismissed ||
                RootFrame.Content is not HomePage ||
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
                ContinueReadingSubtitleTextBlock.Text = _recentReadingProgress.ChapterTitle;
                ContinueReadingProgressTextBlock.Text = _recentReadingProgress.ProgressText;
                ContinueReadingCoverImage.Source = !string.IsNullOrWhiteSpace(_recentReadingProgress.CoverUrl)
                    ? new BitmapImage(new Uri(_recentReadingProgress.CoverUrl))
                    : null;
                ContinueReadingBar.Visibility = Visibility.Visible;
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
            ContinueReadingBar.Background = (Brush)Application.Current.Resources["ContinueReadingBarPointerOverBackgroundBrush"];
        }

        private void ContinueReadingBar_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            ContinueReadingBar.Background = (Brush)Application.Current.Resources["ContinueReadingBarBackgroundBrush"];
        }

        private void PaneToggleButton_Click(object sender, RoutedEventArgs e)
        {
            NavigationRoot.IsPaneOpen = !NavigationRoot.IsPaneOpen;
            UpdateAccountFooterForPaneState();
        }

        private void NavigationRoot_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
        {
            UpdateAccountFooterForPaneState();
        }

        private void AccountFooterButton_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateAccountFooterForPaneState();
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
            ViewAccountButton.IsEnabled = false;
            AccountSettingsButton.IsEnabled = false;

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
            }
            else
            {
                ApplyAccountDisplay("登录", "同步收藏和阅读进度", null);
                AccountFlyoutNameTextBlock.Text = "未登录";
                AccountFlyoutStatusTextBlock.Text = "登录后同步收藏和阅读进度";
                AccountPersonPicture.DisplayName = "";
                AccountFlyoutPersonPicture.DisplayName = "";
            }

            UpdateAccountFooterForPaneState();
        }

        private void UpdateAccountFooterForPaneState()
        {
            var hasFullPaneWidth =
                NavigationRoot.DisplayMode == NavigationViewDisplayMode.Expanded ||
                AccountFooterButton.ActualWidth >= 180;

            AccountFooterDetailsPanel.Visibility = hasFullPaneWidth ? Visibility.Visible : Visibility.Collapsed;
            AccountFooterButton.Padding = hasFullPaneWidth
                ? new Thickness(8, 6, 8, 6)
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
