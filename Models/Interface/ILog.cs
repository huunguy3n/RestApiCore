namespace Models.Interface
{
    public interface ILog
    {
        DateTime timeStamp { get; set; }
        string ip { get; set; }
        string controller { get; set; }
    }
}
