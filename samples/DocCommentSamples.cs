// Open this file in the experimental instance to try the extension (see docs/DEVELOPMENT.md).
// It isn't part of the extension build.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Contoso.Resilience
{
    /// <summary>Thrown when every retry attempt has failed.</summary>
    public sealed class RetryExhaustedException : Exception
    {
    }

    public sealed class RetryPolicy
    {
        /// <summary>Delay before the first retry. Defaults to <c>200 ms</c> and doubles after each failure.</summary>
        public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(200);

        /// <summary>
        /// Gets or sets whether a jitter of up to <c>±20%</c> is added to each delay.
        /// </summary>
        /// <value><see langword="true"/> to add jitter; otherwise <see langword="false"/>.</value>
        public bool UseJitter { get; set; } = true;
    }

    public static class Retry
    {
        /// <summary>
        /// Retries the given <paramref name="operation"/> using exponential backoff
        /// until it succeeds or <paramref name="maxAttempts"/> is reached.
        /// </summary>
        /// <typeparam name="T">The result type of the operation.</typeparam>
        /// <param name="operation">The asynchronous operation to execute.</param>
        /// <param name="maxAttempts">Maximum number of attempts. Must be greater than zero.</param>
        /// <param name="cancellationToken">Token used to cancel the retry loop.</param>
        /// <returns>The result of the first successful attempt.</returns>
        /// <exception cref="ArgumentOutOfRangeException">When <paramref name="maxAttempts"/> is less than 1.</exception>
        /// <exception cref="RetryExhaustedException">When every attempt fails.</exception>
        /// <remarks>
        /// Delays double after each failure, starting at <see cref="RetryPolicy.InitialDelay"/>.
        /// See <see href="https://learn.microsoft.com/dotnet/core/resilience/">.NET resilience guidance</see>.
        /// </remarks>
        /// <example>
        /// <code>
        /// var user = await Retry.ExecuteAsync(() => api.GetUserAsync(id), maxAttempts: 5);
        /// </code>
        /// </example>
        public static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, int maxAttempts, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            return await operation(); // 
        }

        /// <summary>
        /// Picks the delay for an attempt.
        /// <para>The rules are applied in this order:</para>
        /// <list type="number">
        /// <item><description>Start from <see cref="RetryPolicy.InitialDelay"/>.</description></item>
        /// <item><description>Double it for every previous failure.</description></item>
        /// <item><description>Add jitter if <see cref="RetryPolicy.UseJitter"/> is <see langword="true"/>.</description></item>
        /// </list>
        /// </summary>
        /// <param name="policy">The policy to read settings from.</param>
        /// <param name="attempt">The 1-based attempt number.</param>
        /// <returns>How long to wait before <paramref name="attempt"/>.</returns>
        /// <seealso cref="ExecuteAsync{T}(Func{Task{T}}, int, CancellationToken)"/>
        /// <seealso cref="T:System.Threading.Tasks.Task"/>
        public static TimeSpan GetDelay(RetryPolicy policy, int attempt) => policy.InitialDelay * Math.Pow(2, attempt - 1);

        /// <summary>
        /// Supported back-off modes:
        /// <list type="table">
        /// <listheader><term>Mode</term><description>Behaviour</description></listheader>
        /// <item><term>Exponential</term><description>Doubles the delay each time.</description></item>
        /// <item><term>Linear</term><description>Adds the initial delay each time.</description></item>
        /// <item><term>Constant</term><description>Always waits the initial delay.</description></item>
        /// </list>
        /// Use <b>Exponential</b> unless you have a <i>specific</i> reason not to.
        /// </summary>
        public static IReadOnlyList<string> Modes { get; } = new[] { "Exponential", "Linear", "Constant" };

        /// Just text with no XML tags at all, which some codebases use.
        public static void UntaggedComment() { }

        /// <summary>
        /// A comment with a missing closing tag, as you'd have halfway through typing it.
        /// <param name="value">This parameter should still show in the Parameters row.</param>
        public static void Malformed(int value) { }

        /// <inheritdoc cref="ExecuteAsync{T}(Func{Task{T}}, int, CancellationToken)"/>
        public static Task<T> RunAsync<T>(Func<Task<T>> operation) => ExecuteAsync(operation, 3);

        //// Four slashes: an ordinary comment, never rendered.
        public static void NotADocComment() { }
    }

    // ---- <inheritdoc/> samples ----------------------------------------------------------

    /// <summary>Decides how long to wait before each retry.</summary>
    public interface IRetryStrategy
    {
        /// <summary>Gets the delay before the given attempt.</summary>
        /// <param name="attempt">The 1-based attempt number.</param>
        /// <returns>How long to wait before <paramref name="attempt"/>.</returns>
        /// <exception cref="ArgumentOutOfRangeException">When <paramref name="attempt"/> is less than 1.</exception>
        TimeSpan NextDelay(int attempt);
    }

    /// <summary>Base class for retry strategies that can give up early.</summary>
    /// <remarks>Derived classes decide which failures are worth retrying.</remarks>
    public abstract class RetryStrategyBase : IRetryStrategy
    {
        /// <inheritdoc/>
        public abstract TimeSpan NextDelay(int attempt);

        /// <summary>Decides whether a failure should be retried at all.</summary>
        /// <param name="error">The exception thrown by the last attempt.</param>
        /// <returns><see langword="true"/> to try again; <see langword="false"/> to give up.</returns>
        public abstract bool ShouldRetry(Exception error);
    }

    /// <inheritdoc/>
    public sealed class ExponentialRetryStrategy : RetryStrategyBase
    {
        /// <inheritdoc/>
        public override TimeSpan NextDelay(int attempt) => TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt - 1));

        /// <inheritdoc/>
        /// <remarks>Timeouts are always retried; everything else gives up.</remarks>
        public override bool ShouldRetry(Exception error) => error is TimeoutException;

        /// <inheritdoc/>
        public override string ToString() => "Exponential";
    }
}
