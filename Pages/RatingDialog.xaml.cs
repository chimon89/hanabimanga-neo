using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace hanabimanga.Pages
{
    public sealed partial class RatingDialog : ContentDialog
    {
        private const int MaxScore = 10;
        private const string FilledStar = "";
        private const string OutlineStar = "";

        public int SelectedScore { get; private set; }

        public RatingDialog(int? initialScore)
        {
            InitializeComponent();
            BuildStars();
            SelectedScore = initialScore is >= 1 and <= MaxScore ? initialScore.Value : 0;
            UpdateStars();
        }

        private void BuildStars()
        {
            for (var i = 1; i <= MaxScore; i++)
            {
                var button = new Button
                {
                    Tag = i,
                    Width = 30,
                    Height = 36,
                    Padding = new Thickness(0),
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Content = new FontIcon { Glyph = OutlineStar, FontSize = 20 },
                };
                button.Click += Star_Click;
                StarsPanel.Children.Add(button);
            }
        }

        private void Star_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: int score })
            {
                SelectedScore = score;
                UpdateStars();
            }
        }

        private void UpdateStars()
        {
            for (var i = 0; i < StarsPanel.Children.Count; i++)
            {
                if (StarsPanel.Children[i] is Button { Content: FontIcon icon })
                {
                    icon.Glyph = (i + 1) <= SelectedScore ? FilledStar : OutlineStar;
                }
            }

            ScoreTextBlock.Text = SelectedScore >= 1
                ? $"{SelectedScore} 分"
                : "点击星星选择评分（1-10 分）";
            IsPrimaryButtonEnabled = SelectedScore >= 1;
        }
    }
}
