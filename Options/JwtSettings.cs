namespace GameDemoServer.Options;

public sealed class JwtSettings
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "GameDemoServer";
    public string Audience { get; set; } = "GameDemoClient";
    public string SecretKey { get; set; } = string.Empty;
    public int ExpiryMinutes { get; set; } = 120;
}
