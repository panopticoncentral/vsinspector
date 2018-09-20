using System.Collections.ObjectModel;
using System.Windows;
using VSInspector.Models;

namespace VSInspector.ViewModels
{
    internal sealed class ProcessViewModel : ViewModel, IProcessObserver
    {
        private ProcessStatus _status;
        private string _windowTitle;

        public int ProcessId { get; }

        public ObservableCollection<ImageViewModel> Images { get; } = new ObservableCollection<ImageViewModel>();

        public ProcessStatus Status
        {
            get => _status;
            private set
            {
                _status = value;
                NotifyPropertyChanged();
            }
        }

        public string WindowTitle
        {
            get => _windowTitle;
            private set
            {
                _windowTitle = value;
                NotifyPropertyChanged();
            }
        }

        public ProcessViewModel(ProcessModel processModel)
        {
            ProcessId = processModel.ProcessId;
            processModel.AddObserver(this);
        }

        void IProcessObserver.NotifyWindowTitleChanged(string windowTitle)
        {
            Application.Current.Dispatcher.InvokeAsync(() => WindowTitle = windowTitle);
        }

        void IProcessObserver.NotifyStatusChanged(ProcessStatus status)
        {
            Application.Current.Dispatcher.InvokeAsync(() => Status = status);
        }

        void IProcessObserver.NotifyImageLoad(ImageModel model)
        {
            var imageViewModel = new ImageViewModel(model);
            Application.Current.Dispatcher.InvokeAsync(() => Images.Add(imageViewModel));
        }
    }
}
