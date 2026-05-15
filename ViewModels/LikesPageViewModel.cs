using System.Collections.Generic;
using hanabimanga.Models;

namespace hanabimanga.ViewModels
{
    public sealed class LikesPageViewModel : UserListPageViewModel
    {
        public string PageTitle => "点赞";
        public string PageSubtitle => "你点过赞的漫画";
        public string EmptyHint => "还没有点赞过任何漫画";
        public string SignInHint => "登录后查看你的点赞";

        protected override IEnumerable<BookshelfComicItem> PickList(BookshelfDocument doc)
            => doc.Likes;
    }
}
