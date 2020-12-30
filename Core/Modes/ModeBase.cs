using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using ArgsParsing;
using Core.Chat;
using Core.Commands;
using Core.Commands.Definitions;
using Core.Configuration;
using Core.Moderation;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Core.Modes
{
    public sealed class ModeBase : IDisposable
    {
        private readonly CommandProcessor _commandProcessor;
        private readonly IChat _chat;
        private readonly ICommandResponder _commandResponder;
        private readonly IModerator _moderator;

        public ModeBase(ILoggerFactory loggerFactory, BaseConfig baseConfig, StopToken stopToken)
        {
            IClock clock = SystemClock.Instance;
            PokedexData pokedexData = PokedexData.Load();
            Setups.Databases repos = Setups.SetUpRepositories(baseConfig);
            ArgsParser argsParser = Setups.SetUpArgsParser(repos.UserRepo, pokedexData);

            _commandProcessor = Setups.SetUpCommandProcessor(
                loggerFactory, argsParser, repos, stopToken, baseConfig.Chat);

            TwitchChat twitchChat = new(loggerFactory, clock, baseConfig.Chat, repos.UserRepo);
            _chat = twitchChat;
            _chat.IncomingMessage += MessageReceived;
            _commandResponder = new CommandResponder(_chat);

            IImmutableList<IModerationRule> rules = ImmutableList.Create<IModerationRule>(
                new BannedUrlsRule(),
                new SpambotRule(),
                new EmoteRule(),
                new CopypastaRule(clock),
                new UnicodeCharacterCategoryRule());
            ILogger<Moderator> moderatorLogger = loggerFactory.CreateLogger<Moderator>();
            _moderator = new Moderator(moderatorLogger, twitchChat, rules, repos.ModLogRepo, clock);
        }

        private async void MessageReceived(object? sender, MessageEventArgs e) =>
            await ProcessIncomingMessage(e.Message);

        private async Task ProcessIncomingMessage(Message message)
        {
            bool isOk = message.Details.IsStaff
                        || message.MessageSource != MessageSource.Chat
                        || await _moderator.Check(message);
            if (!isOk)
            {
                return;
            }

            string[] parts = message.MessageText.Split(" ");
            string? firstPart = parts.FirstOrDefault();
            string? commandName = firstPart switch
            {
                null => null,
                var name when message.MessageSource == MessageSource.Whisper
                    => name.StartsWith('!') ? name.Substring(startIndex: 1) : name,
                var name when message.MessageSource == MessageSource.Chat && name.StartsWith('!')
                    => name.Substring(startIndex: 1),
                _ => null
            };
            if (commandName != null)
            {
                CommandResult result = await _commandProcessor
                    .Process(commandName, parts.Skip(1).ToImmutableList(), message);
                await _commandResponder.ProcessResponse(message, result);
            }
        }

        public void Start()
        {
            _chat.Connect();
        }

        public void Dispose()
        {
            _chat.Dispose();
            _chat.IncomingMessage -= MessageReceived;
        }
    }
}
