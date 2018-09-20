using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VSInspector.ViewModels
{
    internal abstract class ViewModel : INotifyPropertyChanged, IDisposable
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void NotifyPropertyChanged([CallerMemberName] string methodName = "") =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(methodName));

        public virtual void Dispose()
        {
        }
    }
}
