using Bottlenecks.Systems;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Simulation;

namespace Bottlenecks
{
    public class Mod : IMod
    {
        public static ILog log = LogManager.GetLogger($"{nameof(Bottlenecks)}.{nameof(Mod)}").SetShowsErrorsInUI(false);

        public static Setting Settings { get; private set; }

        // Developer switch: set to true to register the post/cargo diagnostics systems, which write the state
        // of post facilities and cargo stations to the log about once a minute. Not exposed in the options.
        public static readonly bool EnableDiagnostics = false;

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
                log.Info($"Current mod asset at {asset.path}");

            Settings = new Setting(this);
            Settings.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(Settings));
            AssetDatabase.global.LoadSettings(nameof(Bottlenecks), Settings, new Setting(this));

            updateSystem.UpdateBefore<IconPrefabSetupSystem, PrefabInitializeSystem>(SystemUpdatePhase.PrefabUpdate);
            updateSystem.UpdateBefore<DeadlockHoldSystem, TransportTrainAISystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<BlockedTrainSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<TrainDespawnMarkerSystem>(SystemUpdatePhase.Modification4);
            if (EnableDiagnostics)
            {
                updateSystem.UpdateAt<PostDiagnosticsSystem>(SystemUpdatePhase.GameSimulation);
                updateSystem.UpdateAt<CargoDiagnosticsSystem>(SystemUpdatePhase.GameSimulation);
            }
            updateSystem.UpdateAt<CargoQueueTestSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<CargoStationOverloadSystem>(SystemUpdatePhase.GameSimulation);
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));

            Settings?.UnregisterInOptionsUI();
            Settings = null;
        }
    }
}
