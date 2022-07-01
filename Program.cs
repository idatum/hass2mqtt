using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.WebSockets;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Client.Connecting;
using MQTTnet.Client.Disconnecting;
using MQTTnet.Client.Options;
using MQTTnet.Client.Publishing;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using MQTTnet.Server;
using MQTTnet.Exceptions;
using Microsoft.Extensions.Configuration;

namespace hass2mqtt
{
    class Program
    {
        private static IMqttClient _mqttClient;
        private static Tracing _tracing = new();
        private static IConfigurationRoot _configuration;
        private static Inclusion _inclusion = new();
        private static int WebsocketErrorSleepMs = 1000;

        private static void OnPublisherConnected(MqttClientConnectedEventArgs x)
        {
            _tracing.Info("MQTT connected");
        }

        private static void OnPublisherDisconnected(MqttClientDisconnectedEventArgs x)
        {
            _tracing.Info("MQTT disconnected");
        }

        private static async Task<bool> ConnectMqtt()
        {
            var port = _configuration.GetValue<int>("MQTT_PORT");
            var server = _configuration["MQTT_SERVER"];
            var clientId = _configuration["MQTT_CLIENT_ID"];
            var useTls = _configuration.GetValue<bool>("MQTT_USE_TLS");
            _tracing.Debug($"Connecting to {server}:{port} with client id {clientId}");
            var mqttFactory = new MqttFactory();
            var tlsOptions = new MqttClientTlsOptions
            {
                UseTls = useTls
            };
            var options = new MqttClientOptions
            {
                ClientId = clientId,
                ProtocolVersion = MqttProtocolVersion.V311,
                ChannelOptions = new MqttClientTcpOptions
                {
                    Server = server,
                    Port = port,
                    TlsOptions = tlsOptions
                }
            };
            options.Credentials = new MqttClientCredentials
            {
                Username = _configuration["MQTT_USERNAME"],
                Password = Encoding.UTF8.GetBytes(_configuration["MQTT_PASSWORD"])
            };
            options.CleanSession = true;
            _mqttClient = mqttFactory.CreateMqttClient();
            _mqttClient.ConnectedHandler = new MqttClientConnectedHandlerDelegate(OnPublisherConnected);
            _mqttClient.DisconnectedHandler = new MqttClientDisconnectedHandlerDelegate(OnPublisherDisconnected);            
            var connectResult = await _mqttClient.ConnectAsync(options);
            _tracing.Verbose($"MQTT connect result: {connectResult.ResultCode}");

            if (connectResult.ResultCode != MqttClientConnectResultCode.Success)
            {
                _tracing.Error($"MQTT connection failed: {connectResult.ReasonString}");
                return false;
            }

            return true;
        }

        private static async Task DisconnectMqtt()
        {
            _tracing.Info("Disconnecting MQTT");
            await _mqttClient.DisconnectAsync();
        }

        private static async Task Send(ClientWebSocket wsClient, string message, CancellationToken cancelToken)
        {
            _tracing.Verbose($"Sending {message}");
            var bytesToSend = new ArraySegment<byte>(Encoding.UTF8.GetBytes(message));
            await wsClient.SendAsync(bytesToSend, WebSocketMessageType.Text, true, cancelToken);
        }

        private static async Task<string> Receive(ClientWebSocket wsClient, byte[] wsBuffer, CancellationToken cancelToken)
        {
            var textOut = new StringBuilder();
            while (true)
            {
                var wsResult = await wsClient.ReceiveAsync(new ArraySegment<byte>(wsBuffer), cancelToken);
                textOut.Append(Encoding.UTF8.GetString(wsBuffer, 0, wsResult.Count));
                _tracing.Verbose($"WS received {wsResult.Count} bytes.");
                if (wsResult.EndOfMessage)
                {
                    _tracing.Verbose("End of WS receive.");
                    break;
                }
            }
            var message = textOut.ToString();
            _tracing.Verbose($"WS message: {message}");

            return message;
        }

        private static async Task<ClientWebSocket> ConnectHassEvents(CancellationToken cancelToken)
        {
            var hassServerUri = _configuration["HASS_SERVER_URI"];
            Debug.WriteLine($"HASS URL: {hassServerUri}");
            var wsUri = new Uri(hassServerUri);
            var wsClient = new ClientWebSocket();
            await wsClient.ConnectAsync(wsUri, cancelToken);
            if (wsClient.State != WebSocketState.Open)
            {
                _tracing.Error($"WS connect returned {wsClient.State}");
                throw new Exception($"Could not connect to {hassServerUri}");
            }
            var wsBuffer = new byte[1024];

            var textOut = await Receive(wsClient, wsBuffer, cancelToken);
            _tracing.Info($"{textOut}");

            using var authStream = new MemoryStream();
            using var authWriter = new Utf8JsonWriter(authStream);
            authWriter.WriteStartObject();
            authWriter.WriteString("type", "auth");
            authWriter.WriteString("access_token", _configuration["HASS_ADMIN_TOKEN"]);
            authWriter.WriteEndObject();
            authWriter.Flush();
            await Send(wsClient, Encoding.UTF8.GetString(authStream.ToArray()), cancelToken);
            
            textOut = await Receive(wsClient, wsBuffer, cancelToken);
            _tracing.Info($"{textOut}");

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
            _tracing.Info($"{textOut}");

            return wsClient;
        }

        private static async Task PublishHassStateChanged(string topic, byte[] payload)
        {
            var message = new MqttApplicationMessageBuilder()
                                .WithTopic(topic)
                                .WithPayload(payload)
                                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                                .Build();
            _tracing.Info($"Publishing {topic}");
            var pubResult = await _mqttClient.PublishAsync(message);
            if (pubResult.ReasonCode != MqttClientPublishReasonCode.Success)
            {
                _tracing.Error($"Published failed: {pubResult.ReasonString}");
            }
        }

        private static async Task ProcessHassStateChanged(string statePayload)
        {
            var payload = Encoding.UTF8.GetBytes(statePayload);
            var document = JsonDocument.Parse(payload);
            // Event
            if (!document.RootElement.TryGetProperty("event", out JsonElement eventElement))
            {
                _tracing.Warning($"No event property: {document.RootElement}");
                return;
            }
            // Data
            if (!eventElement.TryGetProperty("data", out JsonElement dataElement))
            {
                _tracing.Warning($"No data property: {document.RootElement}");
                return;
            }
            // Entity id
            if (!dataElement.TryGetProperty("entity_id", out JsonElement entityIdElement))
            {
                _tracing.Warning($"No entity_id property: {document.RootElement}");
                return;
            }
            var entityId = entityIdElement.GetString();
            if (String.IsNullOrEmpty(entityId))
            {
                _tracing.Warning($"Empty entity_id: {document.RootElement}");
                return;
            }
            // Event type
            if (!eventElement.TryGetProperty("event_type", out JsonElement eventTypeElement))
            {
                _tracing.Warning($"No event_type property: {document.RootElement}");
                return;
            }
            var eventType = eventTypeElement.GetString();
            if (String.IsNullOrEmpty(eventType))
            {
                _tracing.Warning($"Empty event_type: {document.RootElement}");
                return;
            }

            // Check for inclusions if any, and skip of implicitly excluded.
            if (!_inclusion.EntityIncluded(entityId))
            {
                _tracing.Info($"Skipping {entityId}");
                return;
            }
            // Publish topic and payload.
            var topic = $"{_configuration["MQTT_BASE_TOPIC"]}/event/{eventType}/{entityId}";
            await PublishHassStateChanged(topic, payload);
        }

        private static async Task ProcessHassEvents(ClientWebSocket wsClient, CancellationToken cancelToken)
        {
            var wsBuffer = new byte[4 * 1024];
            while (true)
            {
                var textOut = await Receive(wsClient, wsBuffer, cancelToken);
                _tracing.Verbose($"{textOut}");
                await ProcessHassStateChanged(textOut);
            }
        }

        /// <summary>
        /// Read configuration, connect to MQTT broker, connect to HA WS API, process state changes.
        /// Exit if something goes awry and allow host to control process failure retry.
        /// </summary>
        public static async Task Main(string[] args)
        {
            _configuration = new ConfigurationBuilder()
                            .AddJsonFile("appsettings.json", optional:true, reloadOnChange:true)
                            .AddJsonFile("appsettings.Development.json", optional:true)
                            .AddEnvironmentVariables()
                            .Build();
            // Tracing config
            _tracing.TraceLevel = _configuration.GetValue<TraceLevel>("TRACE_LEVEL");
            // Filter config
            _configuration.GetSection(nameof(Inclusion)).Bind(_inclusion);
            if (_inclusion.Entities is null || _inclusion.Entities.Count == 0)
            {
                _tracing.Info("No inclusions.");
            }
            else
            {
                _tracing.Info("Inclusions:");
                foreach (var entityId in _inclusion.Entities)
                {
                    _tracing.Info($"\t{entityId}");
                }
            }
            // Main processing
            var cancelSource = new CancellationTokenSource();
            cancelSource.CancelAfter(_configuration.GetValue<int>("CANCEL_AFTER"));
            try
            {
                if (!await ConnectMqtt())
                {
                    return;
                }
                using var wsClient = await ConnectHassEvents(cancelSource.Token);
                await ProcessHassEvents(wsClient, CancellationToken.None);
                await DisconnectMqtt();
            }
            catch (MqttCommunicationException ex)
            {
                _tracing.Error($"MQTT: {ex.Message}");
                _tracing.Verbose($"{ex}");
            }
            catch (WebSocketException ex)
            {
                _tracing.Verbose($"{ex}");
                if (WebSocketError.NotAWebSocket == ex.WebSocketErrorCode)
                {
                    _tracing.Info($"Sleeping after WebSocket error {ex.Message}");
                    await Task.Delay(WebsocketErrorSleepMs);
                }
                else
                {
                    _tracing.Error($"WebSocket: {ex.Message}");
                }
            }
            catch (JsonException ex)
            {
                _tracing.Error($"JSON: {ex.Message}");
                _tracing.Verbose($"{ex}");
            }
        }
    }
}
