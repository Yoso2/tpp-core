using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using log4net;
using Newtonsoft.Json;
using TPPCore.ChatProviders.DataModels;

namespace TPPCore.ChatProviders.Twitch
{
    public class TwitchProvider : IProviderAsync
    {
        private static readonly ILog logger = LogManager.GetLogger(
            System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private const string roomListUrl = "https://tmi.twitch.tv/group/user/{0}/chatters";

        public string ClientName { get { return twitchIrcProvider.ClientName; } }
        public string ProviderName { get { return "twitch"; } }

        private TwitchIrcProvider twitchIrcProvider;
        private HttpClient httpClient;

        public TwitchProvider()
        {
            twitchIrcProvider = new TwitchIrcProvider();
            httpClient = new HttpClient();
        }

        public void Configure(string clientName, ProviderContext providerContext)
        {
            twitchIrcProvider.Configure(clientName, providerContext);
        }

        public async Task Run()
        {
            await twitchIrcProvider.Run();
        }

        public void Shutdown()
        {
            twitchIrcProvider.Shutdown();
        }

        public string GetUserId()
        {
            throw new System.NotImplementedException();
        }

        public string GetUsername()
        {
            return twitchIrcProvider.GetUsername();
        }

        public async Task SendMessage(string channel, string message)
        {
            await twitchIrcProvider.SendMessage(channel, message);
        }

        public async Task SendPrivateMessage(string user, string message)
        {
            await SendMessage("#jtv", $".w {user} {message}");
        }

        public async Task TimeoutUser(string user, string reason, int duration, string channel)
        {
            await SendMessage(channel, $".timeout {user} {duration} {reason}");
        }

        public async Task BanUser(string user, string reason, string channel)
        {
            await SendMessage(channel, $".ban {user} {reason}");
        }

        public async Task<IList<ChatUser>> GetRoomList(string channel)
        {
            var users = (await twitchIrcProvider.GetRoomList(channel)).ToList();

            // TODO: Customize the IRC client's ChannelTracker to grab the
            // user-id from the tags.
            users.ForEach(item => { item.UserId = null; item.Nickname = null; });

            var url = string.Format(roomListUrl, channel.TrimStart('#'));
            var response = await httpClient.GetAsync(url);
            var jsonString = await response.Content.ReadAsStringAsync();
            ChatList chatList = JsonConvert.DeserializeObject<ChatList>(jsonString);

            var moderators = chatList.chatters.moderators;
            var staff = chatList.chatters.staff;
            var admins = chatList.chatters.admins;
            var global_mods = chatList.chatters.global_mods;
            var viewers = chatList.chatters.viewers;

            foreach (var username in viewers)
            {
                var user = new ChatUser() { Username = username, AccessLevel = AccessLevel.Viewer };
                users.Add(user);
            }

            foreach (var username in moderators)
            {
                var user = new ChatUser() { Username = username, AccessLevel = AccessLevel.Moderator };
                users.Add(user);
            }

            foreach (var usernames in new[] { global_mods, admins, staff })
            {
                foreach (var username in usernames)
                {
                    var user = new ChatUser() { Username = username, AccessLevel = AccessLevel.Staff };
                    users.Add(user);
                }
            }

            return users.Distinct(new ChatUserEqualityComparer()).ToList();
        }
    }
}
