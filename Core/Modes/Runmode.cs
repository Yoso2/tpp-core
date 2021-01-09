using System;
using System.Threading.Tasks;
using Core.Commands;
using Core.Commands.Definitions;
using Core.Configuration;
using Core.Overlay;
using Inputting;
using Inputting.Parsing;
using Microsoft.Extensions.Logging;

namespace Core.Modes
{
    public sealed class Runmode : IMode, IDisposable
    {
        private readonly ILogger<Runmode> _logger;

        private IInputParser _inputParser;
        private readonly InputServer _inputServer;
        private readonly WebsocketBroadcastServer _broadcastServer;
        private AnarchyInputFeed _anarchyInputFeed;
        private readonly OverlayConnection _overlayConnection;
        private readonly InputBufferQueue<QueuedInput> _inputBufferQueue;

        private readonly StopToken _stopToken;
        private readonly ModeBase _modeBase;

        public Runmode(ILoggerFactory loggerFactory, BaseConfig baseConfig, Func<RunmodeConfig> configLoader)
        {
            RunmodeConfig runmodeConfig = configLoader();
            _logger = loggerFactory.CreateLogger<Runmode>();
            _stopToken = new StopToken();
            _modeBase = new ModeBase(loggerFactory, baseConfig, _stopToken);
            _modeBase.ChatMessageReceived += OnMessageReceived;
            _modeBase.InstallAdditionalCommand(new Command("reloadinputconfig", ctx =>
            {
                ReloadConfig(configLoader().InputConfig);
                return Task.FromResult(new CommandResult { Response = "input config reloaded" });
            }));

            _broadcastServer = new WebsocketBroadcastServer(
                loggerFactory.CreateLogger<WebsocketBroadcastServer>(), "localhost", 5001);
            _overlayConnection =
                new OverlayConnection(loggerFactory.CreateLogger<OverlayConnection>(), _broadcastServer);

            // TODO felk: this feels a bit messy the way it is done right now,
            //            but I am unsure yet how I'd integrate the individual parts in a cleaner way.
            InputConfig inputConfig = runmodeConfig.InputConfig;
            _inputParser = inputConfig.ButtonsProfile.ToInputParser();
            _inputBufferQueue = new InputBufferQueue<QueuedInput>(CreateBufferConfig(inputConfig));
            _anarchyInputFeed = CreateInputFeedFromConfig(inputConfig);
            _inputServer = new InputServer(loggerFactory.CreateLogger<InputServer>(),
                runmodeConfig.InputServerHost, runmodeConfig.InputServerPort,
                _anarchyInputFeed);
        }

        private AnarchyInputFeed CreateInputFeedFromConfig(InputConfig config)
        {
            IInputMapper inputMapper = CreateInputMapperFromConfig(config);
            IInputHoldTiming inputHoldTiming = CreateInputHoldTimingFromConfig(config);
            _inputBufferQueue.SetNewConfig(CreateBufferConfig(config));

            return new AnarchyInputFeed(
                _overlayConnection,
                inputHoldTiming,
                inputMapper,
                _inputBufferQueue,
                config.FramesPerSecond);
        }

        private static IInputMapper CreateInputMapperFromConfig(InputConfig config) =>
            new DefaultTppInputMapper(config.FramesPerSecond);

        private static IInputHoldTiming CreateInputHoldTimingFromConfig(InputConfig config) =>
            new DefaultInputHoldTiming(
                minSleepDuration: config.MinSleepFrames / (float)config.FramesPerSecond,
                minPressDuration: config.MinPressFrames / (float)config.FramesPerSecond,
                maxPressDuration: config.MaxPressFrames / (float)config.FramesPerSecond,
                maxHoldDuration: config.MaxHoldFrames / (float)config.FramesPerSecond);

        private static InputBufferQueue<QueuedInput>.Config CreateBufferConfig(InputConfig config) =>
            new(BufferLengthSeconds: config.BufferLengthSeconds,
                SpeedupRate: config.SpeedupRate,
                SlowdownRate: config.SlowdownRate,
                MinInputDuration: config.MinInputFrames / (float)config.FramesPerSecond,
                MaxInputDuration: config.MaxInputFrames / (float)config.FramesPerSecond,
                MaxBufferLength: config.MaxBufferLength);

        private void ReloadConfig(InputConfig config)
        {
            // TODO endpoints to control configs at runtime?
            _inputParser = config.ButtonsProfile.ToInputParser();
            _anarchyInputFeed = CreateInputFeedFromConfig(config);
            _inputServer.InputFeed = _anarchyInputFeed;
        }

        private async void OnMessageReceived(object? sender, Message message)
        {
            if (message.MessageSource != MessageSource.Chat) return;
            string potentialInput = message.MessageText.Split(' ', count: 2)[0];
            InputSequence? input = _inputParser.Parse(potentialInput);
            if (input != null)
                foreach (InputSet inputSet in input.InputSets)
                    await _anarchyInputFeed.Enqueue(inputSet, message.User);
        }

        public async Task Run()
        {
            _logger.LogInformation("Runmode starting");
            Task overlayWebsocketTask = _broadcastServer.Listen();
            Task inputServerTask = _inputServer.Listen();
            _modeBase.Start();
            while (!_stopToken.ShouldStop)
            {
                // TODO run main loop goes here
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
            _inputServer.Stop();
            await inputServerTask;
            await _broadcastServer.Stop();
            await overlayWebsocketTask;
            _logger.LogInformation("Runmode ended");
        }

        public void Cancel()
        {
            // once the mainloop is not just busylooping, this needs to be replaced with something
            // that makes the mode stop immediately
            _stopToken.ShouldStop = true;
        }

        public void Dispose()
        {
            _modeBase.ChatMessageReceived -= OnMessageReceived;
            _modeBase.Dispose();
        }
    }
}
