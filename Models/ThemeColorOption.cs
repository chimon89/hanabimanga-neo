using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace hanabimanga.Models
{
    public sealed class ThemeColorOption : INotifyPropertyChanged
    {
        private bool _isSelected;

        public ThemeColorOption(string id, string name, uint lightRgb, uint darkRgb)
        {
            Id = id;
            Name = name;
            LightArgb = 0xFF000000u | lightRgb;
            DarkArgb = 0xFF000000u | darkRgb;
            Swatch = new SolidColorBrush(ToColor(LightArgb));
        }

        public string Id { get; }
        public string Name { get; }
        public uint LightArgb { get; }
        public uint DarkArgb { get; }
        public SolidColorBrush Swatch { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        private static Color ToColor(uint argb) => Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF));

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
