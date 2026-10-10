#pragma warning disable 0169, 0414, 0649
namespace RaidArmorRepair
{
    /// <summary>
    /// Display hints read by BepInEx ConfigurationManager (F12). It finds this class by name
    /// among a setting's tags, so only the fields used here are declared.
    /// </summary>
    internal sealed class ConfigurationManagerAttributes
    {
        public string Category;
        public int? Order;
        public string DispName;
        public string Description;
        public bool? ShowRangeAsPercent;
        public bool? IsAdvanced;
    }
}
