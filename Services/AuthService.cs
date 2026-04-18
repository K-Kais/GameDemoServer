using System.Globalization;
using System.Text.RegularExpressions;
using GameDemoServer.Models;
using Npgsql;

namespace GameDemoServer.Services;

public sealed class AuthService
{
    private static readonly Regex UserNameRegex = new("^[a-zA-Z0-9_]{3,32}$", RegexOptions.Compiled);
    private readonly NpgsqlDataSource _dataSource;
    private readonly TokenService _tokenService;

    public AuthService(NpgsqlDataSource dataSource, TokenService tokenService)
    {
        _dataSource = dataSource;
        _tokenService = tokenService;
    }

    public async Task<AuthActionResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        var normalizedUserName = NormalizeUserName(request.UserName);
        if (!UserNameRegex.IsMatch(normalizedUserName))
        {
            return AuthActionResult.Failed("Username must be 3-32 chars and only contain letters, numbers, underscore.");
        }

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 6 || request.Password.Length > 100)
        {
            return AuthActionResult.Failed("Password must be 6-100 characters.");
        }

        var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);

        const string sql = """
            INSERT INTO users(username, password_hash)
            VALUES(@username, @password_hash)
            RETURNING id;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("username", normalizedUserName);
        command.Parameters.AddWithValue("password_hash", passwordHash);

        try
        {
            var idObject = await command.ExecuteScalarAsync(cancellationToken);
            if (idObject is null)
            {
                return AuthActionResult.Failed("Cannot create user.");
            }

            var userId = Convert.ToString(idObject, CultureInfo.InvariantCulture) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(userId))
            {
                return AuthActionResult.Failed("Cannot create user.");
            }

            var token = _tokenService.GenerateToken(userId, normalizedUserName);
            return AuthActionResult.Succeeded(new AuthResponse(token, userId, normalizedUserName));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return AuthActionResult.Failed("Username already exists.");
        }
    }

    public async Task<AuthActionResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var normalizedUserName = NormalizeUserName(request.UserName);
        if (!UserNameRegex.IsMatch(normalizedUserName))
        {
            return AuthActionResult.Failed("Invalid username or password.");
        }

        const string sql = """
            SELECT id, password_hash
            FROM users
            WHERE username = @username
            LIMIT 1;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("username", normalizedUserName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return AuthActionResult.Failed("Invalid username or password.");
        }

        var userId = reader.GetInt64(0).ToString(CultureInfo.InvariantCulture);
        var passwordHash = reader.GetString(1);

        if (!BCrypt.Net.BCrypt.Verify(request.Password, passwordHash))
        {
            return AuthActionResult.Failed("Invalid username or password.");
        }

        var token = _tokenService.GenerateToken(userId, normalizedUserName);
        return AuthActionResult.Succeeded(new AuthResponse(token, userId, normalizedUserName));
    }

    private static string NormalizeUserName(string userName)
    {
        return userName.Trim().ToLowerInvariant();
    }
}
