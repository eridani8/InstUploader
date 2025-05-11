namespace InstUploader;

public class AppConfiguration
{
    public string Code { get; set; } = string.Empty;
    public string AdbPath { get; set; } = string.Empty;
    public string DirectoriesPath { get; set; } = string.Empty;
    public LoadFileMode FileMode { get; set; } = LoadFileMode.Default;
}