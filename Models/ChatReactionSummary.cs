using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SteamPluginManager.Models
{
    public sealed class ChatReactionSummary : INotifyPropertyChanged
    {
        private int _count;
        private bool _isSelected;

        public event PropertyChangedEventHandler? PropertyChanged;

        public long MessageId { get; init; }
        public string Emoji { get; init; } = string.Empty;

        public int Count
        {
            get => _count;
            set
            {
                if (_count != value)
                {
                    _count = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
