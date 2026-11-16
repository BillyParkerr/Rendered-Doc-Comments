// <inheritdoc/> cases across files, for trying the extension in the experimental instance.
// RetryStrategyBase is declared in DocCommentSamples.cs: edit its comments there and, about a
// second after you stop typing, the cards here should follow.

using System;

namespace Contoso.Resilience
{
    /// <inheritdoc/>
    public sealed class LinearRetryStrategy : RetryStrategyBase
    {
        /// <summary>
        /// Waits the same extra amount after each failure. <inheritdoc cref="RetryStrategyBase" path="/remarks/node()"/>
        /// </summary>
        public LinearRetryStrategy() { }

        /// <inheritdoc/>
        public override TimeSpan NextDelay(int attempt) => TimeSpan.FromMilliseconds(100 * attempt);

        /// <inheritdoc path="/summary"/>
        /// <param name="error"><inheritdoc/> Only timeouts and I/O errors count here.</param>
        public override bool ShouldRetry(Exception error) => error is TimeoutException || error is System.IO.IOException;
    }
}
