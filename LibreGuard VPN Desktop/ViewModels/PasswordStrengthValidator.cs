namespace LibreGuard_VPN_Desktop.ViewModels;

/// <summary>
/// Tracks password strength requirements.
/// </summary>
public sealed record PasswordStrengthValidator
{
    /// <summary>
    /// Password is at least 8 characters long.
    /// </summary>
    public bool HasMinimumLength { get; init; }

    /// <summary>
    /// Password contains at least one uppercase letter.
    /// </summary>
    public bool HasUpperCase { get; init; }

    /// <summary>
    /// Password contains at least one lowercase letter.
    /// </summary>
    public bool HasLowerCase { get; init; }

    /// <summary>
    /// Password contains at least one digit.
    /// </summary>
    public bool HasDigit { get; init; }

    /// <summary>
    /// Password contains at least one special character.
    /// </summary>
    public bool HasSpecialChar { get; init; }

    /// <summary>
    /// Gets whether all password requirements are met.
    /// </summary>
    public bool IsValid => HasMinimumLength && HasDigit && HasSpecialChar;

    /// <summary>
    /// Gets a score from 0-100 based on met requirements.
    /// </summary>
    public int Score => (HasMinimumLength ? 30 : 0) +
                        (HasDigit ? 30 : 0) +
                        (HasSpecialChar ? 30 : 0) +
                        (HasUpperCase ? 5 : 0) +
                        (HasLowerCase ? 5 : 0);
}
