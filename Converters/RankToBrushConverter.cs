using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace hanabimanga.Converters
{
    // 排名角标背景:前三名用系统强调色,其余用半透明深色(覆盖在封面上保证可读性)。
    public class RankToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var rank = value is int i ? i : 0;
            if (rank is >= 1 and <= 3 &&
                Application.Current.Resources.TryGetValue("SystemAccentColor", out var accent) &&
                accent is Color accentColor)
            {
                return new SolidColorBrush(accentColor);
            }

            return new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x00, 0x00));
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}
