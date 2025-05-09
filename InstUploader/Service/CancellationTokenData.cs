namespace InstUploader.Service;

public class CancellationTokenData(DateTime expiresAt)
{
    public CancellationTokenSource CancellationTokenSource { get; } = new();
    public DateTime ExpiresAt { get; } = expiresAt;
}