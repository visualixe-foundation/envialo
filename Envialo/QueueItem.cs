using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Envialo
{
    // Represents a single file transfer shown in the queue list.
    // Implements INotifyPropertyChanged so the UI updates live as
    // Status/Progress change during the transfer, without needing
    // to manually refresh the ListBox.
    public class QueueItem : INotifyPropertyChanged
    {
        private string status = "Queued";
        private int progress = 0;

        public string FileName { get; set; }
        public string TargetDevice { get; set; }

        public string Status
        {
            get => status;
            set { status = value; OnPropertyChanged(); }
        }

        public int Progress
        {
            get => progress;
            set { progress = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
