using System.Text;

namespace AgentForge.MintSessionCookie;

/// <summary>
/// An OpenEMR username and password, held only in memory. A class rather than a record so that no
/// compiler-generated <c>ToString</c> can ever print the password.
/// </summary>
internal sealed class LoginCredentials
{
    public const string UsernameVariable = "MintSession__Username";
    public const string PasswordVariable = "MintSession__Password";

    public LoginCredentials(string username, string password)
    {
        Username = username;
        Password = password;
    }

    public string Username { get; }
    public string Password { get; }

    public override string ToString() => $"{nameof(LoginCredentials)} {{ Username = {Username}, Password = *** }}";

    /// <summary>
    /// Environment first; otherwise prompt on the terminal (prompts to stderr, so stdout carries only the
    /// cookie). Returns null plus a reason when neither source can supply both values.
    /// </summary>
    public static (LoginCredentials? Credentials, string? Error) Resolve(
        Func<string, string?> environment, bool interactive, TextWriter prompt, Func<string?> readLine, Func<string> readSecret)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(readLine);
        ArgumentNullException.ThrowIfNull(readSecret);

        var username = environment(UsernameVariable);
        var password = environment(PasswordVariable);

        if (string.IsNullOrEmpty(username))
        {
            if (!interactive)
            {
                return (null, $"No {UsernameVariable} set and no terminal to prompt on.");
            }

            prompt.Write("OpenEMR username: ");
            username = readLine();
        }

        if (string.IsNullOrEmpty(password))
        {
            if (!interactive)
            {
                return (null, $"No {PasswordVariable} set and no terminal to prompt on.");
            }

            prompt.Write("OpenEMR password (not echoed): ");
            password = readSecret();
            prompt.WriteLine();
        }

        return string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)
            ? (null, "An empty username or password was supplied.")
            : (new LoginCredentials(username, password), null);
    }

    /// <summary>Reads a line from the console without echoing it.</summary>
    public static string ReadSecretFromConsole()
    {
        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }
    }
}
