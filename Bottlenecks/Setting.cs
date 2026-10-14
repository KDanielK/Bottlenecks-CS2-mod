using System.Collections.Generic;
using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace Bottlenecks
{
    [FileLocation(nameof(Bottlenecks))]
    [SettingsUIGroupOrder(kGroup)]
    [SettingsUIShowGroupName(kGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kGroup = "Deadlocks";

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

        public override void SetDefaults()
        {
            HoldDeadlockedTrains = true;
            PauseOnDeadlock = false;
            ShowNotifications = true;
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
            };
        }

        public void Unload()
        {
        }
    }
}
