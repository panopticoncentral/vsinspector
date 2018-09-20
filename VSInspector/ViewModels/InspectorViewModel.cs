using System.Collections.ObjectModel;
using System.Windows;
using VSInspector.Models;

namespace VSInspector.ViewModels
{
    internal sealed class InspectorViewModel : ViewModel, IInspectorObserver
    {
        public ObservableCollection<ProcessViewModel> Processes { get; } = new ObservableCollection<ProcessViewModel>();

        public InspectorViewModel()
        {
            InspectorModel.Instance.AddObserver(this);
        }

        void IInspectorObserver.NotifyProcessStart(ProcessModel processModel)
        {
            var processViewModel = new ProcessViewModel(processModel);
            Application.Current.Dispatcher.InvokeAsync(() => Processes.Add(processViewModel));
        }
    }
}
