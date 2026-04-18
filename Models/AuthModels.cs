namespace GameDemoServer.Models;

public sealed class RegisterRequest
{
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class LoginRequest
{
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed record AuthResponse(string Token, string UserId, string UserName);

public sealed record AuthActionResult(bool Success, string? Error, AuthResponse? Data)
{
    public static AuthActionResult Failed(string error) => new(false, error, null);
    public static AuthActionResult Succeeded(AuthResponse data) => new(true, null, data);
}
