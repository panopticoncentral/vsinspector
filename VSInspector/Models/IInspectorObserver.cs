namespace VSInspector.Models
{
    internal interface IInspectorObserver
    {
        void NotifyProcessStart(ProcessModel processModel);
    }
}
