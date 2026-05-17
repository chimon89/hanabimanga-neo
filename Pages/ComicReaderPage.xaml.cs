using System;
using hanabimanga.Models;
using hanabimanga.Services;
using hanabimanga.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.System;

namespace hanabimanga.Pages
{
    public sealed partial class ComicReaderPage : Page
    {
        private const float MinPageZoomFactor = 1.0f;
        private const float MaxPageZoomFactor = 4.0f;
        private const float PageZoomStep = 1.2f;

        private bool _isDraggingPageImage;
        private Point _pageDragStartPoint;
        private double _pageDragStartHorizontalOffset;
        private double _pageDragStartVerticalOffset;
        private ScrollViewer? _dragScrollViewer;

        public ComicReaderPageViewModel ViewModel { get; } = new();
        public event EventHandler? TitleBarInfoChanged;
        public event EventHandler? ReadingProgressChanged;

        public string TitleBarCategory => "阅读器";
        public string TitleBarTitle => ViewModel.TitleBarTitle;

        public ComicReaderPage()
        {
            InitializeComponent();
            var wheelHandler = new PointerEventHandler(ReaderZoomScrollViewer_PointerWheelChanged);
            PageModeZoomScrollViewer.AddHandler(PointerWheelChangedEvent, wheelHandler, true);
            ReaderScrollViewer.AddHandler(PointerWheelChangedEvent, wheelHandler, true);
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            TitleBarInfoChanged?.Invoke(this, EventArgs.Empty);
            await ViewModel.LoadAsync(e.Parameter as ComicReaderNavigationParameter);
            await SaveProgressAndNotifyAsync();
            ResetActiveZoom();
            TitleBarInfoChanged?.Invoke(this, EventArgs.Empty);
            _ = ShowReaderZoomGuideIfNeededAsync();
        }

        private void ReaderImage_ImageOpened(object sender, RoutedEventArgs e)
        {
            ViewModel.OnFirstImageRendered();
            var page = ResolvePageImage(sender);
            if (page != null) page.LoadFailed = false;
            if (ReferenceEquals(sender, PageModeImage))
            {
                UpdatePageModeImageFitHeight();
            }
            else if (sender is Image waterfallImage)
            {
                UpdateWaterfallImageFitHeight(waterfallImage);
            }
        }

        private void ReaderImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            // 即使加载失败也让 loading 收起,避免一直转圈
            ViewModel.OnFirstImageRendered();
            var page = ResolvePageImage(sender);
            if (page != null) page.LoadFailed = true;
            System.Diagnostics.Debug.WriteLine($"[reader] image failed: {e.ErrorMessage}");
        }

        // 瀑布流模式下 Image 的 DataContext 即对应页;翻页模式下取当前页
        private ReaderPageImage? ResolvePageImage(object sender)
            => (sender as FrameworkElement)?.DataContext as ReaderPageImage ?? ViewModel.CurrentPageImage;

        private void RetryImage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;

            if (element.DataContext is ReaderPageImage waterfallPage)
            {
                // 瀑布流模式:重置该页状态并就近重新请求图片
                waterfallPage.LoadFailed = false;
                var image = FindAncestor<Grid>(element)?.Children switch
                {
                    { } children => FirstImage(children),
                    _ => null,
                };
                if (image != null)
                {
                    image.Source = MakeImageSource(waterfallPage.Url);
                    UpdateWaterfallImageFitHeight(image);
                }
            }
            else
            {
                // 翻页模式:重置当前页并重新请求
                var current = ViewModel.CurrentPageImage;
                if (current == null) return;
                current.LoadFailed = false;
                PageModeImage.Source = MakeImageSource(current.Url);
                UpdatePageModeImageFitHeight();
            }
        }

        private static Image? FirstImage(UIElementCollection children)
        {
            foreach (var child in children)
            {
                if (child is Image image) return image;
            }
            return null;
        }

        private static ImageSource? MakeImageSource(string url)
            => string.IsNullOrWhiteSpace(url) ? null : new BitmapImage(new Uri(url));

        private static T? FindAncestor<T>(DependencyObject node) where T : class
        {
            var current = VisualTreeHelper.GetParent(node);
            while (current != null)
            {
                if (current is T match) return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void PreviousChapterButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateToChapter(ViewModel.PreviousChapter);
        }

        private void NextChapterButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateToChapter(ViewModel.NextChapter);
        }

        private void PreviousPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.GoToPreviousPage())
            {
                _ = SaveProgressAndNotifyAsync();
                ResetActiveZoom();
            }
        }

        private void NextPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.GoToNextPage())
            {
                _ = SaveProgressAndNotifyAsync();
                ResetActiveZoom();
            }
        }

        private void ScrollTopButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.IsPageMode)
            {
                PageModeZoomScrollViewer.ChangeView(0, 0, null);
                return;
            }

            ReaderScrollViewer.ChangeView(null, 0, null);
        }

        private async void DownloadChapterButton_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.DownloadCurrentChapterAsync();
        }

        private async void UpscaledToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton toggle) return;

            // ToggleButton 内部已先翻转,IsChecked 表示用户期望的新状态
            var target = toggle.IsChecked == true;
            await ViewModel.SetUpscaledAsync(target);
            await SaveProgressAndNotifyAsync();

            // 失败或被服务端回退时,IsUpscaledLoaded 与 target 不一致,
            // 把按钮 IsChecked 同步回 ViewModel 的真实状态
            if (toggle.IsChecked != ViewModel.IsUpscaledLoaded)
            {
                toggle.IsChecked = ViewModel.IsUpscaledLoaded;
            }

            ResetActiveZoom();
        }

        private void PageModeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SetViewMode(ReaderViewMode.Page);
            ResetActiveZoom();
        }

        private void WaterfallModeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SetViewMode(ReaderViewMode.Waterfall);
            ResetActiveZoom();
        }

        private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
        {
            ChangeActiveZoom(GetActiveZoomScrollViewer().ZoomFactor / PageZoomStep);
        }

        private void ZoomResetButton_Click(object sender, RoutedEventArgs e)
        {
            ResetActiveZoom();
        }

        private void ZoomInButton_Click(object sender, RoutedEventArgs e)
        {
            ChangeActiveZoom(GetActiveZoomScrollViewer().ZoomFactor * PageZoomStep);
        }

        private void ReaderZoomScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if ((e.KeyModifiers & VirtualKeyModifiers.Control) != VirtualKeyModifiers.Control ||
                sender is not ScrollViewer scrollViewer ||
                !ReferenceEquals(scrollViewer, GetActiveZoomScrollViewer()))
            {
                return;
            }

            var pointer = e.GetCurrentPoint(scrollViewer);
            var wheelDelta = pointer.Properties.MouseWheelDelta;
            if (wheelDelta == 0) return;

            var targetZoom = wheelDelta > 0
                ? scrollViewer.ZoomFactor * PageZoomStep
                : scrollViewer.ZoomFactor / PageZoomStep;

            ChangeActiveZoom(targetZoom, pointer.Position);
            e.Handled = true;
        }

        private void PageModeZoomScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            UpdateZoomControls();
        }

        private void PageModeZoomScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdatePageModeImageFitHeight();
        }

        private void ReaderScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateWaterfallImagesFitHeight();
        }

        private void WaterfallPageContainer_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Grid container) return;

            ApplyWaterfallContainerSize(container, out _, out _);
            if (FirstImage(container.Children) is { } image)
            {
                UpdateWaterfallImageFitHeight(image);
            }
        }

        private void PageModeZoomScrollViewer_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not ScrollViewer scrollViewer) return;

            var pointer = e.GetCurrentPoint(scrollViewer);
            if (pointer.PointerDeviceType != PointerDeviceType.Mouse ||
                !pointer.Properties.IsLeftButtonPressed ||
                scrollViewer.ZoomFactor <= MinPageZoomFactor + 0.001f)
            {
                return;
            }

            _isDraggingPageImage = true;
            _dragScrollViewer = scrollViewer;
            _pageDragStartPoint = pointer.Position;
            _pageDragStartHorizontalOffset = scrollViewer.HorizontalOffset;
            _pageDragStartVerticalOffset = scrollViewer.VerticalOffset;
            scrollViewer.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void PageModeZoomScrollViewer_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDraggingPageImage || _dragScrollViewer == null) return;

            var pointer = e.GetCurrentPoint(_dragScrollViewer);
            if (!pointer.Properties.IsLeftButtonPressed)
            {
                EndPageImageDrag(e);
                return;
            }

            var deltaX = pointer.Position.X - _pageDragStartPoint.X;
            var deltaY = pointer.Position.Y - _pageDragStartPoint.Y;
            _dragScrollViewer.ChangeView(
                _pageDragStartHorizontalOffset - deltaX,
                _pageDragStartVerticalOffset - deltaY,
                null,
                true);
            e.Handled = true;
        }

        private void PageModeZoomScrollViewer_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            EndPageImageDrag(e);
        }

        private void PageModeZoomScrollViewer_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            EndPageImageDrag(e);
        }

        private void PageModeZoomScrollViewer_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _isDraggingPageImage = false;
            _dragScrollViewer = null;
        }

        private void EndPageImageDrag(PointerRoutedEventArgs e)
        {
            if (!_isDraggingPageImage) return;

            _isDraggingPageImage = false;
            _dragScrollViewer?.ReleasePointerCapture(e.Pointer);
            _dragScrollViewer = null;
            e.Handled = true;
        }

        private void ChangeActiveZoom(float targetZoom, Point? focusPoint = null)
        {
            var scrollViewer = GetActiveZoomScrollViewer();
            targetZoom = Math.Clamp(targetZoom, MinPageZoomFactor, MaxPageZoomFactor);
            var currentZoom = Math.Max(scrollViewer.ZoomFactor, MinPageZoomFactor);
            var zoomRatio = targetZoom / currentZoom;

            var pivot = focusPoint ?? new Point(
                scrollViewer.ViewportWidth / 2,
                scrollViewer.ViewportHeight / 2);
            var centerX = scrollViewer.HorizontalOffset + pivot.X;
            var centerY = scrollViewer.VerticalOffset + pivot.Y;
            var targetOffsetX = centerX * zoomRatio - pivot.X;
            var targetOffsetY = centerY * zoomRatio - pivot.Y;

            scrollViewer.ChangeView(targetOffsetX, targetOffsetY, targetZoom, false);
            UpdateZoomControls();
        }

        private void ResetActiveZoom()
        {
            _isDraggingPageImage = false;
            _dragScrollViewer = null;
            if (ViewModel.IsWaterfallMode)
            {
                UpdateWaterfallImagesFitHeight();
                ReaderScrollViewer.ChangeView(0, 0, MinPageZoomFactor, true);
            }
            else
            {
                UpdatePageModeImageFitHeight();
                PageModeZoomScrollViewer.ChangeView(0, 0, MinPageZoomFactor, true);
            }

            UpdateZoomControls();
        }

        private void UpdateZoomControls()
        {
            var scrollViewer = GetActiveZoomScrollViewer();
            var zoom = scrollViewer.ZoomFactor;
            var zoomPercent = Math.Max(100, (int)Math.Round(zoom * 100));

            ZoomPercentageTextBlock.Text = $"{zoomPercent}%";
            ZoomOutButton.IsEnabled = zoom > MinPageZoomFactor + 0.001f;
            ZoomResetButton.IsEnabled =
                zoom > MinPageZoomFactor + 0.001f ||
                scrollViewer.HorizontalOffset > 0.5 ||
                scrollViewer.VerticalOffset > 0.5;
            ZoomInButton.IsEnabled = zoom < MaxPageZoomFactor - 0.001f;
        }

        private ScrollViewer GetActiveZoomScrollViewer()
            => ViewModel.IsWaterfallMode ? ReaderScrollViewer : PageModeZoomScrollViewer;

        private async Task ShowReaderZoomGuideIfNeededAsync()
        {
            try
            {
                var settings = await ReaderStorageService.Instance.LoadSettingsAsync();
                if (settings.HasSeenReaderZoomGuide) return;

                settings.HasSeenReaderZoomGuide = true;
                await ReaderStorageService.Instance.SaveSettingsAsync(settings);
                await Task.Delay(500);
                await PlayReaderZoomGuideAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[reader-guide] show zoom guide failed: {ex.Message}");
            }
        }

        private async Task PlayReaderZoomGuideAsync()
        {
            ReaderZoomGuideOverlay.Visibility = Visibility.Visible;
            ReaderZoomGuideOverlay.Opacity = 0;
            ReaderZoomGuideTranslate.Y = -10;

            await BeginStoryboardAsync(CreateGuideStoryboard(0, 1, -10, 0, 260));
            await Task.Delay(3200);
            await BeginStoryboardAsync(CreateGuideStoryboard(1, 0, 0, -10, 220));

            ReaderZoomGuideOverlay.Visibility = Visibility.Collapsed;
        }

        private Storyboard CreateGuideStoryboard(
            double fromOpacity,
            double toOpacity,
            double fromY,
            double toY,
            double milliseconds)
        {
            var storyboard = new Storyboard();
            storyboard.Children.Add(CreateDoubleAnimation(
                ReaderZoomGuideOverlay,
                nameof(Opacity),
                fromOpacity,
                toOpacity,
                milliseconds));
            storyboard.Children.Add(CreateDoubleAnimation(
                ReaderZoomGuideTranslate,
                nameof(TranslateTransform.Y),
                fromY,
                toY,
                milliseconds));

            return storyboard;
        }

        private static DoubleAnimation CreateDoubleAnimation(
            DependencyObject target,
            string property,
            double from,
            double to,
            double milliseconds)
        {
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };

            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            return animation;
        }

        private static Task BeginStoryboardAsync(Storyboard storyboard)
        {
            var completion = new TaskCompletionSource<object?>();
            storyboard.Completed += (_, _) => completion.TrySetResult(null);
            storyboard.Begin();
            return completion.Task;
        }

        private void UpdatePageModeImageFitHeight()
        {
            if (PageModeZoomScrollViewer == null ||
                PageModeSurface == null ||
                PageModeImage == null)
            {
                return;
            }

            var viewportHeight = PageModeZoomScrollViewer.ViewportHeight;
            if (double.IsNaN(viewportHeight) || viewportHeight <= 0)
            {
                viewportHeight = PageModeZoomScrollViewer.ActualHeight;
            }

            var viewportWidth = PageModeZoomScrollViewer.ViewportWidth;
            if (double.IsNaN(viewportWidth) || viewportWidth <= 0)
            {
                viewportWidth = PageModeZoomScrollViewer.ActualWidth;
            }

            viewportHeight = Math.Max(viewportHeight, 480);
            viewportWidth = Math.Max(viewportWidth, 360);

            var imageWidth = viewportWidth;
            if (PageModeImage.Source is BitmapImage bitmap &&
                bitmap.PixelWidth > 0 &&
                bitmap.PixelHeight > 0)
            {
                imageWidth = viewportHeight * bitmap.PixelWidth / bitmap.PixelHeight;
            }

            PageModeImage.Height = viewportHeight;
            PageModeImage.Width = imageWidth;
            PageModeSurface.Height = viewportHeight;
            PageModeSurface.Width = Math.Max(viewportWidth, imageWidth);
        }

        private void UpdateWaterfallImagesFitHeight()
        {
            foreach (var image in FindDescendants<Image>(ReaderScrollViewer))
            {
                if (ReferenceEquals(image, PageModeImage)) continue;
                UpdateWaterfallImageFitHeight(image);
            }
        }

        private void UpdateWaterfallImageFitHeight(Image image)
        {
            if (ReaderScrollViewer == null || image == null) return;

            var container = FindAncestor<Grid>(image);
            if (container == null) return;

            ApplyWaterfallContainerSize(container, out var viewportWidth, out var viewportHeight);

            var imageWidth = Math.Min(viewportWidth, 720);
            var imageHeight = viewportHeight;
            if (image.Source is BitmapImage bitmap &&
                bitmap.PixelWidth > 0 &&
                bitmap.PixelHeight > 0)
            {
                var fitScale = Math.Min(
                    viewportWidth / bitmap.PixelWidth,
                    viewportHeight / bitmap.PixelHeight);
                imageWidth = Math.Max(1, bitmap.PixelWidth * fitScale);
                imageHeight = Math.Max(1, bitmap.PixelHeight * fitScale);
            }

            image.Width = imageWidth;
            image.Height = imageHeight;
        }

        private void ApplyWaterfallContainerSize(
            Grid container,
            out double viewportWidth,
            out double viewportHeight)
        {
            viewportHeight = ReaderScrollViewer.ViewportHeight;
            if (double.IsNaN(viewportHeight) || viewportHeight <= 0)
            {
                viewportHeight = ReaderScrollViewer.ActualHeight;
            }

            viewportWidth = ReaderScrollViewer.ViewportWidth;
            if (double.IsNaN(viewportWidth) || viewportWidth <= 0)
            {
                viewportWidth = ReaderScrollViewer.ActualWidth;
            }

            viewportHeight = Math.Max(viewportHeight, 480);
            viewportWidth = Math.Max(viewportWidth - 40, 360);

            container.Height = viewportHeight;
            container.Width = viewportWidth;
        }

        private static System.Collections.Generic.IEnumerable<T> FindDescendants<T>(DependencyObject root)
            where T : DependencyObject
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                {
                    yield return match;
                }

                foreach (var descendant in FindDescendants<T>(child))
                {
                    yield return descendant;
                }
            }
        }

        private void NavigateToChapter(ComicChapter? chapter)
        {
            if (chapter == null) return;

            Frame.Navigate(typeof(ComicReaderPage), new ComicReaderNavigationParameter
            {
                ComicId = chapter.ComicId,
                ChapterId = chapter.Id,
            });
        }

        private async System.Threading.Tasks.Task SaveProgressAndNotifyAsync()
        {
            try
            {
                await ViewModel.SaveCurrentProgressAsync();
                ReadingProgressChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[reader] save progress failed: {ex.Message}");
            }
        }
    }
}
