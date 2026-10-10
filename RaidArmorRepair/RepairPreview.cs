using EFT.InventoryLogic;

namespace RaidArmorRepair
{
    /// <summary>What a repair tick would do, worked out without actually doing it. Drives
    /// the on-screen progress panel.</summary>
    public struct RepairPreview
    {
        public bool HasTarget;
        public bool HasKit;

        public Item TargetArmor;
        public ArmorComponent TargetComponent;

        /// <summary>Human-readable name of what is being repaired, e.g. "6B13 앞판(SAPI)".</summary>
        public string TargetLabel;

        /// <summary>Everything worn, 0-100 — what the one-line progress shows.</summary>
        public int OverallPercent;
        public Item KitItem;
        public RepairKitComponent KitComponent;

        public float MaxDurability;
        public float CurrentDurability;
        public float MissingDurability;

        /// <summary>Durability this tick would actually restore, after every cap.</summary>
        public float ExpectedHeal;

        /// <summary>True when the kit's remaining resource, not the armour's missing
        /// durability, is what limits this tick.</summary>
        public bool LimitedByKit;

        public float ExpectedMaxDurabilityLossPercent;
    }
}
