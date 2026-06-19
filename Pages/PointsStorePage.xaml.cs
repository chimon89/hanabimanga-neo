using hanabimanga.ViewModels;
using hanabimanga.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Linq;
using Windows.System;

namespace hanabimanga.Pages
{
    public sealed partial class PointsStorePage : Page
    {
        public TaskCenterViewModel ViewModel { get; } = new();

        public PointsStorePage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await ViewModel.LoadAsync();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
                return;
            }

            Frame.Navigate(typeof(TaskCenterPage));
        }

        private void ExchangeHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(ExchangeHistoryPage));
        }

        private async void StoreItemButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string itemId) return;

            var item = ViewModel.VirtualStoreItems.FirstOrDefault(storeItem => storeItem.Id == itemId);
            if (item != null)
            {
                if (ViewModel.IsPermanentVip)
                {
                    await ShowSimpleDialogAsync("永久会员", "会员权益已永久生效，无需再次购买。");
                    return;
                }

                if (item.Price <= 0)
                {
                    await ShowSimpleDialogAsync("暂不可购买", "未读取到有效的支付商品，请刷新后再试。");
                    return;
                }

                await PurchaseAsync(item);
                return;
            }

            await ViewModel.RedeemAsync(itemId);
        }

        private async System.Threading.Tasks.Task PurchaseAsync(PointStoreItem item)
        {
            var order = await ViewModel.CreatePaymentOrderAsync(item.Id);
            if (order == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(order.PayUrl) &&
                Uri.TryCreate(order.PayUrl, UriKind.Absolute, out var payUri))
            {
                await Launcher.LaunchUriAsync(payUri);
            }

            await ShowPaymentDialogAsync(order, item.Title);
        }

        private async System.Threading.Tasks.Task ShowPaymentDialogAsync(PaymentOrder order, string productTitle)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "订单已创建",
                Content = BuildPaymentDialogContent(order, productTitle),
                PrimaryButtonText = "我已支付",
                SecondaryButtonText = "重新打开支付页",
                CloseButtonText = "稍后处理",
                DefaultButton = ContentDialogButton.Primary,
            };

            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                try
                {
                    var latest = await ViewModel.SyncPaymentOrderAsync(order);
                    if (latest?.IsPaid == true)
                    {
                        dialog.Hide();
                        await ShowSimpleDialogAsync("支付成功", "VIP 已到账，若个人信息未立即更新，请稍后刷新账户。");
                    }
                    else
                    {
                        args.Cancel = true;
                    }
                }
                finally
                {
                    deferral.Complete();
                }
            };

            dialog.SecondaryButtonClick += async (_, args) =>
            {
                args.Cancel = true;
                if (!string.IsNullOrWhiteSpace(order.PayUrl) &&
                    Uri.TryCreate(order.PayUrl, UriKind.Absolute, out var payUri))
                {
                    await Launcher.LaunchUriAsync(payUri);
                }
            };

            await dialog.ShowAsync();
        }

        private static StackPanel BuildPaymentDialogContent(PaymentOrder order, string productTitle)
            => new()
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = productTitle, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                    new TextBlock { Text = $"订单号：{order.TradeNo}" },
                    new TextBlock { Text = $"金额：{order.AmountText}" },
                    new TextBlock
                    {
                        Text = "系统浏览器已打开支付页。完成支付后回到这里点击“我已支付”，应用会主动同步订单状态。",
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            };

        private async System.Threading.Tasks.Task ShowSimpleDialogAsync(string title, string message)
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = message,
                CloseButtonText = "知道了",
            }.ShowAsync();
        }
    }
}
