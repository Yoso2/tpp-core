using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace TPPCore.Service.Chat.DataModels
{
    /// <summary>
    /// Represents a chat activity such as a message, subscription, or poll.
    /// </summary>
    public class ChatEvent : IPubSubEvent
    {
        /// <summary>
        /// Pub/sub subscription name.
        /// </summary>
        public string Topic { get; set; }

        /// <summary>
        /// Friendly name of the configured provider.
        /// </summary>
        public string ClientName;

        /// <summary>
        /// Website or server endpoint name such as IRC or Twitch.
        /// </summary>
        public string ProviderName;

        /// <summary>
        /// Whether this event originates within the service.
        /// </summary>
        /// <remarks>
        /// In otherwords, the value is false if we received it and true
        /// if we are sending the event.
        /// </remarks>
        public bool IsSelf = false;

        /// <summary>
        /// Additional information such as IRCv3 tags.
        /// </summary>
        public IDictionary<string,string> Meta;

        public ChatEvent(string topic = ChatTopics.Other)
        {
            this.Topic = topic;
            Meta = new Dictionary<string,string>();
        }

        public virtual JObject ToJObject()
        {
            Debug.Assert(Topic != null);
            Debug.Assert(ClientName != null);
            Debug.Assert(ProviderName != null);

            return JObject.FromObject(new
            {
                topic = Topic,
                clientName = ClientName,
                providerName = ProviderName,
                isSelf = IsSelf,
                meta = Meta
            });
        }
    }
}
