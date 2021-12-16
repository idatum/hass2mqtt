**hass2mqtt** is a different take on the offical Home Assistant MQTT Statestream integration: https://www.home-assistant.io/integrations/mqtt_statestream/

Implemented in C# and .NET 6, with Docker in mind as the host, Home Assistant events are subscribed via its WebSocket API and forwarded to an MQTT broker. The full state_changed event payload is passed as the MQTT message payload. The default topic is `HA/event/state_changed/{entity_id}`.

Client event processing can use the full state_changed payload for a stateless implementation of the code that processes these events. The MQTT Statestream integration instead requires the client to retain state between each topic if processing requires more than one attribute. For example, let's say you need to log both the state (e.g. 'on' or 'off') and the friendly name of the entity. You would need to retain attributes of seperate messages to log both. To further illustrate, here's a snippet of Python client code using paho.mqtt:

```
...
# Handle message received.
def on_message(client, userdata, msg):
    print(msg.topic+" "+str(msg.payload))
...
# Add message handler
client.on_message = on_message
...
```
If subscribing to MQTT Statestream messages, this code would need to save multiple attributes from multiple messages in some global variable (or at least within the scope of on_message) to complete processing. How do you know the order these messages are received? What if you implement a client such that there can be parallel processing of messages? Once you start storing state you need to ask these questions. Having each state change in one message is one solution, which hass2qtt gives you.

An `appsettings.json` configuration file -- overriden by environment variables -- allows for specific integration in your environment. You can also use an `appsettings.Development.json` configuration for debugging (e.g. `dotnet run`).

Configuration also allows for specific inclusions (filtering) of Home Assistant entities, implicitly skipping other entities outside the inclusion set; default is to publish all state_changed events (i.e. no filter).
