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

        // Developer switch, not exposed in the options. Set to true and rebuild to log [Diag] lines about once a
        // minute: post facilities, cargo stations, cargo lines, captured storage transfer routes (truck hops between
        // stations with a truck/line cost comparison and line layouts), and the [Rail] summary of bulk freight
        // optimisation with samples of the truck hops it kept. Useful after a game update to re-check cargo routing.
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
            updateSystem.UpdateBefore<RailFreightRerouteSystem, StorageTransferSystem>(SystemUpdatePhase.GameSimulation);
            if (EnableDiagnostics)
            {
                updateSystem.UpdateAt<PostDiagnosticsSystem>(SystemUpdatePhase.GameSimulation);
                updateSystem.UpdateAt<CargoDiagnosticsSystem>(SystemUpdatePhase.GameSimulation);
                updateSystem.UpdateBefore<RouteCaptureDiagnosticsSystem, StorageTransferSystem>(SystemUpdatePhase.GameSimulation);
            }
            // MainLoop also runs while the game is paused (e.g. with the options open), so the reset button acts at once.
            updateSystem.UpdateAt<CargoQueueTestSystem>(SystemUpdatePhase.MainLoop);
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
