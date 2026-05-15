using System.Collections.Generic;
using hanabimanga.Models;

namespace hanabimanga.ViewModels
{
    public sealed class FavoritesPageViewModel : UserListPageViewModel
    {
        public string PageTitle => "收藏";
        public string PageSubtitle => "你收藏过的漫画";
        public string EmptyHint => "还没有收藏任何漫画";
        public string SignInHint => "登录后查看你的收藏";

        protected override IEnumerable<BookshelfComicItem> PickList(BookshelfDocument doc)
            => doc.Favorites;
    }
}
