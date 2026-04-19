using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using GameDemoServer.Models;

namespace GameDemoServer.Services;

public sealed class AuthService
{
    private const int AccountPasswordMinLength = 6;
    private const int AccountPasswordMaxLength = 30;
    private const int CharacterNameMinLength = 4;
    private const int CharacterNameMaxLength = 12;
    private const int FailedLoginLimit = 30;
    private static readonly TimeSpan FailedWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LoginBlockDuration = TimeSpan.FromMinutes(15);
    private static readonly Regex AlphaNumericRegex = new("^[a-zA-Z0-9]+$", RegexOptions.Compiled);
    private readonly ILogger<AuthService> _logger;
    private readonly TokenService _tokenService;
    private readonly SemaphoreSlim _storageLock = new(1, 1);
    private readonly string _storageFilePath;
    private readonly ConcurrentDictionary<string, FailedLoginTracker> _failedLoginByAccount = new();

    public AuthService(IHostEnvironment hostEnvironment, TokenService tokenService, ILogger<AuthService> logger)
    {
        _logger = logger;
        _tokenService = tokenService;
        var dataDirectory = Path.Combine(hostEnvironment.ContentRootPath, "Data");
        Directory.CreateDirectory(dataDirectory);
        _storageFilePath = Path.Combine(dataDirectory, "users.txt");
        if (!File.Exists(_storageFilePath))
        {
            File.WriteAllText(_storageFilePath, string.Empty);
        }
    }

    public async Task<ActionResultModel> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        var emptyValidationError = ValidateRegisterNotEmpty(request);
        if (!string.IsNullOrEmpty(emptyValidationError))
        {
            return ActionResultModel.Failed(emptyValidationError);
        }

        var accountPasswordValidationError = ValidateAccountPassword(request.UserName, request.Password);
        if (!string.IsNullOrEmpty(accountPasswordValidationError))
        {
            return ActionResultModel.Failed(accountPasswordValidationError);
        }

        if (!string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal))
        {
            return ActionResultModel.Failed("Mật khẩu và Mật khẩu xác nhận cần trùng nhau");
        }

        if (!request.AcceptedTerms)
        {
            return ActionResultModel.Failed("Bạn cần \"Đồng ý điều khoản\" để thực hiện");
        }

        var normalizedUserName = NormalizeAccount(request.UserName);
        await _storageLock.WaitAsync(cancellationToken);
        try
        {
            var users = await ReadUsersAsync(cancellationToken);
            if (users.Exists(user => string.Equals(user.UserName, normalizedUserName, StringComparison.Ordinal)))
            {
                return ActionResultModel.Failed("Tài khoản này đã có người khác đăng ký");
            }

            var userId = GetNextUserId(users);
            var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);
            users.Add(new StoredUser
            {
                UserId = userId,
                UserName = normalizedUserName,
                PasswordHash = passwordHash
            });
            await WriteUsersAsync(users, cancellationToken);
            return ActionResultModel.Succeeded("Đăng ký thành công");
        }
        finally
        {
            _storageLock.Release();
        }
    }

    public async Task<AuthActionResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var emptyValidationError = ValidateLoginNotEmpty(request);
        if (!string.IsNullOrEmpty(emptyValidationError))
        {
            return AuthActionResult.Failed(emptyValidationError);
        }

        var accountPasswordValidationError = ValidateAccountPassword(request.UserName, request.Password);
        if (!string.IsNullOrEmpty(accountPasswordValidationError))
        {
            return AuthActionResult.Failed(accountPasswordValidationError);
        }

        var normalizedUserName = NormalizeAccount(request.UserName);
        var lockMessage = TryGetLoginBlockMessage(normalizedUserName);
        if (!string.IsNullOrEmpty(lockMessage))
        {
            return AuthActionResult.Failed(lockMessage);
        }

        StoredUser? matchedUser;
        await _storageLock.WaitAsync(cancellationToken);
        try
        {
            var users = await ReadUsersAsync(cancellationToken);
            matchedUser = users.Find(user => string.Equals(user.UserName, normalizedUserName, StringComparison.Ordinal));
        }
        finally
        {
            _storageLock.Release();
        }

        if (matchedUser is null || !BCrypt.Net.BCrypt.Verify(request.Password, matchedUser.PasswordHash))
        {
            lockMessage = RecordFailedLogin(normalizedUserName);
            if (!string.IsNullOrEmpty(lockMessage))
            {
                return AuthActionResult.Failed(lockMessage);
            }

            return AuthActionResult.Failed("Tài khoản hoặc mật khẩu không chính xác!");
        }

        _failedLoginByAccount.TryRemove(normalizedUserName, out _);
        return AuthActionResult.Succeeded(BuildAuthResponse(matchedUser));
    }

    public async Task<AuthActionResult> CreateCharacterAsync(string userId, CreateCharacterRequest request, CancellationToken cancellationToken = default)
    {
        var validationError = ValidateCharacterName(request.CharacterName);
        if (!string.IsNullOrEmpty(validationError))
        {
            return AuthActionResult.Failed(validationError);
        }

        var normalizedCharacterName = NormalizeCharacterName(request.CharacterName);

        await _storageLock.WaitAsync(cancellationToken);
        try
        {
            var users = await ReadUsersAsync(cancellationToken);
            var userIndex = users.FindIndex(user => string.Equals(user.UserId, userId, StringComparison.Ordinal));
            if (userIndex < 0)
            {
                return AuthActionResult.Failed("Phiên đăng nhập hết hạn, vui lòng thử lại!");
            }

            var currentUser = users[userIndex];
            if (!string.IsNullOrWhiteSpace(currentUser.CharacterName))
            {
                return AuthActionResult.Failed("Tài khoản đã có nhân vật");
            }

            var duplicated = users.Exists(user =>
                !string.Equals(user.UserId, userId, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(user.NormalizedCharacterName) &&
                string.Equals(user.NormalizedCharacterName, normalizedCharacterName, StringComparison.Ordinal));
            if (duplicated)
            {
                return AuthActionResult.Failed("Tên nhân vật đã tồn tại");
            }

            currentUser.CharacterName = request.CharacterName.Trim();
            currentUser.NormalizedCharacterName = normalizedCharacterName;
            users[userIndex] = currentUser;
            await WriteUsersAsync(users, cancellationToken);
            return AuthActionResult.Succeeded(BuildAuthResponse(currentUser));
        }
        finally
        {
            _storageLock.Release();
        }
    }

    public async Task<bool> TryGetUserProfileAsync(string userId, CancellationToken cancellationToken, Action<AuthUserProfile> onFound)
    {
        await _storageLock.WaitAsync(cancellationToken);
        try
        {
            var users = await ReadUsersAsync(cancellationToken);
            var user = users.Find(item => string.Equals(item.UserId, userId, StringComparison.Ordinal));
            if (user is null)
            {
                return false;
            }

            onFound(new AuthUserProfile(
                user.UserId,
                user.UserName,
                user.CharacterName ?? string.Empty,
                user.NormalizedCharacterName ?? string.Empty));
            return true;
        }
        finally
        {
            _storageLock.Release();
        }
    }

    private static string ValidateLoginNotEmpty(LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
        {
            return "Bạn cần điền đầy đủ thông tin trước khi đăng nhập";
        }

        return string.Empty;
    }

    private static string ValidateRegisterNotEmpty(RegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserName) ||
            string.IsNullOrWhiteSpace(request.Password) ||
            string.IsNullOrWhiteSpace(request.ConfirmPassword))
        {
            return "Bạn cần điền đầy đủ thông tin trước khi đăng ký";
        }

        return string.Empty;
    }

    private static string ValidateAccountPassword(string userName, string password)
    {
        if (userName.Length < AccountPasswordMinLength || userName.Length > AccountPasswordMaxLength)
        {
            return "Tài khoản chỉ chấp nhận độ dài 6 - 30 ký tự";
        }

        if (password.Length < AccountPasswordMinLength || password.Length > AccountPasswordMaxLength)
        {
            return "Mật khẩu chỉ chấp nhận độ dài 6 - 30 ký tự";
        }

        if (!AlphaNumericRegex.IsMatch(userName) || !AlphaNumericRegex.IsMatch(password))
        {
            return "Tài khoản, mật khẩu chỉ chấp nhận ký tự a - z, A - Z, 0 - 9";
        }

        return string.Empty;
    }

    private static string ValidateCharacterName(string characterName)
    {
        var trimmed = characterName?.Trim() ?? string.Empty;
        if (trimmed.Length < CharacterNameMinLength || trimmed.Length > CharacterNameMaxLength)
        {
            return "Tên nhân vật chỉ chấp nhận 4 - 12 ký tự!";
        }

        if (!AlphaNumericRegex.IsMatch(trimmed))
        {
            return "Tên nhân vật chỉ chấp nhận ký tự 0 - 9, a - z, A - Z";
        }

        return string.Empty;
    }

    private string TryGetLoginBlockMessage(string normalizedUserName)
    {
        if (!_failedLoginByAccount.TryGetValue(normalizedUserName, out var tracker))
        {
            return string.Empty;
        }

        lock (tracker.SyncLock)
        {
            var now = DateTime.UtcNow;
            if (tracker.BlockedUntilUtc <= now)
            {
                tracker.BlockedUntilUtc = DateTime.MinValue;
                while (tracker.FailedAtUtc.Count > 0 && now - tracker.FailedAtUtc.Peek() > FailedWindow)
                {
                    tracker.FailedAtUtc.Dequeue();
                }

                if (tracker.FailedAtUtc.Count == 0)
                {
                    _failedLoginByAccount.TryRemove(normalizedUserName, out _);
                }

                return string.Empty;
            }

            var remainMinutes = Math.Max(1, (int)Math.Ceiling((tracker.BlockedUntilUtc - now).TotalMinutes));
            return $"Bạn đăng nhập sai quá nhiều lần, thử lại sau {remainMinutes} phút";
        }
    }

    private string RecordFailedLogin(string normalizedUserName)
    {
        var tracker = _failedLoginByAccount.GetOrAdd(normalizedUserName, _ => new FailedLoginTracker());
        lock (tracker.SyncLock)
        {
            var now = DateTime.UtcNow;
            while (tracker.FailedAtUtc.Count > 0 && now - tracker.FailedAtUtc.Peek() > FailedWindow)
            {
                tracker.FailedAtUtc.Dequeue();
            }

            tracker.FailedAtUtc.Enqueue(now);
            if (tracker.FailedAtUtc.Count <= FailedLoginLimit)
            {
                return string.Empty;
            }

            tracker.FailedAtUtc.Clear();
            tracker.BlockedUntilUtc = now.Add(LoginBlockDuration);
            return $"Bạn đăng nhập sai quá nhiều lần, thử lại sau {(int)LoginBlockDuration.TotalMinutes} phút";
        }
    }

    private AuthResponse BuildAuthResponse(StoredUser user)
    {
        var displayName = string.IsNullOrWhiteSpace(user.CharacterName) ? user.UserName : user.CharacterName;
        var token = _tokenService.GenerateToken(user.UserId, displayName);
        return new AuthResponse(
            token,
            user.UserId,
            user.UserName,
            string.IsNullOrWhiteSpace(user.CharacterName),
            user.CharacterName ?? string.Empty);
    }

    private static string NormalizeAccount(string userName)
    {
        return userName.Trim().ToLowerInvariant();
    }

    private static string NormalizeCharacterName(string characterName)
    {
        return characterName.Trim().ToLowerInvariant();
    }

    private async Task<List<StoredUser>> ReadUsersAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_storageFilePath))
        {
            return new List<StoredUser>();
        }

        var lines = await File.ReadAllLinesAsync(_storageFilePath, cancellationToken);
        var users = new List<StoredUser>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split('\t');
            if (parts.Length < 3 ||
                string.IsNullOrWhiteSpace(parts[0]) ||
                string.IsNullOrWhiteSpace(parts[1]) ||
                string.IsNullOrWhiteSpace(parts[2]))
            {
                _logger.LogWarning("Ignoring malformed user record at line {LineNumber}", i + 1);
                continue;
            }

            var characterName = parts.Length > 3 ? parts[3] : string.Empty;
            var normalizedCharacterName = parts.Length > 4 ? parts[4] : NormalizeCharacterName(characterName);
            if (string.IsNullOrWhiteSpace(characterName))
            {
                normalizedCharacterName = string.Empty;
            }

            users.Add(new StoredUser
            {
                UserId = parts[0],
                UserName = NormalizeAccount(parts[1]),
                PasswordHash = parts[2],
                CharacterName = characterName,
                NormalizedCharacterName = normalizedCharacterName
            });
        }

        return users;
    }

    private async Task WriteUsersAsync(IReadOnlyList<StoredUser> users, CancellationToken cancellationToken)
    {
        var lines = new string[users.Count];
        for (var i = 0; i < users.Count; i++)
        {
            var user = users[i];
            lines[i] = string.Join('\t',
                user.UserId,
                user.UserName,
                user.PasswordHash,
                user.CharacterName ?? string.Empty,
                user.NormalizedCharacterName ?? string.Empty);
        }

        await File.WriteAllLinesAsync(_storageFilePath, lines, cancellationToken);
    }

    private static string GetNextUserId(List<StoredUser> users)
    {
        long maxId = 0;
        for (var i = 0; i < users.Count; i++)
        {
            if (long.TryParse(users[i].UserId, out var parsedId) && parsedId > maxId)
            {
                maxId = parsedId;
            }
        }

        return (maxId + 1).ToString();
    }

    private sealed class FailedLoginTracker
    {
        public object SyncLock { get; } = new();
        public Queue<DateTime> FailedAtUtc { get; } = new();
        public DateTime BlockedUntilUtc { get; set; } = DateTime.MinValue;
    }

    private sealed class StoredUser
    {
        public string UserId { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string CharacterName { get; set; } = string.Empty;
        public string NormalizedCharacterName { get; set; } = string.Empty;
    }
}

public sealed record AuthUserProfile(
    string UserId,
    string UserName,
    string CharacterName,
    string NormalizedCharacterName);
