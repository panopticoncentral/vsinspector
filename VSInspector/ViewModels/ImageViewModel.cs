using System.IO;
using System.Windows;
using VSInspector.Models;

namespace VSInspector.ViewModels
{
    internal sealed class ImageViewModel : ViewModel, IImageObserver
    {
        private ImageStatus _status;

        public string FileName { get; }
        public int LoadOrder { get; }
        public string FilePath { get; }
        public string ImageBase { get; }

        public ImageStatus Status
        {
            get => _status;
            set
            {
                _status = value;
                NotifyPropertyChanged();
            }
        }

        public ImageViewModel(ImageModel imageModel)
        {
            FileName = Path.GetFileName(imageModel.FileName);
            FilePath = Path.GetFullPath(imageModel.FileName);
            LoadOrder = imageModel.LoadOrder;
            ImageBase = $"0x{imageModel.ImageBase:X16}";
            imageModel.AddObserver(this);
        }

        void IImageObserver.NotifyStatusChanged(ImageStatus status)
        {
            Application.Current.Dispatcher.InvokeAsync(() => Status = status);
        }
    }
}
