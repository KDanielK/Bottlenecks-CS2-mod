using System.Collections.Generic;
using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace Bottlenecks
{
    [FileLocation(nameof(Bottlenecks))]
    [SettingsUIGroupOrder(kGroup, kCargoGroup)]
    [SettingsUIShowGroupName(kGroup, kCargoGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kGroup = "Deadlocks";
        public const string kCargoGroup = "CargoStations";

        public Setting(IMod mod)
            : base(mod)
        {
        }

        [SettingsUISection(kSection, kGroup)]
        public bool HoldDeadlockedTrains { get; set; } = true;

        [SettingsUISection(kSection, kGroup)]
        public bool PauseOnDeadlock { get; set; } = false;

        [SettingsUISection(kSection, kGroup)]
        public bool ShowNotifications { get; set; } = true;

        [SettingsUISection(kSection, kCargoGroup)]
        public bool ShowCargoStationIcons { get; set; } = true;

        [SettingsUISection(kSection, kCargoGroup)]
        public bool BulkFreightOptimisation { get; set; } = false;

        // Recover from a cargo backlog: write-only button property, not saved.
        [SettingsUISection(kSection, kCargoGroup)]
        [SettingsUIButton]
        [SettingsUIConfirmation(null, null)]
        public bool ResetCargoTransfers
        {
            set { Systems.CargoQueueTestSystem.RequestReset(); }
        }

        public override void SetDefaults()
        {
            HoldDeadlockedTrains = true;
            PauseOnDeadlock = false;
            ShowNotifications = true;
            ShowCargoStationIcons = true;
            BulkFreightOptimisation = false;
        }
    }

    public class LocaleEN : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleEN(Setting setting)
        {
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "Bottlenecks" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Main" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kGroup), "Deadlocks" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.HoldDeadlockedTrains)), "Hold deadlocked trains" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.HoldDeadlockedTrains)), "Keep trains that are stuck in a deadlock on the track (frozen with a red icon) instead of letting the game despawn them, so you can see and fix the rail layout. Remove them yourself or fix the layout to free them." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.PauseOnDeadlock)), "Pause on deadlock" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.PauseOnDeadlock)), "Pause the game when a new train deadlock is detected." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ShowNotifications)), "Show notifications" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ShowNotifications)), "Show a notification card when a train deadlock is detected. Click it to move the camera there." },

                { m_Setting.GetOptionGroupLocaleID(Setting.kCargoGroup), "Cargo stations" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ShowCargoStationIcons)), "Show cargo station icons" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ShowCargoStationIcons)), "Show an icon on cargo stations, harbors and cargo airports whose truck queue is too long: blue when it has stopped selling to companies for a while, orange when it is falling behind, red when it has a runaway backlog." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.BulkFreightOptimisation)), "Bulk freight optimisation" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.BulkFreightOptimisation)), "Frees up the trucks at your cargo stations, harbors and cargo airports. These stations pass large amounts of bulk cargo to each other, and the game often sends it by truck, even between stations that one of your cargo lines connects. Their trucks then spend their time driving goods alongside your tracks and shipping lanes, and the truck queues grow. With this on, cargo between two stations goes by the cargo line that connects them (train, ship or airplane). The trucks are then free for the trips only they can make: to and from companies, warehouses and container yards. Shorter truck queues help stations keep selling to companies and keep mail moving. Applies to newly planned transfers; 'Reset cargo transfers' re-plans an existing backlog right away. Make sure busy lines have enough vehicles." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ResetCargoTransfers)), "Reset cargo transfers" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ResetCargoTransfers)), "Clears the queued truck trips at all cargo stations, harbors and cargo airports, and the pending transfer requests at all storage buildings, so a backlog (for example after a traffic jam) does not block your supply chain and mail. Storage buildings then plan new transfers from their actual stock. Goods in queued trips are lost; vehicles already on the road continue. Save first." },
                { "Bottlenecks.BulkFreightTip.Title", "Cargo transfers reset" },
                { "Bottlenecks.BulkFreightTip.Message", "Your stations are now planning their transfers again. Bulk freight optimisation is off, so much of the cargo between your cargo stations, harbors and cargo airports will go by truck again, even where a cargo line connects them. Turn it on to send that cargo by train, ship or airplane instead. This frees up the stations' trucks for companies, warehouses and container yards, and helps keep a new backlog from building up. You can change it later under Options > Bottlenecks." },
                { "Bottlenecks.BulkFreightTip.TurnOn", "Turn it on" },
                { "Bottlenecks.BulkFreightTip.NotNow", "Not now" },
                { m_Setting.GetOptionWarningLocaleID(nameof(Setting.ResetCargoTransfers)), "Reset all cargo transfers? Queued truck trips and pending transfer requests are cleared, and goods in queued trips are lost. Make sure you have a backup save." },


                { "Notifications.TITLE[Bottlenecks.TrainBlocked]", "Train blocked" },
                { "Notifications.DESCRIPTION[Bottlenecks.TrainBlocked]", "This train has been held at a signal, junction or crossing for a long time. Check the track layout ahead of it." },
                { "Notifications.TITLE[Bottlenecks.TrainQueued]", "Train waiting for a platform" },
                { "Notifications.DESCRIPTION[Bottlenecks.TrainQueued]", "This train has been waiting a while for a free station platform. The station may be close to capacity." },
                { "Notifications.TITLE[Bottlenecks.TrainDespawned]", "Train deadlock" },
                { "Notifications.DESCRIPTION[Bottlenecks.TrainDespawned]", "Trains are blocking each other here. With 'Hold deadlocked trains' on they stay until you fix the track; otherwise the game removes them." },
                { "Notifications.TITLE[Bottlenecks.CargoNotSelling]", "Cargo station not selling" },
                { "Notifications.DESCRIPTION[Bottlenecks.CargoNotSelling]", "Not critical. This station has had 10 or more truck trips queued for a while, so companies buy elsewhere for now, often by importing. This is common at busy stations. If it happens a lot, shorter truck routes, more rail connections or industry closer to the station help. Bulk freight optimisation (Options > Bottlenecks) frees up station trucks by sending cargo between connected stations by cargo line." },
                { "Notifications.TITLE[Bottlenecks.CargoOverloaded]", "Cargo station overloaded" },
                { "Notifications.DESCRIPTION[Bottlenecks.CargoOverloaded]", "This station's trucks are falling behind: its queue is several rounds long and growing. Check the roads around it for congestion, connect it to other stations by cargo rail line, and make sure it has all its workers and trucks. Bulk freight optimisation (Options > Bottlenecks) frees up station trucks by sending cargo between connected stations by cargo line." },
                { "Notifications.TITLE[Bottlenecks.CargoBacklogged]", "Cargo station backlogged" },
                { "Notifications.DESCRIPTION[Bottlenecks.CargoBacklogged]", "This station has a runaway truck backlog that will not clear by itself. It has stopped selling and also delays mail to and from the outside. Fix the cause first (for example a traffic jam), then clear it under Options > Bottlenecks > Reset cargo transfers. Save first. Bulk freight optimisation on the same page frees up station trucks by sending cargo between connected stations by cargo line." },
            };
        }

        public void Unload()
        {
        }
    }
}
