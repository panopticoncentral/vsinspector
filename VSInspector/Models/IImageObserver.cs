namespace VSInspector.Models
{
    internal interface IImageObserver
    {
        void NotifyStatusChanged(ImageStatus status);
    }
}
