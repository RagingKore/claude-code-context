namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// Configuration is invalid or incomplete.
/// </summary>
public sealed class LoadBalancingConfigurationException : LoadBalancingException {
    /// <summary>
    /// Initializes a new instance of the <see cref="LoadBalancingConfigurationException"/> class.
    /// </summary>
    /// <param name="message">The error message describing the configuration issue.</param>
    public LoadBalancingConfigurationException(string message) : base(message) { }
}
