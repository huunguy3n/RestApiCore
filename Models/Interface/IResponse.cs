namespace Models.Interface
{
    public interface IResponse
    {
        string code { get; set; }
        bool success { get; set; }
        string message { get; set; }
    }
}
