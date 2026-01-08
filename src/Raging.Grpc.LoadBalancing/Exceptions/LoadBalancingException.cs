namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// Base exception for load balancing errors.
/// </summary>
public abstract class LoadBalancingException : Exception {
    /// <summary>
    /// Initializes a new instance of the <see cref="LoadBalancingException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    protected LoadBalancingException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="LoadBalancingException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    protected LoadBalancingException(string message, Exception innerException)
        : base(message, innerException) { }
}
