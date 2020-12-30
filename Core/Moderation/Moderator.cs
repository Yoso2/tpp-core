using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NodaTime;
using Persistence.Models;
using Persistence.Repos;

namespace Core.Moderation
{
    public interface IModerator
    {
        /// Checks a message and may perform some punitive actions,
        /// returning whether the message was okay.
        Task<bool> Check(Message message);
    }

    public class Moderator : IModerator
    {
        private readonly ILogger<Moderator> _logger;
        private readonly IExecutor _executor;
        private readonly IImmutableList<IModerationRule> _rules;
        private readonly IModLogRepo _modLogRepo;
        private readonly IClock _clock;

        private static readonly Duration RecentTimeoutsLimit = Duration.FromDays(7);
        private static readonly Duration InitialTimeoutDuration = Duration.FromMinutes(2);
        // twitch does not allow timeouts beyond 2 weeks
        private static readonly Duration MaxTimeoutDuration = Duration.FromDays(14) - Duration.FromSeconds(1);

        private readonly int _freeTimeouts;
        private readonly float _pointsDecayPerSecond;
        private readonly int _minPoints;
        private readonly int _pointsForTimeout;

        private readonly Dictionary<User, PointStore> _pointsPerUser = new();

        public Moderator(
            ILogger<Moderator> logger,
            IExecutor executor,
            IImmutableList<IModerationRule> rules,
            IModLogRepo modLogRepo,
            IClock clock,
            int freeTimeouts = 2,
            float pointsDecayPerSecond = 1f,
            int minPoints = 20,
            int pointsForTimeout = 300)
        {
            _logger = logger;
            _executor = executor;
            _modLogRepo = modLogRepo;
            _clock = clock;
            _rules = rules;
            _freeTimeouts = freeTimeouts;
            _pointsDecayPerSecond = pointsDecayPerSecond;
            _minPoints = minPoints;
            _pointsForTimeout = pointsForTimeout;
        }

        private RuleResult ApplyPoints(User user, int points)
        {
            if (points < _minPoints)
            {
                _logger.LogDebug($"Ignoring {points} being issues to {user}, because the minimum is {_minPoints}.");
                return new RuleResult.Nothing();
            }

            // clean up expired entries, so we don't leak memory
            List<User> expiredEntries = _pointsPerUser
                .Where(kvp => kvp.Value.IsEmpty())
                .Select(kvp => kvp.Key)
                .ToList();
            expiredEntries.ForEach(u => _pointsPerUser.Remove(u));

            if (!_pointsPerUser.TryGetValue(user, out PointStore? store))
            {
                store = new PointStore(_clock, _pointsDecayPerSecond);
                _pointsPerUser[user] = store;
            }

            store.AddPoints(points);
            int currentPoints = store.GetCurrentPoints();

            if (currentPoints >= _pointsForTimeout)
            {
                _pointsPerUser.Remove(user);
                return new RuleResult.Timeout("You have accumulated too many points through various methods of spam.");
            }

            return new RuleResult.Nothing();
        }

        public async Task<bool> Check(Message message)
        {
            bool deleteMessage = false;
            (RuleResult.Timeout, IModerationRule)? timeoutAndRule = null;

            List<(RuleResult, IModerationRule)> pointResults = new();

            void ProcessResult(RuleResult result, IModerationRule rule)
            {
                if (result is RuleResult.GivePoints givePoints)
                    pointResults.Add((ApplyPoints(message.User, givePoints.Points), rule));
                else if (result is RuleResult.DeleteMessage)
                    deleteMessage = true;
                else if (result is RuleResult.Timeout resultTimeout)
                    timeoutAndRule = (resultTimeout, rule);
                else
                    _logger.LogWarning($"unhandled moderator rule result type '{result.GetType()}'");
            }

            foreach (IModerationRule? rule in _rules)
            {
                RuleResult result = rule.Check(message);
                ProcessResult(result, rule);
            }
            while (pointResults.Any())
            {
                (RuleResult result, IModerationRule rule) = pointResults.First();
                pointResults.RemoveAt(0);
                ProcessResult(result, rule);
            }

            if (timeoutAndRule.HasValue)
            {
                (RuleResult.Timeout timeout, IModerationRule rule) = timeoutAndRule.Value;
                Duration timeoutDuration = await CalculateTimeoutDuration(message.User);
                await _executor.Timeout(message.User,
                    "Your message was timed out for the following reason: " + timeout.Message, timeoutDuration);
                await _modLogRepo.LogModAction(
                    message.User, timeout.Message, rule.Id, _clock.GetCurrentInstant());
                return false;
            }
            else if (deleteMessage)
            {
                if (message.Details.MessageId != null)
                    await _executor.DeleteMessage(message.Details.MessageId);
                else
                    // Regular messages should always have an id. Whispers don't, but shouldn't be checked by modbot.
                    _logger.LogWarning($"Modbot cannot delete message because it's missing a message id: {message}");
                return false;
            }
            return true;
        }

        private async Task<Duration> CalculateTimeoutDuration(User user)
        {
            Instant cutoff = _clock.GetCurrentInstant() - RecentTimeoutsLimit;
            long recentBans = await _modLogRepo.CountRecentBans(user, cutoff);

            Duration duration = InitialTimeoutDuration;
            long increases = Math.Max(0, recentBans - _freeTimeouts);
            duration *= increases + 1;
            if (duration > MaxTimeoutDuration) duration = MaxTimeoutDuration;

            return duration;
        }
    }
}
