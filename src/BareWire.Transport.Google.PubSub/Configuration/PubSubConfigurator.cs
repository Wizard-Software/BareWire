namespace BareWire.Transport.Google.PubSub.Configuration;

internal sealed class PubSubConfigurator : IPubSubConfigurator
{
    private PubSubAuthMode _authMode = PubSubAuthMode.ApplicationDefault;
    private string _projectId = string.Empty;
    private string _serviceAccountJsonPath = string.Empty;
    private string _serviceAccountJson = string.Empty;
    private string _emulatorEndpoint = string.Empty;
    private TimeSpan? _defaultAckDeadline;
    private int? _maxOutstandingMessages;
    private long? _maxOutstandingBytes;
    private int? _maxInFlightMessages;
    private bool _enableMessageOrdering;

    public void UseApplicationDefaultCredentials()
    {
        _authMode = PubSubAuthMode.ApplicationDefault;
    }

    public void UseServiceAccountJson(string jsonFilePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(jsonFilePath);
        _serviceAccountJsonPath = jsonFilePath;
        _authMode = PubSubAuthMode.ServiceAccountJson;
    }

    public void UseServiceAccountJsonContent(string jsonContent)
    {
        ArgumentException.ThrowIfNullOrEmpty(jsonContent);
        _serviceAccountJson = jsonContent;
        _authMode = PubSubAuthMode.ServiceAccountJson;
    }

    public void UseEmulator(string endpoint)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        _emulatorEndpoint = endpoint;
        _authMode = PubSubAuthMode.EmulatorInsecure;
    }

    public void ProjectId(string projectId)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectId);
        _projectId = projectId;
    }

    public void AckDeadline(TimeSpan deadline)
    {
        _defaultAckDeadline = deadline;
    }

    public void MaxOutstandingMessages(int max)
    {
        _maxOutstandingMessages = max;
    }

    public void MaxOutstandingBytes(long maxBytes)
    {
        _maxOutstandingBytes = maxBytes;
    }

    public void MaxInFlightMessages(int max)
    {
        _maxInFlightMessages = max;
    }

    public void EnableMessageOrdering()
    {
        _enableMessageOrdering = true;
    }

    // Every field is explicitly threaded into options to prevent silent defaults.
    internal PubSubTransportOptions Build()
    {
        var options = new PubSubTransportOptions();

        options.AuthMode = _authMode;

        if (!string.IsNullOrEmpty(_projectId))
        {
            options.ProjectId = _projectId;
        }

        if (!string.IsNullOrEmpty(_serviceAccountJsonPath))
        {
            options.ServiceAccountJsonPath = _serviceAccountJsonPath;
        }

        if (!string.IsNullOrEmpty(_serviceAccountJson))
        {
            options.ServiceAccountJson = _serviceAccountJson;
        }

        if (!string.IsNullOrEmpty(_emulatorEndpoint))
        {
            options.EmulatorEndpoint = _emulatorEndpoint;
        }

        if (_defaultAckDeadline.HasValue)
        {
            options.DefaultAckDeadline = _defaultAckDeadline.Value;
        }

        if (_maxOutstandingMessages.HasValue)
        {
            options.MaxOutstandingMessages = _maxOutstandingMessages.Value;
        }

        if (_maxOutstandingBytes.HasValue)
        {
            options.MaxOutstandingBytes = _maxOutstandingBytes.Value;
        }

        if (_maxInFlightMessages.HasValue)
        {
            options.MaxInFlightMessages = _maxInFlightMessages.Value;
        }

        options.EnableMessageOrdering = _enableMessageOrdering;

        options.Validate();

        return options;
    }
}
