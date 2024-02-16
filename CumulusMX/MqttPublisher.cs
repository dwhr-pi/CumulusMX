﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using MQTTnet;
using MQTTnet.Client;

using ServiceStack;
using ServiceStack.Text;

namespace CumulusMX
{
	public static class MqttPublisher
	{
		private static Cumulus cumulus;
		private static MqttClient mqttClient;
		public static bool configured { get; set; }
		private static readonly Dictionary<String, String> publishedTopics = [];
		private static MqttTemplate updateTemplate;
		private static MqttTemplate intervalTemplate;

		public static void Setup(Cumulus cumulus)
		{
			MqttPublisher.cumulus = cumulus;

			var mqttFactory = new MqttFactory();

			mqttClient = (MqttClient) mqttFactory.CreateMqttClient();

			var clientId = Guid.NewGuid().ToString();

			var mqttTcpOptions = new MqttClientTcpOptions
			{
				Server = cumulus.MQTT.Server,
				Port = cumulus.MQTT.Port,
				TlsOptions = new MqttClientTlsOptions { UseTls = cumulus.MQTT.UseTLS },
				AddressFamily = cumulus.MQTT.IpVersion switch
				{
					4 => System.Net.Sockets.AddressFamily.InterNetwork,
					6 => System.Net.Sockets.AddressFamily.InterNetworkV6,
					_ => System.Net.Sockets.AddressFamily.Unspecified,
				}
			};
			var mqttOptions = new MqttClientOptions
			{
				ChannelOptions = mqttTcpOptions,
				ClientId = clientId,
				Credentials = string.IsNullOrEmpty(cumulus.MQTT.Password)
					? null
					: new MqttClientCredentials(cumulus.MQTT.Username, System.Text.Encoding.UTF8.GetBytes(cumulus.MQTT.Password)),
				CleanSession = true
			};

			Connect(mqttOptions).Wait();

			mqttClient.DisconnectedAsync += (async e =>
			{
				cumulus.LogWarningMessage("Error: MQTT disconnected from the server");
				await Task.Delay(TimeSpan.FromSeconds(30));

				cumulus.LogDebugMessage("MQTT attempting to reconnect with server");
				try
				{
					Connect(mqttOptions).Wait();
					cumulus.LogDebugMessage("MQTT reconnected OK");
				}
				catch
				{
					cumulus.LogErrorMessage("Error: MQTT reconnection to server failed");
				}
			});

			ReadTemplateFiles();

			configured = true;
		}


		public static void ReadTemplateFiles()
		{
			try
			{
				updateTemplate = null;

				if (cumulus.MQTT.EnableDataUpdate && !string.IsNullOrEmpty(cumulus.MQTT.UpdateTemplate))
				{
					// read the config file into memory
					var template = "mqtt/" + cumulus.MQTT.UpdateTemplate;

					if (File.Exists(template))
					{
						// use template file
						cumulus.LogMessage($"MQTT: Reading template file - {template}");

						// read the file
						var templateText = File.ReadAllText(template);
						updateTemplate = templateText.FromJson<MqttTemplate>();
					}
				}
			}
			catch (Exception ex)
			{
				cumulus.LogErrorMessage($"MQTT: Error reading update template file {cumulus.MQTT.UpdateTemplate}. Message: {ex.Message}");
			}

			try
			{
				intervalTemplate = null;

				if (cumulus.MQTT.EnableInterval && !string.IsNullOrEmpty(cumulus.MQTT.IntervalTemplate))
				{
					// read the config file into memory
					var template = "mqtt/" + cumulus.MQTT.IntervalTemplate;

					if (File.Exists(template))
					{
						// use template file
						cumulus.LogMessage($"MQTT: Reading template file - {template}");

						// read the file
						var templateText = File.ReadAllText(template);
						intervalTemplate = templateText.FromJson<MqttTemplate>();
					}
				}
			}
			catch (Exception ex)
			{
				cumulus.LogErrorMessage($"MQTT: Error reading interval template file {cumulus.MQTT.IntervalTemplate}. Message: {ex.Message}");
			}
		}

		private static async Task SendMessageAsync(string topic, string message, bool retain)
		{
			cumulus.LogDataMessage($"MQTT: publishing to topic '{topic}', message '{message}'");
			if (mqttClient.IsConnected)
			{
				var mqttMsg = new MqttApplicationMessageBuilder()
					.WithTopic(topic)
					.WithPayload(message)
					.WithRetainFlag(retain)
					.Build();

				await mqttClient.PublishAsync(mqttMsg, CancellationToken.None);
			}
			else
			{
				cumulus.LogErrorMessage("MQTT: Error - Not connected to MQTT server - message not sent");
			}
		}

		private static async Task Connect(MQTTnet.Client.MqttClientOptions options)
		{
			try
			{
				await mqttClient.ConnectAsync(options, CancellationToken.None);
			}
			catch (Exception e)
			{
				cumulus.LogErrorMessage("MQTT Error: failed to connect to the host");
				cumulus.LogMessage(e.Message);
			}
		}


		public static void UpdateMQTTfeed(string feedType, DateTime? now)
		{
			MqttTemplate mqttTemplate;

			if (feedType == "Interval")
			{
				if (intervalTemplate == null)
					return;

				mqttTemplate = intervalTemplate;
			}
			else
			{
				if (updateTemplate == null)
					return;

				mqttTemplate = updateTemplate;
			}


			// process each of the topics in turn
			try
			{
				foreach (var topic in mqttTemplate.topics)
				{
					if (topic == null) continue;

					if (feedType == "Interval" && now.Value.ToUnixTime() % (topic.interval ?? 600) != 0)
					{
						// this topic is not ready to update
						continue;
					}

					cumulus.LogDebugMessage($"MQTT: Processing {feedType} Topic: {topic.topic}");

					bool useAltResult = false;
					var mqttTokenParser = new TokenParser(cumulus.TokenParserOnToken) { Encoding = new System.Text.UTF8Encoding(false) };

					if ((feedType == "DataUpdate") && (topic.doNotTriggerOnTags != null))
					{
						useAltResult = true;
						mqttTokenParser.AltResultNoParseList = topic.doNotTriggerOnTags;
					}

					mqttTokenParser.InputText = topic.data;
					string message = mqttTokenParser.ToStringFromString();

					if (useAltResult)
					{
						if (!(publishedTopics.ContainsKey(topic.data) && (publishedTopics[topic.data] == mqttTokenParser.AltResult)))
						{
							// send the message
							_ = SendMessageAsync(topic.topic, message, topic.retain);

							if (publishedTopics.ContainsKey(topic.data))
								publishedTopics[topic.data] = mqttTokenParser.AltResult;
							else
								publishedTopics.Add(topic.data, mqttTokenParser.AltResult);
						}
					}
					else
					{
						_ = SendMessageAsync(topic.topic, message, topic.retain);
					}
				}
			}
			catch (Exception ex)
			{
				cumulus.LogErrorMessage($"UpdateMQTTfeed: Error processing the template file for [{feedType}], error = {ex.Message}");
			}
		}
	}
}
