namespace hass2mqtt;

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.WebSockets;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using MQTTnet.Exceptions;
using Microsoft.Extensions.Configuration;

class Processor
{
    private IMqttClient? _mqttClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<Worker> _logger;
    private readonly string? _baseTopic;
    private readonly string? _hassServerUri;
    private readonly Inclusion _inclusion = new();
    private readonly int WebsocketErrorSleepMs = 5000;

    public Processor(IConfiguration configuration, ILogger<Worker> logger)
    {
        _configuration = configuration;
        _logger = logger;
        // Filter config
        _configuration.GetSection(nameof(Inclusion)).Bind(_inclusion);
        if (_inclusion.Entities is null || _inclusion.Entities.Count == 0)
        {
            _logger.LogInformation("No inclusions.");
        }
        else
        {
            _logger.LogInformation("Inclusions:");
            foreach (var entityId in _inclusion.Entities)
            {
                _logger.LogInformation("\t{entityId}", entityId);
            }
        }
        _baseTopic = _configuration["MQTT_BASE_TOPIC"];
        if (_baseTopic is null)
        {
            throw new InvalidOperationException("_baseTopic");
        }
        _hassServerUri = _configuration["HASS_SERVER_URI"];
        if (_hassServerUri is null)
        {
            throw new InvalidOperationException("_hassServerUri");
        }
    }

    private async Task<bool> ConnectMqtt()
    {
        if (_configuration is null)
        {
            throw new InvalidOperationException("_configuration");
        }
        var port = _configuration.GetValue<int>("MQTT_PORT");
        var server = _configuration["MQTT_SERVER"];
        var clientId = _configuration["MQTT_CLIENT_ID"];
        var useTls = _configuration.GetValue<bool>("MQTT_USE_TLS");
        var username = _configuration["MQTT_USERNAME"] ?? String.Empty;
        var password = _configuration["MQTT_PASSWORD"] ?? String.Empty;
        var mqttFactory = new MqttClientFactory();
        var tlsOptions = new MqttClientTlsOptions
        {
            UseTls = useTls
        };
        var options = new MqttClientOptionsBuilder()
                        .WithCredentials(username, password)
                        .WithProtocolVersion(MqttProtocolVersion.V311)
                        .WithTcpServer(server, port)
                        .WithTlsOptions(tlsOptions)
                        .WithCleanSession(true)
                        .WithKeepAlivePeriod(TimeSpan.FromSeconds(5))
                        .WithClientId(clientId)
                        .Build();
        _logger.LogInformation("Connecting to {server}:{port} with client id {clientId}", server, port, clientId);
        _mqttClient = mqttFactory.CreateMqttClient();
        if (_mqttClient is null)
        {
            throw new InvalidOperationException("_mqttClient");
        }
        _mqttClient.ConnectedAsync += (MqttClientConnectedEventArgs args) =>
        {
            _logger.LogInformation("MQTT connected");
            return Task.CompletedTask;
        };
        _mqttClient.DisconnectedAsync += (MqttClientDisconnectedEventArgs args) =>
        {
            _logger.LogInformation("MQTT disconnected");
            return Task.CompletedTask;
        };
        var connectResult = await _mqttClient.ConnectAsync(options);
        if (connectResult.ResultCode != MqttClientConnectResultCode.Success)
        {
            _logger.LogError("MQTT connection failed: {ReasonString}", connectResult.ReasonString);
            return false;

        }
        return true;
    }

    private async Task DisconnectMqtt()
    {
        _logger.LogInformation("Disconnecting MQTT");
        if (_mqttClient?.IsConnected == true)
        {
            await _mqttClient.DisconnectAsync();
        }
    }

    private async Task Send(ClientWebSocket wsClient, string message, CancellationToken cancelToken)
    {
        _logger.LogDebug("Sending {message}", message);
        var bytesToSend = new ArraySegment<byte>(Encoding.UTF8.GetBytes(message));
        await wsClient.SendAsync(bytesToSend, WebSocketMessageType.Text, true, cancelToken);
    }

    private async Task<string> Receive(ClientWebSocket wsClient, byte[] wsBuffer, CancellationToken cancelToken)
    {
        var textOut = new StringBuilder();
        while (true)
        {
            var wsResult = await wsClient.ReceiveAsync(new ArraySegment<byte>(wsBuffer), cancelToken);
            textOut.Append(Encoding.UTF8.GetString(wsBuffer, 0, wsResult.Count));
            _logger.LogDebug("WS received {wsResult.Count} bytes.", wsResult.Count);
            if (wsResult.EndOfMessage)
            {
                _logger.LogDebug("End of WS receive.");
                break;
            }
        }
        var message = textOut.ToString();
        _logger.LogDebug("WS message: {message}", message);

        return message;
    }

    private async Task<ClientWebSocket> ConnectHassEvents(CancellationToken cancelToken)
    {
        if (_configuration is null)
        {
            throw new InvalidOperationException("_configuration");
        }
        if (_hassServerUri is null)
        {
            throw new InvalidOperationException("_hassServerUri");
        }
        Debug.WriteLine($"HASS URL: {_hassServerUri}");
        var wsUri = new Uri(_hassServerUri);
        var wsClient = new ClientWebSocket();
        await wsClient.ConnectAsync(wsUri, cancelToken);
        if (wsClient.State != WebSocketState.Open)
        {
            _logger.LogError("WS connect returned {wsClient.State}", wsClient.State);
            throw new Exception($"Could not connect to {_hassServerUri}");
        }

        var wsBuffer = new byte[1024];
        var textOut = await Receive(wsClient, wsBuffer, cancelToken);
        _logger.LogInformation("{textOut}", textOut);

        using var authStream = new MemoryStream();
        using var authWriter = new Utf8JsonWriter(authStream);
        authWriter.WriteStartObject();
        authWriter.WriteString("type", "auth");
        authWriter.WriteString("access_token", _configuration["HASS_ADMIN_TOKEN"]);
        authWriter.WriteEndObject();
        authWriter.Flush();
        await Send(wsClient, Encoding.UTF8.GetString(authStream.ToArray()), cancelToken);

        textOut = await Receive(wsClient, wsBuffer, cancelToken);
        _logger.LogInformation("{textOut}", textOut);

        using var subStream = new MemoryStream();
        using var subWriter = new Utf8JsonWriter(subStream);
        subWriter.WriteStartObject();
        subWriter.WriteNumber("id", 1);
        subWriter.WriteString("type", "subscribe_events");
        subWriter.WriteString("event_type", "state_changed");
        subWriter.WriteEndObject();
        subWriter.Flush();
        await Send(wsClient, Encoding.UTF8.GetString(subStream.ToArray()), cancelToken);

        textOut = await Receive(wsClient, wsBuffer, cancelToken);
        _logger.LogInformation("{textOut}", textOut);

        return wsClient;
    }

    private async Task PublishHassStateChanged(string topic, byte[] payload)
    {
        if (_mqttClient is null)
        {
            throw new InvalidOperationException("_mqttClient");
        }

        var message = new MqttApplicationMessageBuilder()
                            .WithTopic(topic)
                            .WithPayload(payload)
                            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                            .Build();
        _logger.LogInformation("Publishing {topic}", topic);
        var pubResult = await _mqttClient.PublishAsync(message);
        if (!pubResult.IsSuccess)
        {
            _logger.LogError("Published failed: {ReasonString}", pubResult.ReasonString);
        }
    }

    private async Task ProcessHassStateChanged(string statePayload)
    {
        var payload = Encoding.UTF8.GetBytes(statePayload);
        var document = JsonDocument.Parse(payload);
        // Event
        if (!document.RootElement.TryGetProperty("event", out JsonElement eventElement))
        {
            _logger.LogWarning("No event property: {document.RootElement}", document.RootElement);
            return;
        }
        // Data
        if (!eventElement.TryGetProperty("data", out JsonElement dataElement))
        {
            _logger.LogWarning("No data property: {document.RootElement}", document.RootElement);
            return;
        }
        // Entity id
        if (!dataElement.TryGetProperty("entity_id", out JsonElement entityIdElement))
        {
            _logger.LogWarning("No entity_id property: {document.RootElement}", document.RootElement);
            return;
        }
        var entityId = entityIdElement.GetString();
        if (String.IsNullOrEmpty(entityId))
        {
            _logger.LogWarning("Empty entity_id: {document.RootElement}", document.RootElement);
            return;
        }
        // Event type
        if (!eventElement.TryGetProperty("event_type", out JsonElement eventTypeElement))
        {
            _logger.LogWarning("No event_type property: {document.RootElement}", document.RootElement);
            return;
        }
        var eventType = eventTypeElement.GetString();
        if (String.IsNullOrEmpty(eventType))
        {
            _logger.LogWarning("Empty event_type: {document.RootElement}", document.RootElement);
            return;
        }

        // Check for inclusions if any, and skip of implicitly excluded.
        if (!_inclusion.EntityIncluded(entityId))
        {
            _logger.LogDebug("Skipping {entityId}", entityId);
            return;
        }
        // Publish topic and payload.
        var topic = $"{_baseTopic}/event/{eventType}/{entityId}";
        await PublishHassStateChanged(topic, payload);
    }

    private async Task ProcessHassEvents(ClientWebSocket wsClient, CancellationToken cancelToken)
    {
        var wsBuffer = new byte[4 * 1024];
        while (cancelToken.IsCancellationRequested == false)
        {
            var textOut = await Receive(wsClient, wsBuffer, cancelToken);
            _logger.LogDebug("{textOut}", textOut);
            await ProcessHassStateChanged(textOut);
        }
    }

    /// <summary>
    /// Read configuration, connect to MQTT broker, connect to HA WS API, process state changes.
    /// Exit if something goes awry and allow host to control process failure retry.
    /// </summary>
    public async Task ProcessAsync()
    {
        // Main processing
        try
        {
            if (!await ConnectMqtt())
            {
                return;
            }
            var cancelConnect = new CancellationTokenSource();
            cancelConnect.CancelAfter(_configuration.GetValue<int>("CANCEL_AFTER"));
            using var wsClient = await ConnectHassEvents(cancelConnect.Token);
            await ProcessHassEvents(wsClient, CancellationToken.None);
        }
        catch (MqttCommunicationException ex)
        {
            _logger.LogError("MQTT: {ex.Message}", ex.Message);
            _logger.LogDebug("{ex}", ex);
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug("{ex}", ex);
            if (WebSocketError.NotAWebSocket == ex.WebSocketErrorCode)
            {
                _logger.LogInformation("Sleeping after WebSocket error {ex.Message}", ex.Message);
                await Task.Delay(WebsocketErrorSleepMs);
            }
            else
            {
                _logger.LogError("WebSocket: {ex.Message}", ex.Message);
            }
        }
        catch (JsonException ex)
        {
            if (ex.LineNumber == 0 && ex.BytePositionInLine == 0)
            {
                _logger.LogInformation("JSON: {ex.Message}", ex.Message);
            }
            else
            {
                _logger.LogError("JSON: {ex.Message}", ex.Message);
            }
            _logger.LogDebug("{ex}", ex);
        }
        finally
        {
            await DisconnectMqtt();
        }
    }
}
