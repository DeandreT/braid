using System.CommandLine;
using System.Globalization;
using Weft.Cli.Output;

namespace Weft.Cli.Commands;

internal static class CommandSupport
{
    /// <summary>
    /// Runs the command and maps its outcome to a stable exit code. Only fixed CLI messages are written; an
    /// unexpected failure reports its type, never its message or any response content.
    /// </summary>
    public static void Handle(this Command command, CliGlobals globals, Func<CliInvocation, Task<int>> action) =>
        command.SetAction(async (parse, cancellationToken) =>
        {
            using var invocation = new CliInvocation(globals, parse, cancellationToken);
            try
            {
                return await action(invocation);
            }
            catch (CliException error)
            {
                return Report(globals.Environment, error);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Report(globals.Environment, new CliException(CliExitCode.Canceled, "Canceled.", "canceled"));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return Report(globals.Environment, new CliException(CliExitCode.Failure,
                    $"An unexpected client failure occurred ({error.GetType().Name}).", "failure"));
            }
        });

    public static void Handle(this Command command, CliGlobals globals, Func<CliInvocation, Task> action) =>
        command.Handle(globals, async invocation =>
        {
            await action(invocation);
            return (int)CliExitCode.Success;
        });

    internal static int Report(CliEnvironment environment, CliException error)
    {
        environment.Error.WriteLine("error: " + CliRedaction.Text(error.Message));
        if (error.RetryAfterSeconds is { } seconds)
            environment.Error.WriteLine($"The server asked to retry after {seconds.ToString(CultureInfo.InvariantCulture)} s.");
        environment.Error.Flush();
        return (int)error.Code;
    }

    public static Command With(this Command command, params Symbol[] symbols)
    {
        foreach (var symbol in symbols)
            switch (symbol)
            {
                case Option option:
                    command.Options.Add(option);
                    break;
                case Argument argument:
                    command.Arguments.Add(argument);
                    break;
                case Command child:
                    command.Subcommands.Add(child);
                    break;
            }
        return command;
    }

    public static Option<string> Required(string name, string description) => new(name) { Description = description, Required = true };
    public static Option<string?> Optional(string name, string description) => new(name) { Description = description };
    public static Argument<string> Argument(string name, string description) => new(name) { Description = description };

    /// <summary>Reads a whole file, or standard input for <c>-</c>, refusing more than <paramref name="maximumBytes"/>.</summary>
    public static async Task<byte[]> ReadInputAsync(CliInvocation invocation, string path, int maximumBytes, string what)
    {
        Stream stream;
        try
        {
            stream = path == "-" ? invocation.Environment.OpenStandardInput()
                : invocation.Environment.OpenInputFile(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw CliException.Usage($"The {what} file {path} cannot be read.");
        }
        await using (stream)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, invocation.Cancellation)) > 0)
            {
                if (buffer.Length + read > maximumBytes)
                    throw CliException.Usage($"The {what} is larger than the {maximumBytes.ToString(CultureInfo.InvariantCulture)}-byte limit.");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
    }

    /// <summary>A display filename for an upload: the file's own name, or none for standard input.</summary>
    public static string? FileName(string path) => path == "-" ? null : Path.GetFileName(path);

    /// <summary>Polls until <paramref name="step"/> returns a value, with a growing interval and an overall deadline.</summary>
    public static async Task<T> PollAsync<T>(CliInvocation invocation, TimeSpan timeout, string what, Func<Task<T?>> step,
        Func<TimeSpan?>? advised = null) where T : class
    {
        var time = invocation.Environment.Time;
        var deadline = time.GetUtcNow() + timeout;
        var interval = TimeSpan.FromSeconds(1);
        var caller = invocation.Cancellation;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(caller);
        // Network I/O must be canceled while a step is in flight, not merely checked between steps.
        budget.CancelAfter(timeout);
        using var scope = invocation.WithCancellation(budget.Token);
        CliException TimedOut() => new(CliExitCode.OperationFailed,
            $"Timed out waiting for the {what}; it may still finish. Read it again later.", "timeout");
        try
        {
            while (true)
            {
                budget.Token.ThrowIfCancellationRequested();
                if (time.GetUtcNow() >= deadline)
                    throw TimedOut();
                var value = await step();
                budget.Token.ThrowIfCancellationRequested();
                var remaining = deadline - time.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                    throw TimedOut();
                if (value is not null)
                    return value;
                var delay = advised?.Invoke() ?? interval;
                await Task.Delay(delay < remaining ? delay : remaining, time, invocation.Cancellation);
                interval = TimeSpan.FromSeconds(Math.Min(5, interval.TotalSeconds * 2));
            }
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested && budget.IsCancellationRequested)
        {
            throw TimedOut();
        }
        catch (CliException) when (!caller.IsCancellationRequested && budget.IsCancellationRequested)
        {
            throw TimedOut();
        }
    }

    public static Option<TimeSpan> Timeout(string description, TimeSpan fallback) => new("--timeout")
    {
        Description = description + " (for example 90s, 10m)",
        DefaultValueFactory = _ => fallback,
        CustomParser = result =>
        {
            var text = result.Tokens.Count == 1 ? result.Tokens[0].Value : "";
            if (ParseDuration(text) is { } value)
                return value;
            result.AddError("--timeout must be a duration such as 90s, 10m or 1h.");
            return fallback;
        }
    };

    internal static TimeSpan? ParseDuration(string text)
    {
        if (text.Length < 2 || !long.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
            count < 1 || count > 86_400)
            return null;
        return text[^1] switch
        {
            's' => TimeSpan.FromSeconds(count),
            'm' when count <= 1440 => TimeSpan.FromMinutes(count),
            'h' when count <= 24 => TimeSpan.FromHours(count),
            _ => null
        };
    }
}
