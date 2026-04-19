namespace GameDemoServer.Models;

public sealed class RegisterRequest
{
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
    public bool AcceptedTerms { get; set; }
}

public sealed class LoginRequest
{
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class CreateCharacterRequest
{
    public string CharacterName { get; set; } = string.Empty;
}

public sealed record AuthResponse(
    string Token,
    string UserId,
    string UserName,
    bool RequiresCharacterCreation,
    string CharacterName);

public sealed record ActionResponse(string Message);

public sealed record AuthActionResult(bool Success, string? Error, AuthResponse? Data)
{
    public static AuthActionResult Failed(string error) => new(false, error, null);
    public static AuthActionResult Succeeded(AuthResponse data) => new(true, null, data);
}

public sealed record ActionResultModel(bool Success, string? Error, ActionResponse? Data)
{
    public static ActionResultModel Failed(string error) => new(false, error, null);
    public static ActionResultModel Succeeded(string message) => new(true, null, new ActionResponse(message));
}
