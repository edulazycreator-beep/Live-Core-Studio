using System.Diagnostics;
using System.Reflection;

namespace LiveCoreStudio.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute(string? category = null) : Attribute
{
    public string Category { get; } = category ?? "unit";
}

public sealed class SkipException(string reason) : Exception(reason);

public static class Assert
{
    public static void True(bool condition, string message = "expected true")
    {
        if (!condition) throw new InvalidOperationException("Assert.True failed: " + message);
    }

    public static void Equal<T>(T expected, T actual, string? what = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Assert.Equal failed{(what is null ? "" : $" ({what})")}: expected <{expected}>, got <{actual}>");
    }

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException ex) { return ex; }
        catch (Exception ex) { throw new InvalidOperationException($"Expected {typeof(TException).Name}, got {ex.GetType().Name}: {ex.Message}"); }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}, nothing was thrown.");
    }

    /// <summary>Polls until the condition holds or the timeout elapses.</summary>
    public static async Task EventuallyAsync(Func<bool> condition, TimeSpan timeout, string description)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new InvalidOperationException($"Timed out after {timeout.TotalSeconds:0.#} s waiting for: {description}");
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var filter = args.FirstOrDefault();
        var tests = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(m => (Method: m, Attr: m.GetCustomAttribute<TestAttribute>()))
            .Where(x => x.Attr is not null)
            .Where(x => filter is null || $"{x.Method.DeclaringType!.Name}.{x.Method.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Attr!.Category).ThenBy(x => x.Method.DeclaringType!.Name).ThenBy(x => x.Method.Name)
            .ToList();

        int passed = 0, failed = 0, skipped = 0;
        foreach (var (method, attr) in tests)
        {
            var name = $"{attr!.Category,-11} {method.DeclaringType!.Name}.{method.Name}";
            var sw = Stopwatch.StartNew();
            try
            {
                var result = method.Invoke(null, null);
                if (result is Task task) await task.WaitAsync(TimeSpan.FromSeconds(60));
                passed++;
                Console.WriteLine($"[ OK ] {name} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
                if (inner is SkipException)
                {
                    skipped++;
                    Console.WriteLine($"[SKIP] {name}: {inner.Message}");
                    continue;
                }
                failed++;
                Console.WriteLine($"[FAIL] {name}: {inner.GetType().Name}: {inner.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{passed} passed, {failed} failed, {skipped} skipped");
        return failed == 0 ? 0 : 1;
    }
}
