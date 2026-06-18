namespace BareWire.Transport.AWS.SQS;

/// <summary>
/// Specifies the authentication mode used to connect to Amazon SQS.
/// </summary>
/// <remarks>
/// In production, prefer <see cref="DefaultChain"/> with IAM roles (EC2 instance profile,
/// ECS task role, or Lambda execution role). Use <see cref="Explicit"/> only for local
/// development or scenarios where a credential chain is not available.
/// For explicit EC2 instance-profile / ECS task-role credential binding, use
/// <see cref="InstanceProfile"/> (implemented in R4.3).
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

    /// <summary>
    /// Fetches credentials from the EC2 Instance Metadata Service (IMDS) or ECS task-role endpoint,
    /// binding to the IAM role assigned to the instance or task.
    /// When <c>InstanceProfileRoleName</c> on <see cref="SqsTransportOptions"/> is set, the SDK
    /// binds to that specific IAM role; otherwise the role assigned to the instance/task is used.
    /// Credentials are refreshed automatically by the SDK before expiry — no secrets are stored
    /// in application configuration (R4.3).
    /// </summary>
    InstanceProfile = 2,
}
