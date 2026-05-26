using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace hanabimanga.ViewModels
{
    // 书架页 VM:组合 历史 / 收藏 / 点赞 三个已有子 VM,按 SelectorBar 选择懒加载。
    public sealed class BookshelfPageViewModel : INotifyPropertyChanged
    {
        public HistoryPageViewModel History { get; } = new();
        public FavoritesPageViewModel Favorites { get; } = new();
        public LikesPageViewModel Likes { get; } = new();

        private readonly bool[] _loaded = new bool[3];

        private int _selectedIndex;
        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                if (_selectedIndex == value) return;
                _selectedIndex = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsHistorySelected));
                OnPropertyChanged(nameof(IsFavoritesSelected));
                OnPropertyChanged(nameof(IsLikesSelected));
            }
        }

        public bool IsHistorySelected => SelectedIndex == 0;
        public bool IsFavoritesSelected => SelectedIndex == 1;
        public bool IsLikesSelected => SelectedIndex == 2;

        public async Task EnsureLoadedAsync(int index)
        {
            if (index < 0 || index >= _loaded.Length || _loaded[index]) return;
            _loaded[index] = true;
            await LoadByIndexAsync(index);
        }

        public async Task ReloadCurrentAsync()
        {
            _loaded[SelectedIndex] = true;
            await LoadByIndexAsync(SelectedIndex);
        }

        // 登录 / 退出后调用:清空懒加载标记,重新加载当前页,其余页切换时再刷新。
        public async Task RefreshForAuthChangeAsync()
        {
            for (var i = 0; i < _loaded.Length; i++) _loaded[i] = false;
            await EnsureLoadedAsync(SelectedIndex);
        }

        private Task LoadByIndexAsync(int index) => index switch
        {
            0 => History.LoadAsync(),
            1 => Favorites.LoadAsync(),
            2 => Likes.LoadAsync(),
            _ => Task.CompletedTask,
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
