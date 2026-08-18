namespace ProxyDiscord.Domain.Exceptions;

public sealed class AddressParseException : Exception
{
    public AddressParseException(string message) : base(message)
    {
    }
}
