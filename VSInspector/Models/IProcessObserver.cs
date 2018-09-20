namespace VSInspector.Models
{
    internal interface IProcessObserver
    {
        void NotifyWindowTitleChanged(string windowTitle);
        void NotifyStatusChanged(ProcessStatus status);
        void NotifyImageLoad(ImageModel imageModel);
    }
}
