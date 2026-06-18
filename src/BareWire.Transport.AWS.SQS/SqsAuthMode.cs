namespace BareWire.Transport.AWS.SQS;

/// <summary>
/// Specifies the authentication mode used to connect to Amazon SQS.
/// </summary>
/// <remarks>
/// In production, prefer <see cref="DefaultChain"/> with IAM roles (EC2 instance profile,
/// ECS task role, or Lambda execution role). Use <see cref="Explicit"/> only for local
/// development or scenarios where a credential chain is not available.
/// Full IAM InstanceProfile support with <c>InstanceProfileCredentialsProvider</c> arrives in R4.3.
/// </remarks>
internal enum SqsAuthMode
{
    /// <summary>
    /// Uses the AWS SDK default credential chain: environment variables, shared credentials file,
    /// EC2/ECS/Lambda IAM role, and so on. Preferred for production deployments. Default.
    /// </summary>
    DefaultChain = 0,

    /// <summary>
    /// Uses explicitly supplied <c>AccessKeyId</c> and <c>SecretAccessKey</c> credentials.
    /// Prefer <see cref="DefaultChain"/> with IAM roles for production; use <see cref="Explicit"/>
    /// only for local development or static-credential scenarios.
    /// </summary>
    Explicit = 1,
}
