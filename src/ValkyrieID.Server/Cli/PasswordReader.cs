using System.Text;

namespace ValkyrieID.Server.Cli;

/// <summary>
/// Reads a new password for CLI commands. Passwords are never taken from arguments, which end up in shell
/// history and process listings: they are typed at a hidden prompt, or read as one line from redirected input.
/// </summary>
internal static class PasswordReader
{
    public static async Task<string> ReadNewPasswordAsync(TextReader input)
    {
        if (ReferenceEquals(input, Console.In) && !Console.IsInputRedirected)
        {
            var password = Prompt("New password: ");
            if (Prompt("Confirm password: ") != password)
            {
                throw new CliException("The passwords don't match.");
            }

            return Required(password);
        }

        return Required(await input.ReadLineAsync());
    }

    private static string Required(string? password) =>
        string.IsNullOrEmpty(password) ? throw new CliException("No password was given.") : password;

    private static string Prompt(string prompt)
    {
        Console.Error.Write(prompt);
        var password = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return password.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
            }
        }
    }
}
