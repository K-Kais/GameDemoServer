using GameDemoServer.Models;
using GameDemoServer.Services;
using Microsoft.AspNetCore.Mvc;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace GameDemoServer.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class AuthController : ControllerBase
{
    private readonly AuthService _authService;
    private readonly TokenService _tokenService;

    public AuthController(AuthService authService, TokenService tokenService)
    {
        _authService = authService;
        _tokenService = tokenService;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken cancellationToken)
    {
        var result = await _authService.RegisterAsync(request, cancellationToken);
        if (!result.Success)
        {
            return BadRequest(new { message = result.Error });
        }

        return Ok(result.Data);
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await _authService.LoginAsync(request, cancellationToken);
        if (!result.Success)
        {
            return BadRequest(new { message = result.Error });
        }

        return Ok(result.Data);
    }

    [HttpPost("create-character")]
    public async Task<IActionResult> CreateCharacter([FromBody] CreateCharacterRequest request, CancellationToken cancellationToken)
    {
        var token = ExtractBearerToken();
        var principal = _tokenService.ValidateToken(token);
        if (principal is null)
        {
            return Unauthorized(new { message = "Phiên đăng nhập hết hạn, vui lòng thử lại!" });
        }

        var userId = principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ??
            principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(new { message = "Phiên đăng nhập hết hạn, vui lòng thử lại!" });
        }

        var result = await _authService.CreateCharacterAsync(userId, request, cancellationToken);
        if (!result.Success)
        {
            return BadRequest(new { message = result.Error });
        }

        return Ok(result.Data);
    }

    private string ExtractBearerToken()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return string.Empty;
        }

        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : header.Trim();
    }
}
