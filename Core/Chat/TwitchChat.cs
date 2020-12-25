using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Configuration;
using Core.Moderation;
using Microsoft.Extensions.Logging;
using NodaTime;
using Persistence.Models;
using Persistence.Repos;
using TwitchLib.Client;
using TwitchLib.Client.Events;
using TwitchLib.Client.Extensions;
using TwitchLib.Client.Models;
using TwitchLib.Communication.Clients;
using TwitchLib.Communication.Models;

namespace Core.Chat
{
    public sealed class TwitchChat : IChat, IExecutor
    {
        public event EventHandler<MessageEventArgs> IncomingMessage = null!;
        /// Twitch Messaging Interface (TMI, the somewhat IRC-compatible protocol twitch uses) maximum message length.
        /// This limit is in characters, not bytes. See https://discuss.dev.twitch.tv/t/message-character-limit/7793/6
        private const int MaxMessageLength = 500;
        private static readonly MessageSplitter MessageSplitterRegular = new MessageSplitter(
            maxMessageLength: MaxMessageLength - "/me ".Length);
        private static readonly MessageSplitter MessageSplitterWhisper = new MessageSplitter(
            // visual representation of the longest possible username (25 characters)
            maxMessageLength: MaxMessageLength - "/w ,,,,,''''',,,,,''''',,,,, ".Length);

        private readonly ILogger<TwitchChat> _logger;
        private readonly IClock _clock;
        private readonly string _ircChannel;
        private readonly ImmutableHashSet<ChatConfig.SuppressionType> _suppressions;
        private readonly ImmutableHashSet<string> _suppressionOverrides;
        private readonly IUserRepo _userRepo;
        private readonly TwitchClient _twitchClient;

        private bool _connected = false;
        private Action? _connectivityWorkerCleanup;

        public TwitchChat(
            ILoggerFactory loggerFactory,
            IClock clock,
            ChatConfig chatConfig,
            IUserRepo userRepo)
        {
            _logger = loggerFactory.CreateLogger<TwitchChat>();
            _clock = clock;
            _ircChannel = chatConfig.Channel;
            _suppressions = chatConfig.Suppressions;
            _suppressionOverrides = chatConfig.SuppressionOverrides
                .Select(s => s.ToLowerInvariant()).ToImmutableHashSet();
            _userRepo = userRepo;

            _twitchClient = new TwitchClient(
                client: new WebSocketClient(new ClientOptions()),
                logger: loggerFactory.CreateLogger<TwitchClient>());
            var credentials = new ConnectionCredentials(
                twitchUsername: chatConfig.Username,
                twitchOAuth: chatConfig.Password,
                disableUsernameCheck: true);
            _twitchClient.Initialize(
                credentials: credentials,
                channel: chatConfig.Channel,
                // disable TwitchLib's command features, we do that ourselves
                chatCommandIdentifier: '\0',
                whisperCommandIdentifier: '\0');
        }

        public async Task SendMessage(string message)
        {
            if (_suppressions.Contains(ChatConfig.SuppressionType.Message) &&
                !_suppressionOverrides.Contains(_ircChannel))
            {
                _logger.LogDebug($"(suppressed) >#{_ircChannel}: {message}");
                return;
            }
            _logger.LogDebug($">#{_ircChannel}: {message}");
            await Task.Run(() =>
            {
                foreach (string part in MessageSplitterRegular.FitToMaxLength(message))
                {
                    _twitchClient.SendMessage(_ircChannel, "/me " + part);
                }
            });
        }

        public async Task SendWhisper(User target, string message)
        {
            if (_suppressions.Contains(ChatConfig.SuppressionType.Whisper) &&
                !_suppressionOverrides.Contains(target.SimpleName))
            {
                _logger.LogDebug($"(suppressed) >@{target.SimpleName}: {message}");
                return;
            }
            _logger.LogDebug($">@{target.SimpleName}: {message}");
            await Task.Run(() =>
            {
                foreach (string part in MessageSplitterWhisper.FitToMaxLength(message))
                {
                    _twitchClient.SendWhisper(target.SimpleName, part);
                }
            });
        }

        public void Connect()
        {
            if (_connected)
            {
                throw new InvalidOperationException("Can only ever connect once per chat instance.");
            }
            _connected = true;
            _twitchClient.OnMessageReceived += MessageReceived;
            _twitchClient.OnWhisperReceived += WhisperReceived;
            _twitchClient.Connect();
            var tokenSource = new CancellationTokenSource();
            Task checkConnectivityWorker = CheckConnectivityWorker(tokenSource.Token);
            _connectivityWorkerCleanup = () =>
            {
                tokenSource.Cancel();
                if (!checkConnectivityWorker.IsCanceled) checkConnectivityWorker.Wait();
            };
        }

        /// TwitchClient's disconnect event appears to fire unreliably,
        /// so it is safer to manually check the connection every few seconds.
        private async Task CheckConnectivityWorker(CancellationToken cancellationToken)
        {
            TimeSpan minDelay = TimeSpan.FromSeconds(3);
            TimeSpan maxDelay = TimeSpan.FromMinutes(10);
            TimeSpan delay = minDelay;
            while (!cancellationToken.IsCancellationRequested)
            {
                delay *= _twitchClient.IsConnected ? 0.5 : 2;
                if (delay > maxDelay) delay = maxDelay;
                if (delay < minDelay) delay = minDelay;

                if (!_twitchClient.IsConnected)
                {
                    _logger.LogError("Not connected to twitch, trying to reconnect...");
                    try
                    {
                        _twitchClient.Reconnect();
                    }
                    catch (Exception)
                    {
                        _logger.LogError($"Failed to reconnect, trying again in {delay.TotalSeconds} seconds.");
                    }
                }

                await Task.Delay(delay, cancellationToken);
            }
        }

        private async void MessageReceived(object? sender, OnMessageReceivedArgs e)
        {
            _logger.LogDebug($"<#{_ircChannel} {e.ChatMessage.Username}: {e.ChatMessage.Message}");
            User user = await _userRepo.RecordUser(GetUserInfoFromTwitchMessage(e.ChatMessage));
            var message = new Message(user, e.ChatMessage.Message, MessageSource.Chat)
            {
                Details = new MessageDetails(
                    MessageId: e.ChatMessage.Id,
                    IsAction: e.ChatMessage.IsMe,
                    IsStaff: e.ChatMessage.IsBroadcaster || e.ChatMessage.IsModerator
                )
            };
            IncomingMessage?.Invoke(this, new MessageEventArgs(message));
        }

        private async void WhisperReceived(object? sender, OnWhisperReceivedArgs e)
        {
            _logger.LogDebug($"<@{e.WhisperMessage.Username}: {e.WhisperMessage.Message}");
            User user = await _userRepo.RecordUser(GetUserInfoFromTwitchMessage(e.WhisperMessage));
            var message = new Message(user, e.WhisperMessage.Message, MessageSource.Whisper)
            {
                Details = new MessageDetails(MessageId: null, IsAction: false, IsStaff: false)
            };
            IncomingMessage?.Invoke(this, new MessageEventArgs(message));
        }

        private UserInfo GetUserInfoFromTwitchMessage(TwitchLibMessage message)
        {
            string? colorHex = message.ColorHex;
            return new UserInfo(
                id: message.UserId,
                twitchDisplayName: message.DisplayName,
                simpleName: message.Username,
                color: string.IsNullOrEmpty(colorHex) ? null : colorHex.TrimStart('#'),
                fromMessage: true,
                updatedAt: _clock.GetCurrentInstant()
            );
        }

        public void Dispose()
        {
            if (_connected)
            {
                _connectivityWorkerCleanup?.Invoke();
                _twitchClient.Disconnect();
            }
            _twitchClient.OnMessageReceived -= MessageReceived;
            _twitchClient.OnWhisperReceived -= WhisperReceived;
            _logger.LogDebug("twitch chat is now fully shut down.");
        }

        public async Task DeleteMessage(string messageId)
        {
            await Task.Run(() => _twitchClient.SendMessage(_ircChannel, ".delete " + messageId));
        }

        public async Task Timeout(User user, string? message, Duration duration)
        {
            await Task.Run(() =>
                _twitchClient.TimeoutUser(_ircChannel, user.SimpleName, duration.ToTimeSpan(),
                    message ?? "no timeout reason was given"));
        }
    }
}
