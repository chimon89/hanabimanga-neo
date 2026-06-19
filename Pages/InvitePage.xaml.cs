using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using hanabimanga.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using WinRT;
using WinRT.Interop;

namespace hanabimanga.Pages
{
    public sealed partial class InvitePage : Page
    {
        [ComImport]
        [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDataTransferManagerInterop
        {
            IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
            void ShowShareUIForWindow(IntPtr appWindow);
        }

        private static readonly Guid DataTransferManagerIid =
            new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

        public TaskCenterViewModel ViewModel { get; } = new();
        private string? _pendingShareText;

        public InvitePage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await ViewModel.LoadAsync();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.RefreshAsync();
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

        private async void CopyInviteCodeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.HasInviteCode)
            {
                return;
            }

            CopyToClipboard(ViewModel.InviteCode);
            await SetButtonTextTemporarilyAsync(CopyInviteCodeTextBlock, "已复制", "复制");
        }

        private async void ShareInviteCodeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.HasInviteCode)
            {
                return;
            }

            try
            {
                _pendingShareText = ViewModel.InviteShareText;
                var hWnd = App.MainWindow is null
                    ? IntPtr.Zero
                    : WindowNative.GetWindowHandle(App.MainWindow);
                if (hWnd == IntPtr.Zero)
                {
                    return;
                }

                var interop = DataTransferManager.As<IDataTransferManagerInterop>();
                var iid = DataTransferManagerIid;
                var result = interop.GetForWindow(hWnd, ref iid);
                var manager = MarshalInterface<DataTransferManager>.FromAbi(result);
                manager.DataRequested -= ShareDataRequested;
                manager.DataRequested += ShareDataRequested;
                interop.ShowShareUIForWindow(hWnd);
            }
            catch
            {
                // 系统分享面板不可用时保持分享按钮语义,不切换为复制反馈。
            }
        }

        private void ShareDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
        {
            sender.DataRequested -= ShareDataRequested;

            var text = string.IsNullOrWhiteSpace(_pendingShareText)
                ? ViewModel.InviteShareText
                : _pendingShareText;

            args.Request.Data.Properties.Title = "花火漫画邀请码";
            args.Request.Data.Properties.Description = "分享邀请码给好友";
            args.Request.Data.SetText(text);
            args.Request.Data.RequestedOperation = DataPackageOperation.Copy;
        }

        private static void CopyToClipboard(string text)
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }

        private static async Task SetButtonTextTemporarilyAsync(
            TextBlock textBlock,
            string temporaryText,
            string restoredText)
        {
            textBlock.Text = temporaryText;
            await Task.Delay(1400);
            textBlock.Text = restoredText;
        }
    }
}
