using System.Collections.Generic;
using System.Linq;
using ArgsParsing;
using ArgsParsing.TypeParsers;
using Core.Commands;
using Core.Commands.Definitions;
using Core.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using NodaTime;
using Persistence.Models;
using Persistence.MongoDB.Repos;
using Persistence.MongoDB.Serializers;
using Persistence.Repos;

namespace Core
{
    /// <summary>
    /// Bundling up boilerplate code required to construct various classes.
    /// </summary>
    public static class Setups
    {
        public static ArgsParser SetUpArgsParser(IUserRepo userRepo)
        {
            var argsParser = new ArgsParser();
            argsParser.AddArgumentParser(new IntParser());
            argsParser.AddArgumentParser(new StringParser());
            argsParser.AddArgumentParser(new InstantParser());
            argsParser.AddArgumentParser(new TimeSpanParser());
            argsParser.AddArgumentParser(new HexColorParser());
            argsParser.AddArgumentParser(new PokeyenParser());
            argsParser.AddArgumentParser(new TokensParser());
            argsParser.AddArgumentParser(new PkmnSpeciesParser());

            argsParser.AddArgumentParser(new AnyOrderParser(argsParser));
            argsParser.AddArgumentParser(new OneOfParser(argsParser));
            argsParser.AddArgumentParser(new OptionalParser(argsParser));

            argsParser.AddArgumentParser(new UserParser(userRepo));
            return argsParser;
        }

        public static CommandProcessor SetUpCommandProcessor(ILoggerFactory loggerFactory, ArgsParser argsParser)
        {
            var commandProcessor = new CommandProcessor(loggerFactory.CreateLogger<CommandProcessor>(), argsParser);

            IEnumerable<Command> commands = Enumerable.Concat(
                new EasterEggCommands().Commands,
                new StaticResponseCommands().Commands
            );
            foreach (Command command in commands)
            {
                commandProcessor.InstallCommand(command);
            }
            return commandProcessor;
        }

        public class Databases
        {
            public IUserRepo UserRepo { get; }
            public IBadgeRepo BadgeRepo { get; }
            public IBank<User> PokeyenBank { get; }
            public IBank<User> TokensBank { get; }

            public Databases(IUserRepo userRepo, IBadgeRepo badgeRepo, IBank<User> pokeyenBank, IBank<User> tokensBank)
            {
                UserRepo = userRepo;
                BadgeRepo = badgeRepo;
                PokeyenBank = pokeyenBank;
                TokensBank = tokensBank;
            }
        }

        public static Databases SetUpRepositories(RootConfig rootConfig)
        {
            CustomSerializers.RegisterAll();
            IMongoClient mongoClient = new MongoClient(rootConfig.MongoDbConnectionUri);
            IMongoDatabase mongoDatabase = mongoClient.GetDatabase(rootConfig.MongoDbDatabaseName);
            IUserRepo userRepo = new UserRepo(
                database: mongoDatabase,
                startingPokeyen: rootConfig.StartingPokeyen,
                startingTokens: rootConfig.StartingTokens);
            IBadgeRepo badgeRepo = new BadgeRepo(
                database: mongoDatabase);
            IBank<User> pokeyenBank = new Bank<User>(
                database: mongoDatabase,
                currencyCollectionName: UserRepo.CollectionName,
                transactionLogCollectionName: "pokeyentransactions",
                u => u.Pokeyen,
                u => u.Id,
                clock: SystemClock.Instance);
            IBank<User> tokenBank = new Bank<User>(
                database: mongoDatabase,
                currencyCollectionName: UserRepo.CollectionName,
                transactionLogCollectionName: "tokentransactions",
                u => u.Tokens,
                u => u.Id,
                clock: SystemClock.Instance);
            return new Databases(
                userRepo: userRepo,
                badgeRepo: badgeRepo,
                pokeyenBank: pokeyenBank,
                tokensBank: tokenBank);
        }
    }
}
