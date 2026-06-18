namespace BareWire.Transport.AWS.SQS.Configuration;

internal sealed class SqsConfigurator : ISqsConfigurator
{
    private SqsAuthMode _authMode = SqsAuthMode.DefaultChain;
    private string _accessKeyId = string.Empty;
    private string _secretAccessKey = string.Empty;
    private string _regionEndpoint = string.Empty;
    private string? _serviceUrl;
    private bool _allowInsecureEndpoint;
    private TimeSpan? _defaultVisibilityTimeout;
    private int? _waitTimeSeconds;
    private int? _maxNumberOfMessages;
    private int? _maxInFlightMessages;

    public void UseDefaultCredentials()
    {
        _authMode = SqsAuthMode.DefaultChain;
    }

    public void UseExplicitCredentials(string accessKeyId, string secretAccessKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessKeyId);
        ArgumentException.ThrowIfNullOrEmpty(secretAccessKey);
        _accessKeyId = accessKeyId;
        _secretAccessKey = secretAccessKey;
        _authMode = SqsAuthMode.Explicit;
    }

    public void Region(string regionName)
    {
        ArgumentException.ThrowIfNullOrEmpty(regionName);
        _regionEndpoint = regionName;
    }

    public void ServiceUrl(string url)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        _serviceUrl = url;
    }

    public void AllowInsecureEndpoint()
    {
        _allowInsecureEndpoint = true;
    }

    public void VisibilityTimeout(TimeSpan timeout)
    {
        _defaultVisibilityTimeout = timeout;
    }

    public void WaitTimeSeconds(int seconds)
    {
        _waitTimeSeconds = seconds;
    }

    public void MaxNumberOfMessages(int count)
    {
        _maxNumberOfMessages = count;
    }

    public void MaxInFlightMessages(int max)
    {
        _maxInFlightMessages = max;
    }

    // Every field is explicitly threaded into options to prevent silent defaults
    // (mirror ASB configurator GAP-3 fix).
    internal SqsTransportOptions Build()
    {
        var options = new SqsTransportOptions();

        options.AuthMode = _authMode;

        if (!string.IsNullOrEmpty(_accessKeyId))
        {
            options.AccessKeyId = _accessKeyId;
        }

        if (!string.IsNullOrEmpty(_secretAccessKey))
        {
            options.SecretAccessKey = _secretAccessKey;
        }

        if (!string.IsNullOrEmpty(_regionEndpoint))
        {
            options.RegionEndpoint = _regionEndpoint;
        }

        if (_serviceUrl is not null)
        {
            options.ServiceUrl = _serviceUrl;
        }

        options.AllowInsecureEndpoint = _allowInsecureEndpoint;

        if (_defaultVisibilityTimeout.HasValue)
        {
            options.DefaultVisibilityTimeout = _defaultVisibilityTimeout.Value;
        }

        if (_waitTimeSeconds.HasValue)
        {
            options.WaitTimeSeconds = _waitTimeSeconds.Value;
        }

        if (_maxNumberOfMessages.HasValue)
        {
            options.MaxNumberOfMessages = _maxNumberOfMessages.Value;
        }

        if (_maxInFlightMessages.HasValue)
        {
            options.MaxInFlightMessages = _maxInFlightMessages.Value;
        }

        options.Validate();

        return options;
    }
}
