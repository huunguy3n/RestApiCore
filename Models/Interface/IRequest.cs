namespace Models.Interface
{
    public interface IRequest
    {
        DateTime timeStamp { get; }
        string ipAddress { get; set; }
        string userName { get; set; }
    }
}
