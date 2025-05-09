namespace InstUploader.Service;

public class CancellationTokenData(DateTime expiresAt)
{
    public CancellationTokenSource CancellationTokenSource { get; set; } = new();
    public DateTime ExpiresAt { get; set; } = expiresAt;
}